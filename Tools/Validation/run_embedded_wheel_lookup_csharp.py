#!/usr/bin/env python3
"""Compile exact production embedded-wheel importer and its axle guard.

Mono Unity API stubs verify pure managed behaviour; no Unity Editor/IL2CPP
or real Android device performance/physics assertions.
"""
import argparse
from pathlib import Path
import subprocess
import shutil
import tempfile

from run_runtime_native_work_csharp import braced

ROOT = Path(__file__).resolve().parents[2]
MOTOR = ROOT / "Assets/AarisWorld/Scripts/AarisVehicleMotor.cs"
HARNESS = ROOT / "Tools/Validation/EmbeddedWheelLookupTests.cs"


def fixture_source():
    code = MOTOR.read_text(encoding="utf8")
    fixture = HARNESS.read_text(encoding="utf8")
    embedded = "public bool AttachEmbeddedWheelModels(Transform bodyRoot)"
    axles = "private static bool AreAuthoredWheelAxlesPlausible(Bounds[] tyres)"
    blocks = {
        "/* PRODUCTION_EMBEDDED_WHEELS */":
            embedded + "\n        {" + braced(code, embedded) + "}",
        "/* PRODUCTION_AXLE_CHECK */":
            axles + "\n        {" + braced(code, axles) + "}",
    }
    for marker, expansion in blocks.items():
        assert fixture.count(marker) == 1, marker
        fixture = fixture.replace(marker, expansion)
    return fixture


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--emit", type=Path)
    args = parser.parse_args()
    source = fixture_source()
    if args.emit:
        args.emit.write_text(source, encoding="utf8")
        return
    mono, compiler = shutil.which("mono"), shutil.which("mcs")
    if not mono or not compiler:
        raise SystemExit("mono and mcs are required")
    with tempfile.TemporaryDirectory(prefix="aaris-embedded-wheel-") as tmp:
        root = Path(tmp)
        file = root / "embedded.cs"
        app = root / "embedded.exe"
        file.write_text(source, encoding="utf8")
        subprocess.run([compiler, "-nologo", "-langversion:latest",
                        "-optimize+", "-out:" + str(app), str(file)],
                       check=True, timeout=60)
        subprocess.run([mono, str(app)], check=True, timeout=30)


if __name__ == "__main__":
    main()
