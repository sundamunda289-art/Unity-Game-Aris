#!/usr/bin/env python3
"""Exercise production audio/mixer methods in controlled fixtures, without Unity."""
import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
from run_runtime_native_work_csharp import braced

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / 'Assets/AarisWorld/Scripts'


def fixture_source():
    audio = (SRC / 'AarisAudioDirector.cs').read_text()
    rival = (SRC / 'AarisOfflineBRDirector.cs').read_text()
    drop = braced(audio, 'private void TickDrop(')
    engine = braced(audio, 'private void TickVehicle(')
    # Every real active phase must clear its retired-silence state, even when
    # the same clip/source is reused. These are exact production statements.
    drop_reset = re.findall(r'^            dropLoopsSettled = false;$', drop, re.M)
    engine_reset = re.findall(r'^            engineLoopSettled = false;$', engine, re.M)
    assert len(drop_reset) == len(engine_reset) == 1
    assert drop.index(drop_reset[0]) > drop.index('if (!activeDrop)')
    assert engine.index(engine_reset[0]) > engine.index('if (car == null)')
    assert 'visualBlend = 0f;\n            animations.SetInputWeight(0, 1f);' in rival
    assert 'graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);' in rival
    assert 'UpdateAnimationBlend(wanted, dt);' in rival
    template = (ROOT / 'Tools/Validation/IdleAudioAnimationTests.cs').read_text()
    items = {
        '/* ANIMATION_METHOD */': 'private void UpdateAnimationBlend(float wanted, float dt) {' +
            braced(rival, 'private void UpdateAnimationBlend(') + '}',
        '/* ADJUST_METHOD */': 'private static bool AdjustLoop(AudioSource source, float volume, float pitch, float dt) {' +
            braced(audio, 'private static bool AdjustLoop(') + '}',
        '/* BEGIN_METHOD */': 'private static void BeginLoop(AudioSource source, AudioClip clip) {' +
            braced(audio, 'private static void BeginLoop(') + '}',
        '/* INACTIVE_DROP */': braced(drop, 'if (!activeDrop)'),
        '/* INACTIVE_ENGINE */': braced(engine, 'if (car == null)'),
        '/* DROP_INVALIDATION */': drop_reset[0],
        '/* ENGINE_INVALIDATION */': engine_reset[0],
    }
    for marker, body in items.items():
        assert template.count(marker) == 1
        template = template.replace(marker, body)
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
    with tempfile.TemporaryDirectory(prefix='aaris-idle-audio-animation-unit-') as directory:
        path = Path(directory)
        (path / 'fixture.cs').write_text(source)
        executable = path / 'fixture.exe'
        subprocess.run(compiler + ['-nologo', '-langversion:latest', '-optimize+',
                       '-out:' + str(executable), str(path / 'fixture.cs')],check=True,timeout=60)
        subprocess.run([str(mono),str(executable)],check=True,timeout=60)


if __name__ == '__main__':
    main()
