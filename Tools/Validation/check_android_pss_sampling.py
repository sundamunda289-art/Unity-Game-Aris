#!/usr/bin/env python3
"""Static regression: Android diagnostics must not synchronously query PSS.

No Android runtime or Unity compilation is simulated by this test.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
runtime = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs").read_text(encoding="utf-8")
record = runtime.split("private void Record(string reason)", 1)[1].split(
    "private static string InitializeLogFile(", 1
)[0]

# A kernel PSS request can block the game thread during launch, pause or LMK.
assert 'new AndroidJavaClass("android.os.Debug")' not in runtime
assert "s.androidPssKB = -1;" in record
assert "unityAllocatedBytes = Profiler.GetTotalAllocatedMemoryLong()" in record
assert "unityReservedBytes = Profiler.GetTotalReservedMemoryLong()" in record

# Preserve the latest merged background diagnostic queue and ordered launch log.
assert "Task.Run(() => InitializeLogFile(initialPath))" in runtime
assert "Task<string> previous = pendingLogWrite;" in record
assert "previous.ContinueWith(completed =>" in record
assert "if (!pendingLogWrite.IsCompleted)" in record
assert "TaskScheduler.Default" in record

# No change to terrain quality, scene/foliage density or dynamic render policy.
assert "ConfigureTerrainInstanceRenderer();" in runtime
assert "ConfigureAndroidQuality();" in runtime
print("ANDROID_PSS_MAIN_THREAD_STATIC_PASS")
