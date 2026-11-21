#!/usr/bin/env python3
"""Test exact authored car-validation C# code in controlled Unity API stubs."""
import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile

from run_runtime_native_work_csharp import braced

ROOT = Path(__file__).resolve().parents[2]
WORLD = ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs"
FIXTURE = ROOT / "Tools/Validation/AuthoredVehicleValidationScratchTests.cs"


def fixture_source():
    code = WORLD.read_text(encoding="utf8")
    template = FIXTURE.read_text(encoding="utf8")
    scratch = "[ThreadStatic] private static List<Transform> namedWheelQueryScratch;"
    signatures = (
        ("/* ACTUAL_BODY_BOUNDS */",
         "private static bool TryGetAuthoredLampBodyBounds(Transform chassis,\n"
         "            Transform visual, Bounds wholeCar, out Bounds bodyOnly)"),
        ("/* ACTUAL_EMBEDDED_WHEEL_CHECK */",
         "private static bool HasEmbeddedWheelSet(GameObject source)"),
    )
    if code.count(scratch) != 1:
        raise ValueError("missing original shared scratch buffer")
    if template.count("/* ORIGINAL_SHARED_SCRATCH_FIELD */") != 1:
        raise ValueError("scratch fixture changed")
    template = template.replace("/* ORIGINAL_SHARED_SCRATCH_FIELD */", scratch)
    for marker, sig in signatures:
        if template.count(marker) != 1 or code.count(sig) != 1:
            raise ValueError("validation source method changed: " + sig)
        template = template.replace(marker, sig + "\n        {" + braced(code, sig) + "}")
    return template


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--emit", type=Path)
    args = parser.parse_args()
    source = fixture_source()
    if args.emit:
        args.emit.write_text(source, encoding="utf8")
        return
    mono, mcs = shutil.which("mono"), shutil.which("mcs")
    if not mono or not mcs:
        raise SystemExit("Mono and mcs required")
    with tempfile.TemporaryDirectory(prefix="aaris-car-validation-") as td:
        code = Path(td) / "car-validation.cs"
        exe = Path(td) / "car-validation.exe"
        code.write_text(source, encoding="utf8")
        subprocess.run([mcs, "-nologo", "-langversion:latest",
                        "-optimize+", "-out:" + str(exe), str(code)],
                       check=True, timeout=60)
        subprocess.run([mono, str(exe)], check=True, timeout=35)


if __name__ == "__main__":
    main()
