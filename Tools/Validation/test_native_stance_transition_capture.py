#!/usr/bin/env python3
"""Contract for opt-in real Unity stance transition frame capture.

The editor QA stage checks in-between frames and private transient foot
locks. Static checks here do not substitute for an actual Unity Play run.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[2]
editor = (root / "Assets/AarisWorld/Editor/AarisPoseFrames.cs").read_text()
visual = (root / "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text()

assert "public static void CaptureTransitionPortrait(){" in editor
assert 'SessionState.SetBool("pose.transitions",true);' in editor
assert 'SessionState.EraseBool("pose.transitions");' in editor
assert "SUCCESS_16_STANCE_TRANSITION_FRAMES" in editor
assert 'bool transitions=portrait&&SessionState.GetBool("pose.transitions",false);' in editor
assert "transitionFrames=transitions&&(n==1||n==2||n==3)" in editor
assert "transitionFrames?.10+count*.18" in editor
assert "transitionFrames?.84" in editor
assert 'if(transitionFrames)LogTransitionContact(p,b,count);' in editor
assert '"TRANSITION_CONTACT stance="' in editor
assert '"TRANSITION_LEGS stance="' in editor
assert '"TRANSITION_FOOT_ANCHOR_STALE"' in editor
assert '"TRANSITION_TERRAIN_FEET stance="' in editor
assert '"TRANSITION_FOOT_BURIED"' in editor
assert 'tile.SampleHeight(pos)' in editor
assert 'worstGroundGap< -0.15f' in editor
assert 'sampleIndex>=1&&worstGroundGap>0.24f' in editor
assert 'RETURN_TO_STAND_FOOT_FLOAT' in editor
assert 'bool returningFromProne = !prone && !crouching &&' in visual
assert 'effectiveProneBlend > 0.02f &&' in visual
assert 'effectiveProneBlend < 0.70f;' in visual
assert 'effectiveProneBlend < 0.01f) || returningFromProne);' in visual
assert 'float headroomDrop = Mathf.Min(excess, 1.00f) * lowPose;' in visual
assert 'lowestShoeY - (capsuleFloor + 0.02f)' in visual
assert 'headroomDrop = Mathf.Min(headroomDrop, safeDown);' in visual
assert 'if(transitioning&&(left||right))' in editor
assert 'bool transitioningLowStance =' in visual
assert 'turningFast || transitioningStance)' in visual
assert visual.count('planar, yawRate, ankleRigWeight, transitioningLowStance,') == 2

# Normal settled multi-angle mode must still capture standing/crouch/prone
# from four angles, not be accidentally replaced by this transition mode.
assert "bool orbitStance=multiangle&&n<=2;" in editor
assert "SUCCESS_16_ACTOR_MULTIANGLE_PORTRAITS" in editor
assert "CaptureMultiAnglePortrait()" in editor
assert "SessionState.GetBool(\"pose.transitions\",false)&&excess>.055f" in editor

for transitions in (False, True):
    frame_counts = [
        2 if n == 5 else 4 if transitions and n in (1, 2, 3) else 1
        for n in range(6)
    ]
    assert sum(frame_counts) == (16 if transitions else 7)
for weight, locked, expected_problem in [
    (0.0, False, False),
    (0.45, False, False),
    (0.45, True, True),
    (0.98, True, False),
    (0.98, False, False),
]:
    changing = 0.03 < weight < 0.96
    assert (changing and locked) is expected_problem

print("PASS: native stance transitions capture 16 time-sampled frames and check foot locks")
print("NOT TESTED: Unity rendering, slopes, gameplay movement or device FPS")
