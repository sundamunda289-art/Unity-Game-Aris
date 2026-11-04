"""Contract tests for the Codespace-local read-only World job executor."""
from __future__ import annotations

import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import ai_world_worker as worker


class ContractTests(unittest.TestCase):
    def test_read_only_allowlist(self):
        for mode in ("schema", "context", "scan", "site", "capture"):
            self.assertIn(mode, worker.READ_MODES)
        for rejected in ("edit", "delete", "apply", "preview", "shell", "terrain_sculpt"):
            with self.assertRaises(worker.JobError):
                worker.validate({"id": "test", "mode": rejected})
        with self.assertRaises(worker.JobError):
            worker.validate({"id": "ok", "mode": "schema", "script": "anything"})
        with self.assertRaises(worker.JobError):
            worker.validate({"id": "../escape", "mode": "schema"})
        with self.assertRaises(worker.JobError):
            worker.validate({"id": "test", "mode": "site", "x": float("nan"), "z": 12})
        with self.assertRaises(worker.JobError):
            worker.validate({"id": "test", "mode": "site", "x": 12, "z": 13, "limit": True})
        with self.assertRaises(worker.JobError):
            worker.validate({"id": "test", "mode": "capture", "camera_position": [0, 0, 0],
                             "camera_target": [0, 0, 0]})

    def test_capture_is_fail_closed(self):
        with tempfile.TemporaryDirectory() as tmp:
            request = worker.validate({"id": "photo", "mode": "capture",
                "camera_position": [0, 10, 0], "camera_target": [0, 0, 1]})
            with self.assertRaisesRegex(worker.JobError, "disabled"):
                worker.command_for(request, Path(tmp), Path(tmp), False)
            with patch.dict(os.environ, {}, clear=True):
                with self.assertRaisesRegex(worker.JobError, "UNITY_EDITOR_PATH"):
                    worker.command_for(request, Path(tmp), Path(tmp), True)

    def test_idempotence_and_persistent_status(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "repo"
            (root / "Tools" / "World").mkdir(parents=True)
            script = root / "Tools" / "World" / "world.py"
            script.write_text(
                "import json,sys,pathlib\n"
                "out = pathlib.Path(sys.argv[sys.argv.index('--out') + 1])\n"
                "out.write_text(json.dumps({'command':'schema','result':{'okay':True}}))\n")
            state = Path(tmp) / "state"
            request_file = Path(tmp) / "request.json"
            request_file.write_text('{"id":"job01","mode":"schema"}')
            first = worker.execute(request_file, root, state)
            self.assertEqual(first["state"], "succeeded")
            self.assertTrue(Path(first["report"]).exists())
            self.assertEqual(first, worker.execute(request_file, root, state))
            request_file.write_text('{"id":"job01","mode":"context"}')
            with self.assertRaisesRegex(worker.JobError, "reused"):
                worker.execute(request_file, root, state)

    def test_shell_metacharacters_are_data(self):
        with tempfile.TemporaryDirectory() as tmp:
            request = worker.validate({"id": "search", "mode": "scan",
                "search": "tree; touch /tmp/NEVER", "limit": 2})
            args, report = worker.command_for(request, Path(tmp), Path(tmp), False)
            self.assertIn("tree; touch /tmp/NEVER", args)
            self.assertNotIn("bash", args)
            self.assertNotIn("sh", args)


if __name__ == "__main__":
    unittest.main()
