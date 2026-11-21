#!/usr/bin/env python3
"""Compile and execute the production authored wheel name lookup in Mono stubs."""
import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile

from run_runtime_native_work_csharp import braced

ROOT = Path(__file__).resolve().parents[2]
WORLD = ROOT / 'Assets/AarisWorld/Scripts/AarisVehicleWorld.cs'
FIXTURE = ROOT / 'Tools/Validation/AuthoredWheelNameBufferTests.cs'


def fixture_source():
    source = WORLD.read_text(encoding='utf8')
    template = FIXTURE.read_text(encoding='utf8')
    scratch = '[ThreadStatic] private static List<Transform> namedWheelQueryScratch;'
    signature = 'private static Transform FindNamedWheel(Transform root, string token)'
    assert source.count(scratch) == 1
    assert source.count(signature) == 1
    assert template.count('/* PRODUCTION_SCRATCH_FIELD */') == 1
    assert template.count('/* PRODUCTION_WHEEL_NAME_LOOKUP */') == 1
    return template.replace('/* PRODUCTION_SCRATCH_FIELD */', scratch).replace(
        '/* PRODUCTION_WHEEL_NAME_LOOKUP */',
        signature + '\n        {' + braced(source, signature) + '}'
    )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--emit', type=Path)
    args = parser.parse_args()
    source = fixture_source()
    if args.emit:
        args.emit.write_text(source, encoding='utf8')
        return
    mono, mcs = shutil.which('mono'), shutil.which('mcs')
    if not mono or not mcs:
        raise SystemExit('Mono and mcs are required')
    with tempfile.TemporaryDirectory(prefix='aaris-wheel-name-') as td:
        path, exe = Path(td)/'name.cs', Path(td)/'name.exe'
        path.write_text(source, encoding='utf8')
        subprocess.run([mcs, '-nologo','-langversion:latest',
                        '-optimize+', '-out:'+str(exe),str(path)],check=True,timeout=60)
        subprocess.run([mono,str(exe)],check=True,timeout=30)


if __name__ == '__main__':
    main()
