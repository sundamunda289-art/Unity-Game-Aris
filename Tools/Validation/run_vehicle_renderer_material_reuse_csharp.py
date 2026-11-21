#!/usr/bin/env python3
"""Compile actual Unity car wheel-renderer and URP validation methods in stubs."""
import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile

from run_runtime_native_work_csharp import braced

ROOT=Path(__file__).resolve().parents[2]
SOURCE=ROOT/"Assets/AarisWorld/Scripts/AarisVehicleWorld.cs"
FIXTURE=ROOT/"Tools/Validation/VehicleRendererMaterialReuseTests.cs"


def fixture_source():
    code=SOURCE.read_text(encoding="utf8")
    template=FIXTURE.read_text(encoding="utf8")
    fields=[
        "[ThreadStatic] private static List<Renderer> validationRenderers;",
        "[ThreadStatic] private static List<Material> validationMaterials;"
    ]
    for field in fields:
        if code.count(field)!=1: raise ValueError("scratch declaration changed")
    signatures=[
        ("/* ACTUAL_RENDERABLE_BODY */",
         "private static bool HasRenderableVehicleBody(IReadOnlyList<Renderer> renderers)"),
        ("/* ACTUAL_VISIBLE_WHEEL */",
         "internal static bool HasRenderableWheelGeometry(Transform wheelRoot)"),
        ("/* ACTUAL_URP_COMPATIBILITY */",
         "private static bool HasCompatibleUrpMaterials(GameObject source)")
    ]
    marker="/* ACTUAL_SCRATCH_FIELDS */"
    if template.count(marker)!=1:raise ValueError("missing test field marker")
    template=template.replace(marker,"\n        ".join(fields))
    for marker,sig in signatures:
        if template.count(marker)!=1 or code.count(sig)!=1:
            raise ValueError("source signature or test marker mismatch "+sig)
        template=template.replace(marker,sig+"\n        {"+braced(code,sig)+"}")
    return template


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--emit",type=Path)
    args=parser.parse_args()
    code=fixture_source()
    if args.emit:
        args.emit.write_text(code,encoding="utf8")
        return
    mono,mcs=shutil.which("mono"),shutil.which("mcs")
    if not mono or not mcs:raise SystemExit("mono and mcs required")
    with tempfile.TemporaryDirectory(prefix="aaris-renderer-validation-") as td:
        file=Path(td)/"vehicle-renderer.cs"
        exe=Path(td)/"vehicle-renderer.exe"
        file.write_text(code,encoding="utf8")
        subprocess.run([mcs,"-nologo","-langversion:latest","-optimize+",
                        "-out:"+str(exe),str(file)],check=True,timeout=60)
        subprocess.run([mono,str(exe)],check=True,timeout=40)


if __name__=="__main__":
    main()
