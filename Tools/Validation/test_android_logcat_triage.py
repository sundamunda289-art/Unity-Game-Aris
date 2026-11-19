#!/usr/bin/env python3
"""Regression tests for privacy-safe Android logcat categorization."""
import importlib.util
import io
import json
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("analyze_android_logcat.py")
spec = importlib.util.spec_from_file_location("logcat_triage", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def report(*lines):
    return module.analyze(lines)


def amount(item, name):
    return item["categories"].get(name, {}).get("count", 0)


class AndroidLogcatTriage(unittest.TestCase):
    def test_lmk_kill_targets_only_app(self):
        r = report("lmkd: Kill 'com.some.other' (123), oom_score_adj 900",
                   "lmkd: Kill 'com.aaris.game' (456), oom_score_adj 700")
        self.assertEqual(amount(r, "lmk_kill"), 1)
        self.assertTrue(r["has_explicit_app_failure_signal"])

    def test_ordinary_process_death_is_not_called_low_memory(self):
        r = report("ActivityManager: Process com.aaris.game (pid 123) has died")
        self.assertEqual(amount(r, "app_process_exit_unknown"), 1)
        self.assertEqual(amount(r, "lmk_kill"), 0)
        self.assertFalse(r["has_explicit_app_failure_signal"])

    def test_java_crash_requires_package_near_header(self):
        r = report("AndroidRuntime: FATAL EXCEPTION: main",
                   "AndroidRuntime: Process: com.not.our.game, PID: 20",
                   "AndroidRuntime: FATAL EXCEPTION: main",
                   "AndroidRuntime: Process: com.aaris.game, PID: 50")
        self.assertEqual(amount(r, "java_crash"), 1)
        self.assertEqual(r["categories"]["java_crash"]["line_numbers"], [3])

    def test_native_crash_requires_tombstone_process(self):
        r = report("libc: Fatal signal 11 (SIGSEGV), code 1, pid 52",
                   "DEBUG: >>> com.aaris.game <<<")
        self.assertEqual(amount(r, "native_crash"), 1)
        self.assertTrue(r["has_explicit_app_failure_signal"])

    def test_anr_is_app_specific(self):
        r = report("ActivityManager: ANR in com.other.app",
                   "ActivityManager: ANR in com.aaris.game")
        self.assertEqual(amount(r, "app_anr"), 1)

    def test_driver_and_thermal_are_unattributed(self):
        r = report("kgsl: GPU fault reset detected",
                   "thermalengine: CPU thermal throttling critical")
        self.assertEqual(amount(r, "gpu_driver_signal_unattributed"), 1)
        self.assertEqual(amount(r, "thermal_signal_unattributed"), 1)
        self.assertFalse(r["has_explicit_app_failure_signal"])

    def test_low_memory_warning_and_frame_stall_are_not_kill(self):
        r = report("Unity com.aaris.game: low-memory",
                   'Unity com.aaris.game: {"event":"frame-stall"}')
        self.assertEqual(amount(r, "app_low_memory_signal"), 1)
        self.assertEqual(amount(r, "app_frame_stall"), 1)
        self.assertEqual(amount(r, "lmk_kill"), 0)
        self.assertFalse(r["has_explicit_app_failure_signal"])

    def test_oom_is_distinct_from_lmk(self):
        r = report("AndroidRuntime: com.aaris.game java.lang.OutOfMemoryError")
        self.assertEqual(amount(r, "app_oom"), 1)
        self.assertEqual(amount(r, "lmk_kill"), 0)

    def test_exact_package_not_prefix(self):
        r = report("lmkd: Kill 'com.aaris.game.beta' (100)")
        self.assertEqual(amount(r, "lmk_kill"), 0)

    def test_reference_count_bounded_and_raw_log_never_echoed(self):
        secret = "private-email@example.invalid"
        r = report(*[f"lmkd: Kill 'com.aaris.game' ({i}) {secret}" for i in range(30)])
        self.assertEqual(amount(r, "lmk_kill"), 30)
        self.assertEqual(len(r["categories"]["lmk_kill"]["line_numbers"]), 12)
        self.assertNotIn(secret, json.dumps(r))

    def test_empty_input_and_invalid_package(self):
        self.assertEqual(report()["lines_scanned"], 0)
        with self.assertRaises(ValueError):
            module.analyze([], "../../anything")

    def test_cli_local_input_generates_sanitized_json(self):
        with tempfile.TemporaryDirectory() as tmp:
            source = Path(tmp) / "raw.txt"
            output = Path(tmp) / "summary.json"
            source.write_text("lmkd: Kill 'com.aaris.game' (765)\n", encoding="utf-8")
            module.main([str(source), "--output", str(output)])
            r = json.loads(output.read_text())
            self.assertEqual(amount(r, "lmk_kill"), 1)
            self.assertEqual(r["lines_scanned"], 1)


if __name__ == "__main__":
    unittest.main(verbosity=2)
