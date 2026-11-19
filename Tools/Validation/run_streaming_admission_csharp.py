#!/usr/bin/env python3
"""Execute the entire production admission gate with controlled Unity API stubs.

Tests Android, Editor and desktop preprocessor variants. No Unity Editor,
native import timing, asset rendering or physical phone FPS is certified.
"""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from run_runtime_native_work_csharp import braced

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / 'Assets/AarisWorld/Scripts'


def fixture_source():
    runtime = (SRC / 'AarisMobileRuntime.cs').read_text()
    fixture = (ROOT / 'Tools/Validation/StreamingAdmissionTests.cs').read_text()
    # Run the actual GPU-name policy too, including vendor aliases and exact
    # model boundaries. Do not substitute a Python mirror or a fake detector.
    methods = (
        'private static bool IsBudgetAndroidGpu()',
        'internal static bool UseConstrainedGpuStreamingBudget()',
        'private static bool HasExactGpuModel(string description, string model)',
    )
    production = '\n'.join(signature + ' {' + braced(runtime, signature) + '}'
                           for signature in methods)
    assert fixture.count('/* GPU_POLICY */') == 1
    return fixture.replace('/* GPU_POLICY */', production)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--emit', type=Path)
    args = parser.parse_args()
    fixture = fixture_source()
    if args.emit:
        args.emit.write_text(fixture)
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
    with tempfile.TemporaryDirectory(prefix='aaris-streaming-admission-') as directory:
        path = Path(directory)
        gate = path / 'gate.cs'
        gate.write_text((SRC / 'AarisMobileStreamingFrameGate.cs').read_text())
        harness = path / 'fixture.cs'
        harness.write_text(fixture)
        for name, defines in (('android', 'UNITY_ANDROID'),
                              ('editor', 'UNITY_ANDROID,UNITY_EDITOR'),
                              ('desktop', None)):
            executable = path / (name + '.exe')
            command = compiler + ['-nologo', '-langversion:latest', '-optimize+',
                                  '-out:' + str(executable)]
            if defines:
                command.append('-define:' + defines)
            subprocess.run(command + [str(gate), str(harness)], check=True, timeout=60)
            subprocess.run([str(mono), str(executable)], check=True, timeout=60)


if __name__ == '__main__':
    main()
