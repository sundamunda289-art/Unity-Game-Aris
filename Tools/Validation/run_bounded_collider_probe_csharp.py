#!/usr/bin/env python3
"""Compile the actual AarisNearbyColliders probe in bounded Unity stubs.

This tests deterministic authored-bound comparisons, not Unity PhysX, native
timings or Android FPS. Outputs are in a temporary directory only.
"""
import argparse
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
    finite = braced(code, "private static bool Finite(Vector3 value)")
    for marker in ("/* PROBE_BODY */", "/* FINITE_BODY */", "/* PROBE_CAP */"):
        if tests.count(marker) != 1:
            raise ValueError(f"fixture marker mismatch: {marker}")
    return (tests.replace("/* PROBE_BODY */", body)
                 .replace("/* FINITE_BODY */", finite)
                 .replace("/* PROBE_CAP */", match.group(1)))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--emit", type=Path)
    args = parser.parse_args()
    source = fixture_source()
    if args.emit:
        args.emit.write_text(source, encoding="utf-8")
        return
    mono = shutil.which("mono")
    mcs = shutil.which("mcs")
    if not mono or not mcs:
        raise SystemExit("Mono/mcs required to test the production C# method")
    with tempfile.TemporaryDirectory(prefix="aaris-bounded-probe-") as td:
        code = Path(td) / "probe.cs"
        exe = Path(td) / "probe.exe"
        code.write_text(source, encoding="utf-8")
        subprocess.run([mcs, "-nologo", "-langversion:latest", "-optimize+",
                        "-out:" + str(exe), str(code)],
                       check=True, timeout=60)
        subprocess.run([mono, str(exe)], check=True, timeout=20)


if __name__ == "__main__":
    main()
