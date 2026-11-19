#!/usr/bin/env python3
"""Static regressions for Android previous-process exit diagnostics.

Only source contracts; does NOT execute Android JNI or prove a crash cause.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
RUNTIME = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs").read_text()
BRIDGE = (ROOT / "Assets/AarisWorld/Scripts/AarisAndroidExitHistory.cs").read_text()
META = (ROOT / "Assets/AarisWorld/Scripts/AarisAndroidExitHistory.cs.meta").read_text()


class PriorExitHistory(unittest.TestCase):
    def test_same_package_api30_only_and_no_permission_changes(self):
        self.assertIn('new AndroidJavaClass("android.os.Build$VERSION")', BRIDGE)
        self.assertIn('sdk.GetStatic<int>("SDK_INT") < 30', BRIDGE)
        self.assertIn('activity.Call<string>("getPackageName")', BRIDGE)
        self.assertIn('"getHistoricalProcessExitReasons", packageName, 0, 1', BRIDGE)
        self.assertNotIn('"android.permission.DUMP"', BRIDGE)
        self.assertNotIn("getTraceInputStream", BRIDGE)
        self.assertNotIn("getDescription", BRIDGE)

    def test_binder_work_is_off_main_thread(self):
        async_code = BRIDGE.split("internal static Task<Snapshot> QueryAsync()", 1)[1]
        self.assertIn("return Task.Run(() => QueryOnAttachedWorker(activity));", async_code)
        worker = BRIDGE.split("private static Snapshot QueryOnAttachedWorker(", 1)[1]
        self.assertIn("if (AndroidJNI.AttachCurrentThread() < 0)", worker)
        self.assertIn('Snapshot.Unknown("jni-attach-failed")', worker)
        self.assertIn("AndroidJNI.DetachCurrentThread();", worker)
        self.assertIn("if (attached) AndroidJNI.DetachCurrentThread();", worker)
        self.assertIn("catch (AndroidJavaException)", worker)
        self.assertIn("catch (Exception)", worker)
        self.assertNotIn("Profiler.GetTotalAllocatedMemoryLong", BRIDGE)
        self.assertNotIn("Debug.getPss", BRIDGE)

    def test_sampling_deferred_past_startup_with_no_update_jni(self):
        probe = RUNTIME.split(
            "private IEnumerator CollectPreviousProcessExitAfterStartup()", 1
        )[1].split("#endif", 1)[0]
        self.assertIn("yield return new WaitForSecondsRealtime(12f);", probe)
        self.assertIn("AarisAndroidExitHistory.QueryAsync();", probe)
        self.assertIn("while (!query.IsCompleted) yield return null;", probe)
        self.assertIn('Record("previous-process-exit");', probe)
        update = RUNTIME.split("private void Update()", 1)[1].split(
            "private void OnLowMemory()", 1
        )[0]
        self.assertNotIn("getHistoricalProcessExitReasons", update)
        self.assertNotIn("QueryAsync()", update)
        self.assertNotIn("AndroidJNI", update)

    def test_report_distinguishes_previous_exit_from_current_ram(self):
        for field in (
            "previousExitEvidence", "previousExitReasonCode",
            "previousExitStatusCode", "previousExitTimestampUtcMs",
            "previousExitPssKB", "previousExitRssKB",
        ):
            self.assertIn(field, RUNTIME)
        self.assertIn('previousExitEvidence = "pending"', RUNTIME)
        self.assertIn("previousExitReasonCode = -1", RUNTIME)
        self.assertIn("s.androidPssKB = -1;", RUNTIME)
        self.assertIn('result.state = "available"', BRIDGE)
        self.assertIn("lastPss > 0 ? lastPss : -1L;", BRIDGE)
        self.assertIn("lastRss > 0 ? lastRss : -1L;", BRIDGE)
        self.assertIn('Snapshot.Unknown("no-history")', BRIDGE)
        self.assertIn('Snapshot.Unknown("unsupported-api")', BRIDGE)
        self.assertNotIn('Call<long>("getPss")', RUNTIME)

    def test_other_platforms_do_not_run_android_bridge(self):
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", BRIDGE)
        self.assertIn("internal static Task<Snapshot> QueryAsync()", BRIDGE)
        self.assertIn("internal static Snapshot Unknown(string state)", BRIDGE)
        self.assertEqual(META.count("guid: "), 1)
        self.assertIn("MonoImporter:", META)


if __name__ == "__main__":
    unittest.main(verbosity=2)
