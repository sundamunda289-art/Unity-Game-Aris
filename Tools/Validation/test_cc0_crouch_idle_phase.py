#!/usr/bin/env python3
"""CC0 crouch idle must not sample the older transition FBX's time offset.

The check intentionally verifies only the source selector; real Unity
humanoid import, retargeted rig and stance visuals require native QA.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[2]
src = (root / "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text()
assert "cc0CrouchLoaded = cc0CrouchIdle != null && cc0CrouchForward != null &&" in src
assert "crouchClip = cc0CrouchIdle;" in src
assert "crouchPlayable.SetSpeed(0d);" in src
start = src.index("crouchPlayable.SetTime(naturalMocapLoaded")
end = src.index("// The 24-frame KayKit Crawling", start)
phase = src[start:end]
assert "communityStanceClipsLoaded || cc0CrouchLoaded" in phase
assert "? 0d : crouchClip.length * (10d / 24d)" in phase

def time_for_crouch(asset_cc0, asset_als, asset_mit, asset_mocap, legacy_length):
    return 0.0 if any((asset_cc0, asset_als, asset_mit, asset_mocap)) else legacy_length * 10 / 24

assert time_for_crouch(True, False, False, False, 1.0) == 0
assert time_for_crouch(True, True, True, True, 2.0) == 0
assert time_for_crouch(False, False, False, False, 2.4) == 1.0
print("PASS: CC0 crouch idle uses authored zero phase; legacy FBX fallback unchanged")
print("NOT TESTED: native Unity rendered stance, walking or Android build")
