#!/usr/bin/env python3
"""Exercise exact production AttachWheelModels through controlled C# API stubs.

No Unity Editor, IL2CPP, native physics, graphics or device profiling occurs.
"""
import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile

from run_runtime_native_work_csharp import braced

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "Assets/AarisWorld/Scripts/AarisVehicleMotor.cs"
FIXTURE = ROOT / "Tools/Validation/WheelPrefabAllocationTests.cs"


def fixture_source():
    code = SOURCE.read_text(encoding="utf8")
    template = FIXTURE.read_text(encoding="utf8")
    anchor = "public bool AttachWheelModels(GameObject wheelPrefab, Material palette,"
    body = braced(code, anchor)
    method = (
        "public bool AttachWheelModels(GameObject wheelPrefab, Material palette,\n"
        "            bool preserveAuthoredUrpMaterials = false)\n"
        "        {" + body + "}"
    )
    marker = "/* PRODUCTION_ATTACH_WHEEL_MODELS */"
    assert template.count(marker) == 1
    for token in (
        "wheelPrefabColliders.Clear();", "wheelPrefabRenderers.Clear();",
        "wheelPrefabSlotLayouts.Clear();",
        "instance.GetComponentsInChildren<Collider>(true, wheelPrefabColliders);",
        "instance.GetComponentsInChildren<Renderer>(true, wheelPrefabRenderers);",
        "wheelPrefabSlotLayouts.TryGetValue(n, out Material[] materials)",
        "wheelPrefabSlotLayouts.Add(n, materials);",
        "visualWheelCenterOffsets[i] =",
    ):
        assert token in body, token
    return template.replace(marker, method)


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
    with tempfile.TemporaryDirectory(prefix="aaris-wheel-prefab-") as td:
        code = Path(td) / "WheelPrefab.generated.cs"
        exe = Path(td) / "WheelPrefab.exe"
        code.write_text(source, encoding="utf8")
        subprocess.run([mcs, "-nologo", "-langversion:latest",
                        "-optimize+", "-out:" + str(exe), str(code)],
                       check=True, timeout=60)
        subprocess.run([mono, str(exe)], check=True, timeout=45)


if __name__ == "__main__":
    main()
