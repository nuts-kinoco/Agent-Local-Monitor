#!/usr/bin/env python3
"""Read-only, zero-model-call inventory of locally running Codex processes."""
import argparse
import datetime as dt
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def discover_roots():
    roots = [Path.home() / ".codex" / "hybrid-dev"]
    projects = Path("S:/Projects")
    if projects.exists():
        roots.extend(path for path in projects.glob("*/codex-runs") if path.is_dir())
    return list(dict.fromkeys(path.resolve() for path in roots if path.exists()))


DEFAULT_ROOTS = discover_roots()
CLAUDE_PROJECTS = Path.home() / ".claude" / "projects"
_JSON_CACHE = {}
_CLAUDE_CACHE = {}


def _powershell_processes():
    if os.name != "nt":
        raise RuntimeError("Automatic process discovery currently requires Windows CIM.")
    script = ("$ErrorActionPreference='Stop'; "
              "[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); "
              "Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(codex|claude|agy)' } | "
              "Select-Object ProcessId,ParentProcessId,Name,CreationDate,ExecutablePath,CommandLine | "
              "ConvertTo-Json -Depth 3 -Compress")
    result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
                            capture_output=True, text=True, encoding="utf-8", errors="strict", timeout=20)
    if result.returncode:
        raise RuntimeError("Windows process inventory failed: " + result.stderr.strip())
    payload = json.loads(result.stdout or "[]")
    return payload if isinstance(payload, list) else [payload]


def _arg_value(command, name):
    match = re.search(r"(?:^|\s)" + re.escape(name) + r"(?:=\s*|\s+)(?:\"([^\"]*)\"|'([^']*)'|(\S+))", command)
    return next((part for part in match.groups() if part is not None), None) if match else None


def _effort(command):
    value = _arg_value(command, "model_reasoning_effort")
    if value:
        return value.strip('\\"')
    match = re.search(r"model_reasoning_effort\\?=\\?\"(low|medium|high|xhigh|max)\\?\"", command)
    return match.group(1) if match else None


def _receipt_for(command):
    output = _arg_value(command, "--output-last-message")
    if not output:
        return None
    path = Path(output)
    receipt_path = path.parent / "receipt.json"
    try:
        data = json.loads(receipt_path.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else None
    except (OSError, ValueError, UnicodeError):
        return None


def _request_summary(path):
    if not path:
        return None
    try:
        text = Path(path).read_text(encoding="utf-8-sig", errors="replace")
    except OSError:
        return None
    for line in text.splitlines():
        line = line.strip().lstrip("#*- ").strip()
        if line:
            return line[:180]
    return None


def _claude_session(session_id):
    if not session_id or not CLAUDE_PROJECTS.exists():
        return None
    matches = list(CLAUDE_PROJECTS.glob(f"*/{session_id}.jsonl"))
    if not matches:
        return None
    path = matches[0]
    cached = _cached_claude(path, full=True)
    if cached is not None and "last_api_request_at" in cached:
        return cached
    latest_user = latest_assistant = first_at = last_at = None
    last_api_request_at = last_api_response_at = None
    last_api_model = last_api_effort = None
    try:
        with path.open("r", encoding="utf-8", errors="replace") as stream:
            for line in stream:
                try:
                    entry = json.loads(line)
                except ValueError:
                    continue
                stamp = entry.get("timestamp")
                if stamp:
                    first_at = first_at or stamp
                    last_at = stamp
                if entry.get("type") == "api-request":
                    last_api_request_at = stamp or last_api_request_at
                    params = entry.get("params") or {}
                    last_api_model = params.get("model") or last_api_model
                    last_api_effort = (params.get("output_config") or {}).get("effort") or last_api_effort
                if entry.get("type") == "assistant" and entry.get("message", {}).get("usage"):
                    last_api_response_at = stamp or last_api_response_at
                if entry.get("type") not in ("user", "assistant"):
                    continue
                content = (entry.get("message") or {}).get("content") or ""
                if isinstance(content, list):
                    content = " ".join(block.get("text", "") for block in content
                                       if isinstance(block, dict) and block.get("type") == "text")
                if not isinstance(content, str):
                    continue
                text = " ".join(content.split())
                if not text:
                    continue
                if entry.get("type") == "user":
                    if (text.startswith("<task-notification>") or text.startswith("Command running in background")
                            or text.startswith("This session is being continued from a previous conversation")):
                        continue
                    latest_user = text[:500]
                else:
                    latest_assistant = text[:500]
    except OSError:
        return None
    result = {"session_id": session_id, "transcript": str(path), "started_at": first_at,
            "last_activity_at": last_at, "task": latest_user, "latest_update": latest_assistant,
            "last_api_request_at": last_api_request_at, "last_api_response_at": last_api_response_at,
            "last_api_model": last_api_model, "last_api_effort": last_api_effort,
            "project": path.parent.name}
    _store_claude(path, result)
    return result


def _store_claude(path, value):
    try:
        stat = path.stat()
        _CLAUDE_CACHE[str(path)] = (stat.st_mtime_ns, stat.st_size, value)
    except OSError:
        pass


def _cached_claude(path, full=False):
    try:
        stat = path.stat()
        item = _CLAUDE_CACHE.get(str(path))
        if item and item[0] == stat.st_mtime_ns and item[1] == stat.st_size:
            return item[2]
    except OSError:
        return None
    return None


def _process_started_at(value):
    if not value:
        return None
    match = re.fullmatch(r"/Date\((\d+)\)/", str(value))
    if match:
        return dt.datetime.fromtimestamp(int(match.group(1)) / 1000, tz=dt.timezone.utc)
    try:
        parsed = dt.datetime.fromisoformat(str(value).replace("/", "-"))
        return parsed.astimezone(dt.timezone.utc) if parsed.tzinfo else parsed.astimezone().astimezone(dt.timezone.utc)
    except ValueError:
        return None


def _iso_time(value):
    if not value:
        return None
    try:
        parsed = dt.datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        return parsed.astimezone(dt.timezone.utc) if parsed.tzinfo else parsed.replace(tzinfo=dt.timezone.utc)
    except ValueError:
        return None


def _all_receipts(roots=DEFAULT_ROOTS):
    found = []
    for root in roots:
        runs = root / "runs"
        if not runs.exists():
            continue
        for receipt_path in runs.glob("*/receipt.json"):
            try:
                stat = receipt_path.stat()
                cached = _JSON_CACHE.get(str(receipt_path))
                if cached and cached[0] == stat.st_mtime_ns and cached[1] == stat.st_size:
                    receipt = cached[2]
                else:
                    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
                    _JSON_CACHE[str(receipt_path)] = (stat.st_mtime_ns, stat.st_size, receipt)
            except (OSError, ValueError, UnicodeError):
                continue
            if isinstance(receipt, dict):
                receipt["_receipt_path"] = str(receipt_path)
                found.append(receipt)
    return found


def _claude_history(active_session_ids, limit=None):
    if not CLAUDE_PROJECTS.exists():
        return []
    files = sorted(CLAUDE_PROJECTS.glob("*/*.jsonl"), key=lambda path: path.stat().st_mtime, reverse=True)
    if limit is not None:
        files = files[:limit]
    history = []
    for path in files:
        session_id = path.stem
        if session_id in active_session_ids:
            continue
        cached = _cached_claude(path)
        if cached is not None and "session_id" in cached:
            value = dict(cached)
            history.append({"provider": "claude", "run_id": session_id, "status": "inactive_session",
                            "task_summary": value.get("task"), "task": value.get("task"), "repo": value.get("project"),
                            "model": "see transcript", "effort": "see transcript", "effective_model": "not_reported",
                            "effective_effort": "not_reported", "created_at": None, "started_at": None,
                            "ended_at": value.get("last_activity_at"), "receipt": value.get("transcript"),
                            "result": None, "latest_update": value.get("latest_update")})
            continue
        latest_user = latest_assistant = None
        last_at = None
        try:
            with path.open("rb") as stream:
                stream.seek(max(0, path.stat().st_size - 131072))
                tail = stream.read().decode("utf-8", errors="replace").splitlines()
            for line in reversed(tail):
                try:
                    entry = json.loads(line)
                except ValueError:
                    continue
                if last_at is None and entry.get("timestamp"):
                    last_at = entry["timestamp"]
                if entry.get("type") not in ("user", "assistant"):
                    continue
                content = (entry.get("message") or {}).get("content") or ""
                if isinstance(content, list):
                    content = " ".join(block.get("text", "") for block in content
                                       if isinstance(block, dict) and block.get("type") == "text")
                if not isinstance(content, str) or not content.strip():
                    continue
                if entry.get("type") == "user" and latest_user is None:
                    text = " ".join(content.split())
                    if not text.startswith("<task-notification>") and not text.startswith("Command running in background"):
                        latest_user = text[:500]
                elif entry.get("type") == "assistant" and latest_assistant is None:
                    latest_assistant = " ".join(content.split())[:500]
                if latest_user and latest_assistant and last_at:
                    break
        except OSError:
            continue
        summary = {"session_id": session_id, "transcript": str(path), "task": latest_user,
                   "latest_update": latest_assistant, "last_activity_at": last_at, "project": path.parent.name}
        _store_claude(path, summary)
        history.append({"provider": "claude", "run_id": session_id, "status": "inactive_session",
                        "task_summary": latest_user, "task": latest_user, "repo": path.parent.name,
                        "model": "see transcript", "effort": "see transcript",
                        "effective_model": "not_reported", "effective_effort": "not_reported",
                        "created_at": None, "started_at": None, "ended_at": last_at,
                        "receipt": str(path), "result": None, "latest_update": latest_assistant})
    return history


def _tail_event(path):
    try:
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError:
        return None
    for line in reversed(lines[-80:]):
        try:
            event = json.loads(line)
        except ValueError:
            continue
        kind = event.get("type")
        item = event.get("item") or {}
        if kind in ("item.started", "item.completed", "turn.started", "turn.completed", "turn.failed", "error"):
            detail = item.get("command") or item.get("type") or event.get("error", {}).get("message") or ""
            return {"type": kind, "detail": str(detail)[:240], "observed_at": None}
    return None


def classify_process(process, roots=DEFAULT_ROOTS):
    command = process.get("CommandLine") or ""
    name = process.get("Name") or "codex.exe"
    pid = int(process.get("ProcessId") or 0)
    lower = (command + " " + name).lower()
    process_start = _process_started_at(process.get("CreationDate"))
    base = {"pid": pid, "parent_pid": int(process.get("ParentProcessId") or 0),
            "name": name, "started_at": process_start.isoformat() if process_start else None,
            "process_started_at": process_start.isoformat() if process_start else None,
            "executable": process.get("ExecutablePath"), "classification": "unknown",
            "task": None, "repo": None, "model": None, "effort": None,
            "run_id": None, "status": "unknown", "latest_event": None}
    if (name.lower() in ("codex-code-mode-host.exe", "codex-windows-sandbox-service.exe",
                         "codex-computer-use-swift.exe")
            or re.search(r"(?:^|\s)(?:app-server|exec-server)(?:\s|$)", lower)
            or re.search(r"(?:^|\s)--type=", lower)):
        base["classification"] = "infrastructure"
        base["status"] = "service"
        return base
    if name.lower() == "claude.exe" and "\\windowsapps\\claude_" in lower and "--output-format" not in lower:
        base["classification"] = "infrastructure"
        base["status"] = "desktop_shell"
        return base
    if re.search(r"(?:^|\s)exec(?:\s|$)", lower):
        base["classification"] = "codex_exec"
        base["provider"] = "codex"
    elif name.lower() == "claude.exe" and ("--output-format" in lower or "--resume" in lower):
        base["classification"] = "claude_code"
        base["provider"] = "claude"
        base["model"] = _arg_value(command, "--model") or "alias/default"
        effort_match = re.search(r"--effort(?:=|\s+)([a-z]+)", command, re.IGNORECASE)
        base["effort"] = effort_match.group(1).lower() if effort_match else "not_reported"
        session_match = re.search(r"--resume(?:=|\s+)([0-9a-f-]{20,})", command, re.IGNORECASE)
        session = _claude_session(session_match.group(1) if session_match else None)
        if session:
            base.update({"run_id": session["session_id"], "task": session["task"],
                         "task_summary": session["task"], "repo": session["project"],
                         "created_at": session["started_at"],
                         "last_api_request_at": session["last_api_request_at"],
                         "last_api_response_at": session["last_api_response_at"],
                         "last_api_model": session["last_api_model"],
                         "last_api_effort": session["last_api_effort"],
                         "transcript": session["transcript"],
                         "latest_event": {"type": "last_activity", "detail": session["latest_update"] or ""}})
            api_request_at = _iso_time(session["last_api_request_at"])
            api_response_at = _iso_time(session["last_api_response_at"])
            process_started_at = _process_started_at(process.get("CreationDate"))
            if not api_request_at or (process_started_at and api_request_at < process_started_at):
                base["status"] = "idle_no_recent_api_evidence"
            elif api_response_at and api_response_at >= api_request_at:
                base["status"] = "idle_after_last_response"
            else:
                base["status"] = "request_in_flight_or_unconfirmed"
        else:
            base["status"] = "process_detected_session_unresolved"
    elif name.lower() == "agy.exe":
        base["classification"] = "agy_cli"
        base["provider"] = "agy"
        base["status"] = "running"
        base["task"] = command[:500]
        base["task_summary"] = command[:180]
    elif command.strip().lower().endswith("codex.exe") or not command.strip():
        base["classification"] = "interactive_or_app_child"
        base["status"] = "process_detected_task_unresolved"
    else:
        base["classification"] = "codex_process_unresolved"
    base["repo"] = _arg_value(command, "--cd")
    base["model"] = base.get("model") or _arg_value(command, "--model")
    base["effort"] = base.get("effort") or _effort(command)
    receipt = _receipt_for(command)
    if receipt:
        base.update({"provider": receipt.get("provider", base.get("provider", "unknown")),
                     "role": receipt.get("role"),
                     "run_id": receipt.get("run_id"), "task": receipt.get("request"),
                     "repo": receipt.get("repo") or base["repo"],
                     "model": receipt.get("requested_model") or base["model"],
                     "effort": receipt.get("requested_effort") or base["effort"],
                     "status": receipt.get("status", "unknown"),
                     "effective_model": receipt.get("effective_model", "not_reported"),
                     "effective_effort": receipt.get("effective_effort", "not_reported")})
        base["task_summary"] = _request_summary(receipt.get("request"))
        base["created_at"] = receipt.get("created_at")
        base["started_at"] = receipt.get("started_at") or base.get("started_at")
        base["ended_at"] = receipt.get("ended_at") or receipt.get("completed_at")
        base["duration_seconds"] = receipt.get("duration_seconds")
        event = _tail_event(Path(receipt.get("result", "")).parent / "events.jsonl") if receipt.get("result") else None
        base["latest_event"] = event
    else:
        base["status"] = "running_unmanaged" if base["classification"] == "codex_exec" else base["status"]
    return base


def snapshot(processes=None, roots=None):
    roots = discover_roots() if roots is None else roots
    processes = _powershell_processes() if processes is None else processes
    items = [classify_process(process) for process in processes]
    items.sort(key=lambda item: (item["classification"] == "infrastructure", item["pid"]))
    tasks = [item for item in items if item["classification"] != "infrastructure"]
    services = [item for item in items if item["classification"] == "infrastructure"]
    active_run_ids = {item.get("run_id") for item in tasks if item.get("run_id")}
    history = []
    for receipt in _all_receipts(roots):
        if receipt.get("run_id") in active_run_ids or receipt.get("status") in ("running", "starting"):
            continue
        history.append({"provider": receipt.get("provider", "unknown"),
                        "role": receipt.get("role"),
                        "run_id": receipt.get("run_id"), "status": receipt.get("status", "unknown"),
                        "task": receipt.get("request"), "task_summary": _request_summary(receipt.get("request")),
                        "repo": receipt.get("repo"), "model": receipt.get("requested_model"),
                        "effort": receipt.get("requested_effort"),
                        "effective_model": receipt.get("effective_model", "not_reported"),
                        "effective_effort": receipt.get("effective_effort", "not_reported"),
                        "created_at": receipt.get("created_at"), "started_at": receipt.get("started_at"),
                        "ended_at": receipt.get("ended_at") or receipt.get("completed_at"),
                        "exit_code": receipt.get("exit_code"), "receipt": receipt.get("_receipt_path"),
                        "result": receipt.get("result")})
    history.sort(key=lambda item: item.get("ended_at") or item.get("started_at") or item.get("created_at") or "", reverse=True)
    history.extend(_claude_history(active_run_ids))
    history.sort(key=lambda item: item.get("ended_at") or item.get("started_at") or item.get("created_at") or "", reverse=True)
    return {"observed_at": dt.datetime.now(dt.timezone.utc).isoformat(),
            "source": "Windows Win32_Process + local Claude transcripts + agent receipts/events",
            "model_calls": 0, "quota_cost": 0, "tasks": tasks, "history": history, "infrastructure": services}


def render(data):
    print("Codex Local Monitor | model calls: 0 | quota cost: 0")
    print("Observed:", data["observed_at"])
    if not data["tasks"]:
        print("No Codex task processes detected.")
    for task in data["tasks"]:
        print("\n[{status}] PID {pid} / {classification}".format(**task))
        print("  Task:", task.get("task") or "内容未判別（未管理の対話セッション等）")
        print("  Repo:", task.get("repo") or "unknown")
        print("  Model/effort:", (task.get("model") or "unknown") + " / " + (task.get("effort") or "unknown"))
        print("  Effective:", (task.get("effective_model") or "unknown") + " / " + (task.get("effective_effort") or "unknown"))
        print("  Run:", task.get("run_id") or "unmanaged")
        if task.get("latest_event"):
            print("  Latest:", task["latest_event"].get("type"), task["latest_event"].get("detail"))
    print("\nInfrastructure processes:", len(data["infrastructure"]))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("snapshot", help="print one read-only inventory")
    watch = sub.add_parser("watch", help="refresh a local console view")
    watch.add_argument("--interval", type=float, default=2.0)
    watch_json = sub.add_parser("watch-json", help="stream local snapshots as JSON lines")
    watch_json.add_argument("--interval", type=float, default=5.0)
    args = parser.parse_args()
    if args.command == "snapshot":
        data = snapshot()
        print(json.dumps(data, ensure_ascii=False, indent=2))
        return 0
    if args.command == "watch-json":
        if args.interval < 2:
            parser.error("--interval must be at least 2 seconds")
        try:
            while True:
                data = snapshot()
                print(json.dumps(data, ensure_ascii=False, separators=(",", ":")), flush=True)
                time.sleep(args.interval)
        except KeyboardInterrupt:
            return 0
    if args.interval < 1:
        parser.error("--interval must be at least 1 second")
    try:
        while True:
            os.system("cls" if os.name == "nt" else "clear")
            render(snapshot())
            time.sleep(args.interval)
    except KeyboardInterrupt:
        return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as exc:
        print("codex_monitor: " + str(exc), file=sys.stderr)
        raise SystemExit(2)
