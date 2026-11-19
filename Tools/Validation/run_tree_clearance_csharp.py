#!/usr/bin/env python3
"""Compile and execute the production C# spatial index (no Unity license needed)."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
production = ROOT / 'Assets/AarisWorld/Scripts/AarisTreeClearanceIndex.cs'
tests = ROOT / 'Tools/Validation/TreeClearanceIndexTests.cs'
editor_data = os.environ.get('AARIS_UNITY_EDITOR_DATA')
if editor_data:
    editor = Path(editor_data)
    mono = editor / 'MonoBleedingEdge/bin/mono'
    compiler = [str(mono), str(editor / 'MonoBleedingEdge/lib/mono/4.5/csc.exe')]
else:
    mono = shutil.which('mono')
    mcs = shutil.which('mcs')
    if not mono or not mcs:
        raise SystemExit('C# toolchain missing: install mono-mcs or set AARIS_UNITY_EDITOR_DATA')
    compiler = [mcs]
with tempfile.TemporaryDirectory(prefix='aaris-tree-clearance-') as directory:
    executable = Path(directory) / 'clearance-tests.exe'
    subprocess.run(compiler + ['-nologo', '-langversion:latest', '-optimize+',
                   '-out:' + str(executable), str(production), str(tests)],
                   check=True, timeout=60)
    subprocess.run([str(mono), str(executable)], check=True, timeout=60)
