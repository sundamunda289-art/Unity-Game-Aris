#!/usr/bin/env python3
"""Execute actual production bodies in controlled C# fixtures, without Unity."""
import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / 'Assets/AarisWorld/Scripts'


def braced(text, anchor):
    start = text.index('{', text.index(anchor))
    depth = 0
    # These narrow production bodies contain no brace characters in strings.
    for i in range(start, len(text)):
        if text[i] == '{':
            depth += 1
        elif text[i] == '}':
            depth -= 1
            if not depth:
                return text[start+1:i]
    raise ValueError('Unclosed C# body')


def fixture_source():
    nearby = (SRC / 'AarisNearbyColliders.cs').read_text()
    refresh = braced(nearby, 'private void RefreshNow(bool forcePhysicsSync)')
    probe = braced(nearby, 'internal bool HasPotentialInactiveObstacle(')
    expressions = re.findall(r'if \((worldBounds\[i\]\.Intersects\(probe\)[\s\S]*?)\)\s*return true;', probe)
    assert len(expressions) == 2 and expressions[0].split() == expressions[1].split()
    director = (SRC / 'AarisOfflineBRDirector.cs').read_text()
    draw = 'private void DrawBoundary()\n{' + braced(director, 'private void DrawBoundary()') + '\n}'
    template = (ROOT / 'Tools/Validation/RuntimeNativeWorkTests.cs').read_text()
    for marker, text in {
        '/* GRID_BODY */': braced(refresh, 'foreach (int i in entries)'),
        '/* OVERSIZED_BODY */': braced(refresh, 'foreach (int i in oversizedIndices)'),
        '/* PROBE_EXPRESSION */ false': expressions[0],
        '/* DRAW_METHOD */': draw,
    }.items():
        assert template.count(marker) == 1, marker
        template = template.replace(marker, text)
    return template


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--emit', type=Path)
    args = parser.parse_args()
    source = fixture_source()
    if args.emit:
        args.emit.write_text(source)
        return
    editor_data = os.environ.get('AARIS_UNITY_EDITOR_DATA')
    if editor_data:
        editor = Path(editor_data)
        mono = editor / 'MonoBleedingEdge/bin/mono'
        compiler = [str(mono), str(editor / 'MonoBleedingEdge/lib/mono/4.5/csc.exe')]
    else:
        mono, mcs = shutil.which('mono'), shutil.which('mcs')
        if not mono or not mcs:
            raise SystemExit('Install mono-mcs or set AARIS_UNITY_EDITOR_DATA')
        compiler = [mcs]
    with tempfile.TemporaryDirectory(prefix='aaris-runtime-work-unit-') as directory:
        path = Path(directory)
        (path / 'fixture.cs').write_text(source)
        executable = path / 'fixture.exe'
        subprocess.run(compiler + ['-nologo', '-langversion:latest', '-optimize+',
                       '-out:' + str(executable), str(path / 'fixture.cs')],
                       check=True, timeout=60)
        subprocess.run([str(mono), str(executable)], check=True, timeout=60)


if __name__ == '__main__':
    main()
