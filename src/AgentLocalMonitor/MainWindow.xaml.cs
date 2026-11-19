using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;

namespace AgentLocalMonitor;

public partial class MainWindow : Window
{
    private const int PageSize = 8;
    private Process? _monitorProcess;
    private CancellationTokenSource? _streamCancellation;
    private Task? _streamTask;
    private readonly ObservableCollection<TaskRow> _activeView = new();
    private readonly ObservableCollection<TaskRow> _historyView = new();
    private readonly ObservableCollection<EventRow> _eventView = new();
    private readonly List<TaskRow> _active = new();
    private readonly List<TaskRow> _history = new();
    private readonly List<EventRow> _events = new();
    private readonly Dictionary<string, TaskRow> _previous = new();
    private readonly Forms.NotifyIcon _tray = new();
    private readonly string _dataDir;
    private readonly string _eventLog;
    private readonly string _projectsFile;
    private readonly HashSet<string> _manualProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _python;
    private readonly string _monitorScript;
    private TaskRow? _selected;
    private string _tab = "live";
    private int _page = 1;
    private bool _paused;
    private bool _allowClose;
    private bool _trayReady;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        ActiveView = _activeView;
        HistoryView = _historyView;
        EventView = _eventView;
        var appRoot = FindAppRoot();
        _monitorScript = Path.Combine(appRoot, "monitor", "codex_monitor.py");
        _python = FindPython();
        var args = Environment.GetCommandLineArgs();
        var dataDirIndex = Array.IndexOf(args, "--data-dir");
        _dataDir = dataDirIndex >= 0 && dataDirIndex + 1 < args.Length
            ? Path.GetFullPath(args[dataDirIndex + 1])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentLocalMonitor");
        Directory.CreateDirectory(_dataDir);
        _eventLog = Path.Combine(_dataDir, "events.jsonl");
        _projectsFile = Path.Combine(_dataDir, "projects.json");
        LoadProjectFolders();
        LoadEvents();
        SetupTray();
        Loaded += (_, _) => { PositionInLowerHalf(); StartMonitorStream(); };
        IntervalBox.SelectedIndex = 0;
    }

    public ObservableCollection<TaskRow> ActiveView { get; }
    public ObservableCollection<TaskRow> HistoryView { get; }
    public ObservableCollection<EventRow> EventView { get; }

    private static string FindAppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "monitor", "codex_monitor.py"))) return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static string FindPython()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("AGENT_MONITOR_PYTHON"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python311", "python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python", "Python312", "python.exe"),
            "python.exe"
        };
        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && (p == "python.exe" || File.Exists(p))) ?? "python.exe";
    }

    private void PositionInLowerHalf()
    {
        WindowPlacement.PlaceInLowerHalf(this, _dataDir, useCurrentMonitor: false);
    }

    private void PositionButton_Click(object sender, RoutedEventArgs e)
        => WindowPlacement.PlaceInLowerHalf(this, _dataDir, useCurrentMonitor: true);

    private void TopmostButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        TopmostButton.Content = Topmost ? "最前面 ON" : "最前面 OFF";
    }

    private void LoadProjectFolders()
    {
        try
        {
            if (File.Exists(_projectsFile))
                foreach (var path in JsonSerializer.Deserialize<string[]>(File.ReadAllText(_projectsFile)) ?? [])
                    if (Directory.Exists(path)) _manualProjects.Add(Path.GetFullPath(path));
        }
        catch (Exception ex) { Debug.WriteLine($"Project folder load failed: {ex.Message}"); }
    }

    private void AddProjectButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "監視対象候補に表示するプロジェクトフォルダを選択", ShowNewFolderButton = false };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || !Directory.Exists(dialog.SelectedPath)) return;
        var path = Path.GetFullPath(dialog.SelectedPath);
        _manualProjects.Add(path);
        try { File.WriteAllText(_projectsFile, JsonSerializer.Serialize(_manualProjects.OrderBy(p => p).ToArray())); }
        catch (Exception ex) { MessageBox.Show(this, $"フォルダ候補を保存できませんでした: {ex.Message}", "保存エラー", MessageBoxButton.OK, MessageBoxImage.Warning); }
        RebuildProjectFilter(path);
        RenderCurrentTab();
    }

    private void SetupTray()
    {
        _tray.Icon = Drawing.SystemIcons.Application;
        _tray.Text = "Agent Local Monitor";
        _tray.Visible = true;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("ウィンドウを表示", null, (_, _) => ShowWindow());
        menu.Items.Add("監視を一時停止 / 再開", null, (_, _) => TogglePause());
        var startup = new Forms.ToolStripMenuItem("Windowsログオン時に起動") { Checked = IsStartupEnabled, CheckOnClick = false };
        startup.Click += (_, _) => { SetStartup(!IsStartupEnabled); startup.Checked = IsStartupEnabled; };
        menu.Items.Add(startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApplication());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();
        _trayReady = true;
    }

    private static string StartupKey => @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static bool IsStartupEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(StartupKey); return key?.GetValue("AgentLocalMonitor") is not null; }
    }

    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StartupKey);
        if (enabled) key.SetValue("AgentLocalMonitor", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("AgentLocalMonitor", false);
    }

    private int PollSeconds => IntervalBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var n) ? n : 5;

    private void StartMonitorStream()
    {
        if (_paused || (_streamTask is { IsCompleted: false })) return;
        _streamCancellation = new CancellationTokenSource();
        _streamTask = StreamSnapshotsAsync(_streamCancellation.Token);
    }

    private async Task StreamSnapshotsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var psi = new ProcessStartInfo(_python)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(_monitorScript)!,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                psi.Environment["PYTHONIOENCODING"] = "utf-8";
                psi.ArgumentList.Add("-B"); psi.ArgumentList.Add(_monitorScript); psi.ArgumentList.Add("watch-json");
                psi.ArgumentList.Add("--interval"); psi.ArgumentList.Add(PollSeconds.ToString(CultureInfo.InvariantCulture));
                using var process = Process.Start(psi) ?? throw new InvalidOperationException("Python監視プロセスを起動できません");
                _monitorProcess = process;
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                    if (line is null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    AppendWorkerLog("snapshot line received chars=" + line.Length);
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        await Dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                ApplySnapshot(doc.RootElement);
                                LastRefresh.Text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                                Footer.Text = "ローカル監視 · 外部通信なし · AI呼び出し 0";
                            }
                            catch (Exception ex) { AppendWorkerLog("apply snapshot failed: " + ex); Footer.Text = "表示エラー: " + ex.Message; }
                        });
                    }
                    catch (Exception ex)
                    {
                        try { File.WriteAllText(Path.Combine(_dataDir, "last-snapshot-invalid.json"), line); } catch { }
                        AppendWorkerLog("parse/dispatch failed: " + ex);
                    }
                }
                await process.WaitForExitAsync(cancellationToken);
                var stderr = await stderrTask;
                if (!cancellationToken.IsCancellationRequested && process.ExitCode != 0)
                    await Dispatcher.InvokeAsync(() => Footer.Text = "監視プロセス終了: " + stderr.Trim());
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { AppendWorkerLog("monitor stream failed: " + ex); await Dispatcher.InvokeAsync(() => Footer.Text = "監視エラー: " + ex.Message); }
            finally { _monitorProcess = null; }
            try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void AppendWorkerLog(string message)
    {
        try { File.AppendAllText(Path.Combine(_dataDir, "worker.log"), DateTime.Now.ToString("o") + " " + message + Environment.NewLine); } catch { }
    }

    private void StopMonitorStream()
    {
        try { _streamCancellation?.Cancel(); } catch { }
        try { if (_monitorProcess is { HasExited: false }) _monitorProcess.Kill(true); } catch { }
        _streamCancellation?.Dispose(); _streamCancellation = null; _streamTask = null;
    }

    private string ReadSnapshot()
    {
        if (!File.Exists(_monitorScript)) throw new FileNotFoundException("監視スクリプトが見つかりません", _monitorScript);
        var psi = new ProcessStartInfo(_python)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(_monitorScript)!,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.ArgumentList.Add("-B");
        psi.ArgumentList.Add(_monitorScript);
        psi.ArgumentList.Add("snapshot");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Python監視プロセスを起動できません");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { try { process.Kill(true); } catch { } throw new TimeoutException("監視スナップショットが15秒以内に完了しませんでした"); }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr.Result) ? "監視スクリプトが失敗しました" : stderr.Result.Trim());
        return stdout.Result;
    }

    private void ApplySnapshot(JsonElement root)
    {
        var tasks = ReadArray(root, "tasks").Select(ParseTask).Where(x => x is not null).Cast<TaskRow>().ToList();
        var history = ReadArray(root, "history").Select(ParseTask).Where(x => x is not null).Cast<TaskRow>().ToList();
        var infrastructure = ReadArray(root, "infrastructure").ToList();
        var current = tasks.ToDictionary(x => x.Key, x => x, StringComparer.OrdinalIgnoreCase);

        foreach (var old in _previous)
        {
            if (current.ContainsKey(old.Key)) continue;
            var recorded = history.FirstOrDefault(x => x.Key == old.Key);
            if (recorded is null)
                AddEvent("プロセス終了・結果未確認", $"{old.Value.ProviderLabel} / PID {old.Value.Pid} のプロセスが一覧から消えました。正常完了は確認できていません。", old.Value.Project);
        }
        foreach (var item in tasks)
        {
            if (!_previous.ContainsKey(item.Key)) AddEvent("新規検出", $"{item.ProviderLabel} / {item.TaskSummary}", item.Project);
            if (item.LatestEvent is not null) AddEvent(item.LatestEvent, item.TaskSummary, item.Project, dedupe: true);
        }
        _previous.Clear(); foreach (var pair in current) _previous[pair.Key] = pair.Value;

        _active.Clear(); _active.AddRange(tasks.OrderBy(x => x.ProviderLabel).ThenBy(x => x.StartedAt));
        _history.Clear(); _history.AddRange(history.OrderByDescending(x => x.SortTime));
        RebuildProjectFilter();
        RefreshCounts(root, tasks, history);
        RenderCurrentTab();
        if (_selected is not null)
        {
            var refreshed = tasks.Concat(history).FirstOrDefault(x => x.Key == _selected.Key);
            if (refreshed is not null) { _selected = refreshed; RenderDetails(refreshed); }
        }
        else if (tasks.Count > 0)
        {
            _selected = tasks[0]; RenderDetails(_selected);
        }
        InfraCount.Text = $"補助 {infrastructure.Count}";
    }

    private static IEnumerable<JsonElement> ReadArray(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : Array.Empty<JsonElement>();

    private static string GetString(JsonElement e, string name, string fallback = "")
        => e.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.ToString() : fallback;

    private static int GetInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.TryGetInt32(out var value) ? value : 0;

    private static TaskRow? ParseTask(JsonElement e)
    {
        var pid = GetInt(e, "pid");
        var run = GetString(e, "run_id");
        var classification = GetString(e, "classification");
        var provider = GetString(e, "provider");
        if (string.IsNullOrEmpty(provider)) provider = classification.Contains("claude") ? "claude" : classification.Contains("agy") ? "agy" : "codex";
        var repo = GetString(e, "repo");
        var summary = GetString(e, "task_summary");
        if (string.IsNullOrWhiteSpace(summary)) summary = GetString(e, "task");
        if (string.IsNullOrWhiteSpace(summary)) summary = classification == "infrastructure" ? "補助プロセス" : "内容未判別";
        var model = GetString(e, "model", "不明");
        var apiModel = GetString(e, "last_api_model", "記録なし");
        var effective = GetString(e, "effective_model", "未報告");
        var status = GetString(e, "status", "unknown");
        var role = GetString(e, "role");
        var latest = "";
        if (e.TryGetProperty("latest_event", out var latestEl) && latestEl.ValueKind == JsonValueKind.Object)
            latest = GetString(latestEl, "type") + " " + GetString(latestEl, "detail");
        var started = GetString(e, "started_at");
        var created = GetString(e, "created_at");
        var ended = GetString(e, "ended_at");
        return new TaskRow
        {
            Key = string.IsNullOrEmpty(run) ? $"{provider}:{pid}" : run,
            RunId = run, Pid = pid == 0 ? "—" : pid.ToString(CultureInfo.InvariantCulture),
            Provider = provider.ToLowerInvariant(), ProviderLabel = ProviderName(provider),
            Role = role, Project = ProjectName(repo, GetString(e, "repo")), Repo = repo,
            Tree = Path.GetFileName(repo.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            TaskSummary = summary, Task = GetString(e, "task"),
            Model = model, ApiModel = apiModel, EffectiveModel = effective,
            Effort = GetString(e, "effort", "不明"), ApiEffort = GetString(e, "last_api_effort", "記録なし"),
            EffectiveEffort = GetString(e, "effective_effort", "未報告"),
            Status = status, StatusLabel = StatusName(status, role),
            StatusBrush = StatusColor(status), ProviderBrush = ProviderColor(provider),
            CreatedAt = created, StartedAt = started, ProcessStartedAt = GetString(e, "process_started_at"), EndedAt = ended,
            Created = FormatDate(created), Started = FormatDate(started), Ended = FormatDate(ended),
            Elapsed = FormatElapsed(started, ended), SortTime = ParseDate(ended) ?? ParseDate(started) ?? ParseDate(created) ?? DateTime.MinValue,
            LatestEvent = latest, Brief = GetString(e, "task"), Result = GetString(e, "result"),
            Receipt = GetString(e, "receipt"), Transcript = GetString(e, "transcript"),
            ProjectTree = $"{ProjectName(repo, repo)} / {Path.GetFileName(repo.TrimEnd('\\', '/'))}",
            PidRun = $"PID { (pid == 0 ? "—" : pid.ToString()) } · { (string.IsNullOrEmpty(run) ? "unmanaged" : run) }",
            ModelEffort = $"{ShortModel(model)} · {GetString(e, "effort", "?")}",
            CardBackground = MediaBrushes.Transparent
        };
    }

    private static string ProviderName(string p) => p.ToLowerInvariant() switch { "claude" => "Claude", "codex" => "Codex", "agy" => "AGY", _ => p };
    private static MediaBrush ProviderColor(string p) => p.ToLowerInvariant() switch { "claude" => Brush("#EAB593"), "agy" => Brush("#B8A8FF"), _ => Brush("#77DBBA") };
    private static MediaBrush StatusColor(string s) => s switch
    {
        "running" or "running_unmanaged" => Brush("#77DBBA"),
        "request_in_flight_or_unconfirmed" => Brush("#89B9FF"),
        "idle_after_last_response" or "idle_no_recent_api_evidence" => Brush("#E7B87C"),
        "completed" => Brush("#AEB DCA".Replace(" ", "")),
        "failed" or "error" => Brush("#FF9D9D"), _ => Brush("#9BA8B5")
    };
    private static MediaBrush Brush(string hex) => new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
    private static string StatusName(string status, string role) => status switch
    {
        "running" or "running_unmanaged" => role == "review" ? "◇ Reviewing" : "● Working",
        "request_in_flight_or_unconfirmed" => "◌ Thinking",
        "idle_after_last_response" => "Ⅱ 応答後待機",
        "idle_no_recent_api_evidence" => "Ⅱ API記録なし",
        "process_detected_session_unresolved" => "? セッション未解決",
        "process_detected_task_unresolved" => "? タスク未判別",
        "completed" => "✓ Completed", "failed" => "! Failed", _ => status
    };
    private static string ProjectName(string repo, string fallback)
    {
        if (string.IsNullOrWhiteSpace(repo)) return "不明";
        var path = repo.TrimEnd('\\', '/');
        var name = Path.GetFileName(path);
        if (name.StartsWith("wt-", StringComparison.OrdinalIgnoreCase))
        {
            var parentInfo = Directory.GetParent(path);
            var parent = parentInfo?.Name;
            if (parent is "worktree" or "worktrees" or "trees") parent = parentInfo?.Parent?.Name;
            return string.IsNullOrWhiteSpace(parent) ? name : parent;
        }
        return name;
    }
    private static string ShortModel(string model) => model.Replace("gpt-6.1-", "").Replace("gpt-6.2-", "").Replace("claude-", "");
    private static DateTime? ParseDate(string value) => DateTimeOffset.TryParse(value, out var date) ? date.LocalDateTime : null;
    private static string FormatDate(string value) => ParseDate(value)?.ToString("MM/dd HH:mm:ss") ?? "未記録";
    private static string FormatElapsed(string start, string end)
    {
        var s = ParseDate(start); if (s is null) return "—";
        var e = ParseDate(end) ?? DateTime.Now; var span = e - s.Value;
        if (span.TotalSeconds < 0) return "時刻不整合";
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:D2}m" : $"{span.Minutes}m {span.Seconds:D2}s";
    }

    private void RefreshCounts(JsonElement root, List<TaskRow> tasks, List<TaskRow> history)
    {
        ActiveCount.Text = tasks.Count.ToString(CultureInfo.InvariantCulture);
        ReviewCount.Text = tasks.Count(x => x.Role == "review" || x.StatusLabel.Contains("Reviewing")).ToString(CultureInfo.InvariantCulture);
        var today = DateTime.Today;
        CompletedCount.Text = history.Count(x => x.Status == "completed" && x.SortTime.Date == today).ToString(CultureInfo.InvariantCulture);
        AttentionCount.Text = tasks.Count(x => x.Status.Contains("unknown") || x.Status.Contains("failed") || x.EffectiveModel == "未報告").ToString(CultureInfo.InvariantCulture);
    }

    private void RebuildProjectFilter(string? selectedPath = null)
    {
        var current = selectedPath ?? (ProjectFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        ProjectFilter.SelectionChanged -= Filter_Changed;
        ProjectFilter.Items.Clear(); ProjectFilter.Items.Add(new ComboBoxItem { Content = "全プロジェクト", Tag = "" });
        var knownPaths = _active.Concat(_history).Select(x => x.Repo)
            .Concat(_manualProjects);
        var folderRoot = @"S:\Projects";
        try
        {
            if (Directory.Exists(folderRoot)) knownPaths = knownPaths.Concat(Directory.EnumerateDirectories(folderRoot));
        }
        catch (Exception ex) { Debug.WriteLine($"Project directory scan failed: {ex.Message}"); }
        var projectPaths = knownPaths.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.TrimEnd('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var path in projectPaths)
            ProjectFilter.Items.Add(new ComboBoxItem { Content = $"{Path.GetFileName(path)}  ·  {path}", Tag = path, ToolTip = path });
        // Preserve unknown/unresolved groups without conflating them with an actual folder.
        foreach (var project in _active.Concat(_history).Where(x => string.IsNullOrWhiteSpace(x.Repo))
                     .Select(x => x.Project).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            ProjectFilter.Items.Add(new ComboBoxItem { Content = project, Tag = "name:" + project });
        ProjectFilter.SelectedItem = ProjectFilter.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (string?)x.Tag == current) ?? ProjectFilter.Items[0];
        ProjectFilter.SelectionChanged += Filter_Changed;
    }

    private bool Matches(TaskRow x)
    {
        var project = (ProjectFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        var provider = (ProviderFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        var query = SearchBox.Text.Trim();
        return (string.IsNullOrEmpty(project) || (project.StartsWith("name:", StringComparison.Ordinal) ? x.Project.Equals(project[5..], StringComparison.OrdinalIgnoreCase)
                : x.Repo.TrimEnd('\\', '/').Equals(project, StringComparison.OrdinalIgnoreCase)
                  || x.Repo.StartsWith(project.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase)
                  || x.Repo.StartsWith(project.TrimEnd('\\', '/') + "/", StringComparison.OrdinalIgnoreCase)))
            && (string.IsNullOrEmpty(provider) || x.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrEmpty(query) || $"{x.TaskSummary} {x.Pid} {x.RunId} {x.ProjectTree} {x.Model}".Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void RenderCurrentTab()
    {
        LiveScroll.Visibility = _tab == "live" ? Visibility.Visible : Visibility.Collapsed;
        EventsScroll.Visibility = _tab == "events" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPane.Visibility = _tab == "history" ? Visibility.Visible : Visibility.Collapsed;
        var live = _active.Where(Matches).ToList();
        var history = _history.Where(Matches).ToList();
        if (FromDate.SelectedDate is not null) history = history.Where(x => x.SortTime.Date >= FromDate.SelectedDate.Value.Date).ToList();
        if (ToDate.SelectedDate is not null) history = history.Where(x => x.SortTime.Date <= ToDate.SelectedDate.Value.Date).ToList();
        _activeView.Clear(); foreach (var item in live) _activeView.Add(item);
        _eventView.Clear(); foreach (var item in _events.Take(100)) _eventView.Add(item);
        var pages = Math.Max(1, (int)Math.Ceiling(history.Count / (double)PageSize)); _page = Math.Clamp(_page, 1, pages);
        _historyView.Clear(); foreach (var item in history.Skip((_page - 1) * PageSize).Take(PageSize)) _historyView.Add(item);
        TabCount.Text = _tab switch { "live" => live.Count.ToString(), "events" => _events.Count.ToString(), _ => history.Count.ToString() };
        ListCount.Text = _tab == "live" ? $"{live.Count} 件 · 実行中" : _tab == "events" ? $"{_events.Count} 件のイベント" : $"{history.Count} 件の履歴";
        HistoryCount.Text = $"{history.Count} 件 · {(_page - 1) * PageSize + (history.Count == 0 ? 0 : 1)}–{Math.Min(_page * PageSize, history.Count)}";
        PageLabel.Text = $"{_page} / {pages}";
    }

    private void RenderDetails(TaskRow x)
    {
        KillTaskButton.IsEnabled = _active.Any(a => a.Key == x.Key && a.Pid == x.Pid) &&
                                   int.TryParse(x.Pid, out var pid) && pid > 0 && pid != Environment.ProcessId &&
                                   x.Provider is "claude" or "codex" or "agy" && !string.IsNullOrWhiteSpace(x.ProcessStartedAt);
        DetailTitle.Text = x.TaskSummary; DetailIdentity.Text = $"{x.RunId} · PID {x.Pid}";
        DetailState.Text = x.StatusLabel; DetailProject.Text = x.Project; DetailTree.Text = x.Tree;
        DetailModel.Text = x.Model; DetailApiModel.Text = x.ApiModel;
        DetailEffective.Text = x.EffectiveModel == "not_reported" ? "未報告" : x.EffectiveModel;
        DetailEffort.Text = $"{x.Effort} / API {x.ApiEffort} / 実効 {x.EffectiveEffort}";
        DetailTimeline.Text = $"投入: {x.Created}\n開始: {x.Started}\n終了: {x.Ended}\n経過: {x.Elapsed}";
        DetailBrief.Text = string.IsNullOrWhiteSpace(x.Brief) ? "依頼内容の記録なし" : x.Brief;
        DetailResult.Text = string.IsNullOrWhiteSpace(x.LatestEvent) ? (x.Status == "completed" ? "receiptで完了確認済み" : "最新イベントなし") : x.LatestEvent;
        DetailReceipt.Text = !string.IsNullOrWhiteSpace(x.Receipt) ? x.Receipt : x.Transcript;
    }

    private void AddEvent(string title, string description, string source, bool dedupe = false)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        if (dedupe && _events.Count > 0 && _events[0].Title == title && _events[0].Description == description) return;
        var item = new EventRow { Time = DateTime.Now.ToString("HH:mm:ss"), Title = title, Description = description, Source = source };
        _events.Insert(0, item); if (_events.Count > 500) _events.RemoveAt(_events.Count - 1);
        try { File.AppendAllText(_eventLog, JsonSerializer.Serialize(item) + Environment.NewLine); } catch { }
    }

    private void LoadEvents()
    {
        if (!File.Exists(_eventLog)) return;
        try
        {
            foreach (var line in File.ReadLines(_eventLog).TakeLast(500))
            {
                try { var item = JsonSerializer.Deserialize<EventRow>(line); if (item is not null) _events.Add(item); } catch { }
            }
        }
        catch { }
    }

    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void TogglePause()
    {
        _paused = !_paused; PauseButton.Content = _paused ? "▶ 再開" : "Ⅱ 停止";
        RefreshState.Text = _paused ? "Ⅱ 更新停止中" : "● 自動更新";
        if (_paused) StopMonitorStream(); else StartMonitorStream();
    }
    private void ExitApplication()
    {
        _allowClose = true; StopMonitorStream(); _tray.Visible = false; _tray.Dispose(); Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true; Hide(); _tray.ShowBalloonTip(1200, "Agent Local Monitor", "通知領域で監視を継続しています", Forms.ToolTipIcon.Info);
    }
    private void LiveTab_Click(object sender, RoutedEventArgs e) { _tab = "live"; SetTabStyles(); RenderCurrentTab(); }
    private void EventsTab_Click(object sender, RoutedEventArgs e) { _tab = "events"; SetTabStyles(); RenderCurrentTab(); }
    private void HistoryTab_Click(object sender, RoutedEventArgs e) { _tab = "history"; SetTabStyles(); RenderCurrentTab(); }
    private void SetTabStyles()
    {
        foreach (var b in new[] { LiveTab, EventsTab, HistoryTab }) { b.Background = MediaBrushes.Transparent; b.BorderThickness = new Thickness(0); b.BorderBrush = MediaBrushes.Transparent; }
        var active = _tab == "live" ? LiveTab : _tab == "events" ? EventsTab : HistoryTab;
        active.BorderThickness = new Thickness(0, 0, 0, 2); active.BorderBrush = (MediaBrush)FindResource("Mint");
    }
    private void TaskCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is TaskRow task)
        {
            _selected = task;
            RenderDetails(task);
            DetailExpander.IsExpanded = true;
        }
    }
    private void KillTaskButton_Click(object sender, RoutedEventArgs e)
    {
        var row = _selected;
        if (row is null || !KillTaskButton.IsEnabled || !int.TryParse(row.Pid, out var pid)) return;
        // No process tree kill: the selected task can be a child of an unrelated owner.
        if (!TryVerifyTarget(row, pid, out var reason, out var initialStart))
        {
            MessageBox.Show(this, reason, "終了対象を確認できません", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var prompt = $"本当にこのプロセスを終了しますか？\n\n{row.ProviderLabel}  /  PID {pid}\n{row.Project}\n{row.TaskSummary}\n\n未保存の作業が失われる可能性があります。子プロセスは終了しません。";
        if (MessageBox.Show(this, prompt, "本当に終了しますか？", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!TryVerifyTarget(row, pid, out reason, out var verifiedStart) || verifiedStart != initialStart)
        {
            MessageBox.Show(this, "確認中に対象の同一性が変わったため、中止しました。" + reason, "安全停止", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.StartTime.ToUniversalTime() != initialStart) throw new InvalidOperationException("終了直前にPIDが再利用されました");
            process.Kill(entireProcessTree: false);
            AddEvent("手動終了要求", $"{row.ProviderLabel} PID {pid}: OSへ終了要求を送信（完了は未確認）", row.Project);
            MessageBox.Show(this, $"PID {pid} に終了要求を送信しました。次回の更新で結果を確認してください。", "終了要求", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, $"終了できませんでした: {ex.Message}", "終了失敗", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private bool TryVerifyTarget(TaskRow row, int pid, out string reason, out DateTime startUtc)
    {
        reason = ""; startUtc = default;
        if (pid <= 0 || pid == Environment.ProcessId || !_active.Any(x => x.Key == row.Key && x.Pid == row.Pid && x.ProcessStartedAt == row.ProcessStartedAt))
        { reason = "監視中の選択タスクと一致しません。"; return false; }
        if (!DateTimeOffset.TryParse(row.ProcessStartedAt, out var recordedStart))
        { reason = "プロセス開始時刻を解釈できません。"; return false; }
        try
        {
            using var process = Process.GetProcessById(pid);
            var name = process.ProcessName.ToLowerInvariant();
            if (!(name.StartsWith("codex") && row.Provider == "codex" || name.StartsWith("claude") && row.Provider == "claude" || name.StartsWith("agy") && row.Provider == "agy"))
            { reason = "実行中プロセスの名前とプロバイダが一致しません。"; return false; }
            startUtc = process.StartTime.ToUniversalTime();
            if (Math.Abs((startUtc - recordedStart.UtcDateTime).TotalSeconds) > 2)
            { reason = "PIDの開始時刻が監視記録と一致しません。"; return false; }
            return true;
        }
        catch (Exception ex) { reason = "対象の同一性確認に失敗: " + ex.Message; return false; }
    }
    private void Filter_Changed(object sender, RoutedEventArgs e) { if (!IsLoaded) return; _page = 1; RenderCurrentTab(); }
    private void HistoryFilter_Changed(object sender, SelectionChangedEventArgs e) { if (!IsLoaded) return; _page = 1; RenderCurrentTab(); }
    private void PrevPage_Click(object sender, RoutedEventArgs e) { _page = Math.Max(1, _page - 1); RenderCurrentTab(); }
    private void NextPage_Click(object sender, RoutedEventArgs e) { _page++; RenderCurrentTab(); }
    private void PauseButton_Click(object sender, RoutedEventArgs e) => TogglePause();
    private void IntervalBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || IntervalBox.SelectedItem is not ComboBoxItem || _paused) return;
        StopMonitorStream(); StartMonitorStream();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopMonitorStream();
        if (_trayReady) { _tray.Visible = false; _tray.Dispose(); }
        base.OnClosed(e);
    }
}

public sealed class TaskRow
{
    public string Key { get; set; } = ""; public string RunId { get; set; } = ""; public string Pid { get; set; } = "—";
    public string Provider { get; set; } = "unknown"; public string ProviderLabel { get; set; } = "Unknown"; public string Role { get; set; } = "";
    public string Project { get; set; } = "不明"; public string Repo { get; set; } = ""; public string Tree { get; set; } = "";
    public string TaskSummary { get; set; } = "内容未判別"; public string Task { get; set; } = "";
    public string Model { get; set; } = "不明"; public string ApiModel { get; set; } = "記録なし"; public string EffectiveModel { get; set; } = "未報告";
    public string Effort { get; set; } = "不明"; public string ApiEffort { get; set; } = "記録なし"; public string EffectiveEffort { get; set; } = "未報告";
    public string Status { get; set; } = "unknown"; public string StatusLabel { get; set; } = "Unknown";
    public MediaBrush ProviderBrush { get; set; } = MediaBrushes.White; public MediaBrush StatusBrush { get; set; } = MediaBrushes.Gray; public MediaBrush CardBackground { get; set; } = MediaBrushes.Transparent;
    public string CreatedAt { get; set; } = ""; public string StartedAt { get; set; } = ""; public string ProcessStartedAt { get; set; } = ""; public string EndedAt { get; set; } = "";
    public string Created { get; set; } = "未記録"; public string Started { get; set; } = "未記録"; public string Ended { get; set; } = "未記録";
    public string Elapsed { get; set; } = "—"; public DateTime SortTime { get; set; }
    public string LatestEvent { get; set; } = ""; public string Brief { get; set; } = ""; public string Result { get; set; } = "";
    public string Receipt { get; set; } = ""; public string Transcript { get; set; } = "";
    public string ProjectTree { get; set; } = ""; public string PidRun { get; set; } = ""; public string ModelEffort { get; set; } = "";
}

public sealed class EventRow
{
    public string Time { get; set; } = ""; public string Title { get; set; } = ""; public string Description { get; set; } = ""; public string Source { get; set; } = "";
}
