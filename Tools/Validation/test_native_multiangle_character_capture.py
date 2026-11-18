#!/usr/bin/env python3
"""Source-level contract for opt-in Unity Play Mode multi-angle character photos.

This does not certify rendered output: actual Unity captures and pose inspection
must be checked separately.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
source = (ROOT / "Assets/AarisWorld/Editor/AarisPoseFrames.cs").read_text(
    encoding="utf-8"
)

assert "public static void CaptureMultiAnglePortrait()" in source
assert 'SessionState.SetBool("pose.multiangle",true);' in source
assert "CapturePortrait();" in source
assert 'SessionState.EraseBool("pose.multiangle");' in source
assert 'bool multiangle=portrait&&SessionState.GetBool("pose.multiangle",false);' in source
assert 'bool orbitStance=multiangle&&n<=2;' in source
assert 'int needed=portrait?(n==5?2:orbitStance?4:1)' in source
assert 'orbitStance?.52+count*.22:.52' in source
assert 'orbitStance?1.40:.70' in source
assert 'Quaternion.AngleAxis(90f*count,Vector3.up)*p.transform.forward' in source
assert 'viewDegrees="+(orbitStance?90*count:0)' in source
assert "SUCCESS_16_ACTOR_MULTIANGLE_PORTRAITS" in source

# Stages 0, 1, 2: standing/crouch/prone. Non-stance stages keep the
# existing single angle; the jump stage still has two timed frames.
for multi in (False, True):
    expected = [4, 4, 4, 1, 1, 2] if multi else [1, 1, 1, 1, 1, 2]
    actual = [2 if n == 5 else 4 if multi and n <= 2 else 1
              for n in range(6)]
    assert expected == actual
    if multi:
        assert [90 * i for i in range(4)] == [0, 90, 180, 270]
        assert [0.52 + i * 0.22 for i in range(4)][-1] < 1.40
assert sum([4, 4, 4, 1, 1, 2]) == 16
assert sum([1, 1, 1, 1, 1, 2]) == 7
print("PASS: optional 16-view native portrait contract; original seven retained")
print("NOT TESTED: Unity camera render, rig pose visibility or Android performance")
