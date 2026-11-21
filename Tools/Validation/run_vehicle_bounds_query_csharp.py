#!/usr/bin/env python3
"""Compile the actual car-bound-fitting method against controlled Unity API stubs.

This tests source integration and geometric parity, not native Unity or FPS.
"""
import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile

from run_runtime_native_work_csharp import braced

ROOT = Path(__file__).resolve().parents[2]
WORLD = ROOT / "Assets/AarisWorld/Scripts/AarisVehicleWorld.cs"
FIXTURE = ROOT / "Tools/Validation/VehicleBoundsQueryTests.cs"


def fixture_source():
    code = WORLD.read_text(encoding="utf8")
    template = FIXTURE.read_text(encoding="utf8")
    declarations = [
        "[ThreadStatic] private static List<MeshFilter> visualBoundsFilters;",
        "[ThreadStatic] private static List<SkinnedMeshRenderer> visualBoundsSkins;",
    ]
    for decl in declarations:
        if code.count(decl) != 1:
            raise ValueError("bounds scratch declaration changed: " + decl)
    signatures = [
        ("/* PRODUCTION_BOUNDS_METHOD */",
         "internal static bool TryGetVisualBoundsLocal(Transform localSpace,\n"
         "            Transform subtree, out Bounds combined)"),
        ("/* PRODUCTION_ACCUMULATE_METHOD */",
         "private static void AccumulateLocalBounds(Bounds meshBounds, Matrix4x4 toLocal,\n"
         "            ref Bounds combined, ref bool found)"),
    ]
    replacements = [("/* PRODUCTION_BOUNDS_FIELDS */", "\n        ".join(declarations))]
    for marker, signature in signatures:
        replacements.append((marker, signature + "\n        {" +
                             braced(code, signature) + "}"))
    for marker, replacement in replacements:
        if template.count(marker) != 1:
            raise ValueError("fixture marker missing: " + marker)
        template = template.replace(marker, replacement)
    return template


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--emit", type=Path)
    args = parser.parse_args()
    code = fixture_source()
    if args.emit:
        args.emit.write_text(code, encoding="utf8")
        return
    mono, mcs = shutil.which("mono"), shutil.which("mcs")
    if not mono or not mcs:
        raise SystemExit("Mono and mcs required")
    with tempfile.TemporaryDirectory(prefix="aaris-vehicle-bounds-") as folder:
        source = Path(folder) / "bounds.cs"
        exe = Path(folder) / "bounds.exe"
        source.write_text(code, encoding="utf8")
        subprocess.run([mcs, "-nologo", "-langversion:latest",
                        "-optimize+", "-out:" + str(exe), str(source)],
                       check=True, timeout=60)
        subprocess.run([mono, str(exe)], check=True, timeout=30)


if __name__ == "__main__":
    main()
