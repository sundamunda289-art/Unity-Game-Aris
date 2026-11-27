#!/usr/bin/env python3
"""Compile the actual AarisNearbyColliders probe in bounded Unity stubs.

This tests deterministic authored-bound comparisons, not Unity PhysX, native
timings or Android FPS. Outputs are in a temporary directory only.
"""
import argparse
import os
import re
from pathlib import Path
import shutil
import subprocess
import tempfile

from run_runtime_native_work_csharp import braced

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "Assets/AarisWorld/Scripts/AarisNearbyColliders.cs"
FIXTURE = ROOT / "Tools/Validation/BoundedColliderProbeTests.cs"


def fixture_source():
    code = SOURCE.read_text(encoding="utf-8")
    tests = FIXTURE.read_text(encoding="utf-8")
    match = re.search(r"private const int MaxCellsPerProbe = (\d+);", code)
    if not match:
        raise ValueError("missing bounded probe budget")
    body = braced(code, "internal bool HasPotentialInactiveObstacle(")
    director = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text(encoding="utf-8")
    sweep = braced(director, "internal bool WouldCrossInactiveCover(")
    steps = re.search(r"private const int MaxCoverSteps = (\d+);", director)
    if not steps:
        raise ValueError("missing movement probe budget")
    finite = braced(code, "private static bool Finite(Vector3 value)")
    for marker, content in {
        "/* PROBE_BODY */": body,
        "/* PROBE_BATCH_BODY */": braced(code, "internal bool HasObstacleInProbes("),
        "/* MATCH_PROBE_BODY */": braced(code, "private bool MatchesObstacleProbe("),
        "/* FINITE_BODY */": finite,
        "/* PROBE_CAP */": match.group(1),
        "/* SWEEP_BODY */": sweep,
        "/* SWEEP_CAP */": steps.group(1),
    }.items():
        if tests.count(marker) != 1:
            raise ValueError(f"fixture marker mismatch: {marker}")
        tests = tests.replace(marker, content)
    return tests


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--emit", type=Path)
    args = parser.parse_args()
    source = fixture_source()
    if args.emit:
        args.emit.write_text(source, encoding="utf-8")
        return
    editor_data = os.environ.get("AARIS_UNITY_EDITOR_DATA")
    if editor_data:
        editor = Path(editor_data)
        mono = str(editor / "MonoBleedingEdge/bin/mono")
        compiler = [mono, str(editor / "MonoBleedingEdge/lib/mono/4.5/csc.exe")]
    else:
        mono, mcs = shutil.which("mono"), shutil.which("mcs")
        if not mono or not mcs:
            raise SystemExit("Install mono-mcs or set AARIS_UNITY_EDITOR_DATA")
        compiler = [mcs]
    with tempfile.TemporaryDirectory(prefix="aaris-bounded-probe-") as td:
        code = Path(td) / "probe.cs"
        exe = Path(td) / "probe.exe"
        code.write_text(source, encoding="utf-8")
        subprocess.run(compiler + ["-nologo", "-langversion:latest", "-optimize+",
                                   "-out:" + str(exe), str(code)],
                       check=True, timeout=60)
        subprocess.run([mono, str(exe)], check=True, timeout=20)


if __name__ == "__main__":
    main()
