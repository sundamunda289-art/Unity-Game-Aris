#!/usr/bin/env python3
"""Execute production diagnostics guards with counted native API stand-ins.

Runs the actual source condition/helper, not a parallel Python implementation.
Native counter existence/units are separately verified by
AarisMobileDiagnosticsQA.Verify in the real Unity Editor.
"""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

from run_runtime_native_work_csharp import braced

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets/AarisWorld/Scripts"


def fixture_source():
    runtime = (SCRIPTS / "AarisMobileRuntime.cs").read_text()
    start = runtime.index("if (", runtime.index(
        "// SceneManager.UnloadSceneAsync destroys scene objects"))
    header = runtime[start:runtime.index("{", start)]
    memory_guard = header + "{" + braced(runtime[start:], "if (") + "}"
    overlay = (SCRIPTS / "AarisMobilePerfOverlay.cs").read_text()
    signature = "internal static long ReadGcAllocatedBytes(ProfilerRecorder recorder)"
    reader = signature + "{" + braced(overlay, signature) + "}"
    fixture = (ROOT / "Tools/Validation/MobileDiagnosticsTests.cs").read_text()
    for marker, production in (("/* MEMORY_GUARD */", memory_guard),
                               ("/* BYTE_READER */", reader)):
        assert fixture.count(marker) == 1
        fixture = fixture.replace(marker, production)
    return fixture


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--emit", type=Path)
    args = parser.parse_args()
    source = fixture_source()
    if args.emit:
        args.emit.write_text(source)
        return
    editor_data = os.environ.get("AARIS_UNITY_EDITOR_DATA")
    if editor_data:
        editor = Path(editor_data)
        mono = editor / "MonoBleedingEdge/bin/mono"
        compiler = [str(mono), str(editor / "MonoBleedingEdge/lib/mono/4.5/csc.exe")]
    else:
        mono, mcs = shutil.which("mono"), shutil.which("mcs")
        if not mono or not mcs:
            raise SystemExit("Install mono-mcs or set AARIS_UNITY_EDITOR_DATA")
        compiler = [mcs]
    with tempfile.TemporaryDirectory(prefix="aaris-mobile-diagnostics-", dir="/tmp") as directory:
        work = Path(directory)
        code = work / "fixture.cs"
        code.write_text(source)
        executable = work / "fixture.exe"
        subprocess.run(compiler + ["-nologo", "-langversion:latest", "-optimize+",
                       "-out:" + str(executable), str(code)], check=True, timeout=60)
        subprocess.run([str(mono), str(executable)], check=True, timeout=60)


if __name__ == "__main__":
    main()
