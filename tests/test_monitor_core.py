import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "monitor"))
import codex_monitor as monitor  # noqa: E402


class MonitorCoreTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.original_projects = monitor.CLAUDE_PROJECTS
        monitor.CLAUDE_PROJECTS = self.root / "claude-projects"
        monitor.CLAUDE_PROJECTS.mkdir()

    def tearDown(self):
        monitor.CLAUDE_PROJECTS = self.original_projects
        monitor._JSON_CACHE.clear()
        monitor._CLAUDE_CACHE.clear()
        self.temp.cleanup()

    def test_process_identity_keeps_os_start_time(self):
        row = monitor.classify_process({
            "ProcessId": 99871, "ParentProcessId": 77, "Name": "agy.exe",
            "CreationDate": "/Date(1795093583106)/", "ExecutablePath": "C:/tools/agy.exe",
            "CommandLine": "agy.exe --model gemini-test",
        }, roots=[])
        self.assertEqual(row["pid"], 99871)
        self.assertEqual(row["process_started_at"], "2026-11-19T13:06:23.106000+00:00")
        self.assertEqual(row["started_at"], row["process_started_at"])

    def test_history_has_no_legacy_100_or_200_item_cap(self):
        runs = self.root / "runs"
        for index in range(237):
            folder = runs / ("run-%04d" % index)
            folder.mkdir(parents=True)
            receipt = {
                "run_id": "run-%04d" % index,
                "provider": "codex",
                "status": "completed",
                "request": "Task %d" % index,
                "repo": "S:/Projects/Test/wt-%d" % index,
                "completed_at": "2026-11-18T12:00:%02dZ" % (index % 60),
            }
            (folder / "receipt.json").write_text(json.dumps(receipt), encoding="utf-8")
        data = monitor.snapshot(processes=[], roots=[self.root])
        self.assertEqual(len(data["history"]), 237)
        self.assertEqual(data["model_calls"], 0)
        self.assertEqual(data["quota_cost"], 0)

    def test_claude_history_keeps_more_than_100_sessions(self):
        for index in range(125):
            project = monitor.CLAUDE_PROJECTS / ("Project-%03d" % index)
            project.mkdir()
            session = "%08x-0000-0000-0000-%012x" % (index + 1, index + 1)
            (project / (session + ".jsonl")).write_text(json.dumps({
                "type": "user", "timestamp": "2026-11-18T12:00:00Z",
                "message": {"content": "Session %d" % index},
            }) + "\n", encoding="utf-8")
        history = monitor._claude_history(set())
        self.assertEqual(len(history), 125)

    def test_watch_json_stream_emits_zero_cost_snapshot(self):
        env = dict(__import__("os").environ)
        env["PYTHONIOENCODING"] = "utf-8"
        process = subprocess.Popen(
            [sys.executable, str(ROOT / "monitor" / "codex_monitor.py"), "watch-json", "--interval", "2"],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", env=env,
        )
        try:
            line = process.stdout.readline()
            self.assertTrue(line)
            data = json.loads(line)
            self.assertEqual(data["model_calls"], 0)
            self.assertEqual(data["quota_cost"], 0)
            self.assertIn("tasks", data)
        finally:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
            process.stdout.close()
            process.stderr.close()


if __name__ == "__main__":
    unittest.main()
