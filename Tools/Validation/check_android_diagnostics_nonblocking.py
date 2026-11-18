#!/usr/bin/env python3
"""Static regression guard for Android runtime diagnostic stalls.

This does not prove Unity compilation, Android crash freedom, or FPS.
Usage: python3 Tools/Validation/check_android_diagnostics_nonblocking.py [path-to-AarisMobileRuntime.cs]
"""
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
DEFAULT = ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs"


def audit(text: str) -> None:
    awake = text.split("private void Awake()", 1)[1].split("private IEnumerator Start()", 1)[0]
    record = text.split("private void Record(string reason)", 1)[1].split("private static string AppendReport(", 1)[0]

    # File setup is dispatched from Record(), not run synchronously in Android Awake.
    assert "diagnosticsFileNeedsInitialization = true;" in awake
    assert "#if UNITY_ANDROID && !UNITY_EDITOR" in awake
    assert "pendingLogWrite = Task.Run(() => InitializeDiagnosticsFile(" in record
    assert 'File.WriteAllText(path, firstReport + "\\n");' in record

    # getPss may block the foreground thread under memory pressure.
    assert "AndroidJavaClass(\"android.os.Debug\")" not in record
    assert "s.androidPssKB = -1;" in record

    # No Android pause/quit/low-memory branch is allowed to wait for an
    # unfinished task, or to run file append on the Unity main thread.
    guard = record.index("if (!pendingLogWrite.IsCompleted) return;")
    result = record.index("pendingLogWrite.GetAwaiter().GetResult()")
    assert guard < result
    assert 'reason != "pause"' not in record
    assert 'reason != "quit"' not in record
    assert "pendingLogWrite = Task.Run(() => AppendReport(" in record
    assert "string error = AppendReport(logPath, LatestReport);" in record
    assert record.index("#else\n            string error = AppendReport(") > guard

    # Runtime budgets, vegetation density, and texture source data stay unchanged.
    assert "ConfigureTerrainInstanceRenderer();" in text
    assert "ConfigureAndroidQuality();" in text
    assert "Resources.UnloadUnusedAssets()" in text


if __name__ == "__main__":
    path = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT
    audit(path.read_text(encoding="utf-8"))
    print("ANDROID_DIAGNOSTICS_NONBLOCKING_STATIC_PASS")
