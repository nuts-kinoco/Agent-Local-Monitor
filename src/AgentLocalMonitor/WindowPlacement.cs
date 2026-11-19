using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AgentLocalMonitor;

internal static class WindowPlacement
{
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint NoSize = 0x0001;

    internal static void PlaceInLowerHalf(Window window, string dataDir, bool useCurrentMonitor)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("配置対象のウィンドウが未作成です");
            if (window.WindowState != WindowState.Normal) window.WindowState = WindowState.Normal;
            var monitors = ReadMonitors();
            var current = MonitorFromWindow(hwnd, 2);
            var selected = useCurrentMonitor ? monitors.FirstOrDefault(x => x.Handle == current) : null;
            selected ??= monitors.Where(x => x.Bounds.Height > x.Bounds.Width)
                .OrderByDescending(x => x.Bounds.Left).FirstOrDefault();
            selected ??= monitors.FirstOrDefault(x => x.Primary) ?? monitors.FirstOrDefault();
            if (selected is null) throw new InvalidOperationException("配置先の画面を取得できません");

            // All these screen coordinates are physical pixels in this PMv2 process.
            // Move to the target first, then use that HWND's real DPI for dimensions.
            if (current != selected.Handle && !SetWindowPos(hwnd, IntPtr.Zero,
                selected.Work.Left + 8, selected.Work.Top + 8, 0, 0, NoSize | NoZOrder | NoActivate))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var target = selected;
            window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                try
                {
                    var dpi = GetDpiForWindow(hwnd);
                    if (dpi == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var scale = dpi / 96.0;
                    var gap = Math.Max(8, (int)Math.Round(8 * scale));
                    var availableWidth = Math.Max(1, target.Work.Width - 2 * gap);
                    var halfTop = target.Work.Top + target.Work.Height / 2;
                    var availableHeight = Math.Max(1, target.Work.Bottom - halfTop - 2 * gap);
                    window.MinWidth = Math.Min(360, availableWidth / scale);
                    window.MinHeight = Math.Min(440, availableHeight / scale);
                    var width = Math.Min((int)Math.Round(540 * scale), availableWidth);
                    var height = Math.Min((int)Math.Round(900 * scale), availableHeight);
                    var left = target.Work.Left + (target.Work.Width - width) / 2;
                    var top = target.Work.Bottom - gap - height;
                    if (!SetWindowPos(hwnd, IntPtr.Zero, left, top, width, height, NoZOrder | NoActivate))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                    {
                        GetWindowRect(hwnd, out var actual);
                        AppendLog(dataDir, new
                        {
                            time = DateTimeOffset.Now, action = "place-lower-half", monitor = target.Device,
                            boundsPx = target.Bounds, workPx = target.Work, dpi = GetDpiForWindow(hwnd),
                            requestedPx = new { left, top, width, height }, actualPx = actual,
                            widthDip = window.ActualWidth, heightDip = window.ActualHeight,
                            awareness = GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(hwnd)),
                            perMonitorV2 = AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(hwnd), new IntPtr(-4))
                        });
                    }));
                }
                catch (Exception ex) { AppendLog(dataDir, new { time = DateTimeOffset.Now, action = "placement-error", error = ex.Message }); }
            }));
        }
        catch (Exception ex) { AppendLog(dataDir, new { time = DateTimeOffset.Now, action = "placement-error", error = ex.Message }); }
    }

    private static void AppendLog(string dataDir, object value)
    {
        try { File.AppendAllText(Path.Combine(dataDir, "startup.log"), JsonSerializer.Serialize(value, new JsonSerializerOptions { IncludeFields = true }) + Environment.NewLine); }
        catch { }
    }

    private sealed record MonitorTarget(IntPtr Handle, string Device, NativeRect Bounds, NativeRect Work, bool Primary);

    private static List<MonitorTarget> ReadMonitors()
    {
        var result = new List<MonitorTarget>();
        MonitorCallback callback = (IntPtr handle, IntPtr dc, ref NativeRect bounds, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(handle, ref info)) result.Add(new MonitorTarget(handle, info.Device, info.Bounds, info.Work, (info.Flags & 1) != 0));
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Bounds, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref NativeRect bounds, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
