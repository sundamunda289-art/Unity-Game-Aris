#!/usr/bin/env python3
"""Execute production C# query policy with a controlled backend, outside Unity."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
editor_data = os.environ.get('AARIS_UNITY_EDITOR_DATA')
if editor_data:
    editor = Path(editor_data)
    mono = editor / 'MonoBleedingEdge/bin/mono'
    compiler = [str(mono), str(editor / 'MonoBleedingEdge/lib/mono/4.5/csc.exe')]
else:
    mono, mcs = shutil.which('mono'), shutil.which('mcs')
    if not mono or not mcs:
        raise SystemExit('Install mono-mcs or set AARIS_UNITY_EDITOR_DATA for unit tests')
    compiler = [mcs]
with tempfile.TemporaryDirectory(prefix='aaris-physics-query-unit-') as directory:
    executable = Path(directory) / 'physics-tests.exe'
    subprocess.run(compiler + ['-nologo', '-langversion:latest', '-optimize+',
                   '-out:' + str(executable),
                   str(ROOT / 'Assets/AarisWorld/Scripts/AarisPhysicsHitBuffer.cs'),
                   str(ROOT / 'Tools/Validation/PhysicsHitBufferTests.cs')],
                   check=True, timeout=60)
    subprocess.run([str(mono), str(executable)], check=True, timeout=60)
