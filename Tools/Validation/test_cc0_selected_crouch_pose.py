#!/usr/bin/env python3
"""Source-level contract for CC0 crouch source selection and calibration.

This checks only branch wiring, not native Humanoid pose or Android framerate.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[2]
source = (root / "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text()

# Both CC0 clips must exist and be Humanoid before the game selects them.
assert "cc0CrouchLoaded = cc0CrouchIdle != null && cc0CrouchForward != null" in source
assert "cc0CrouchIdle.humanMotion && cc0CrouchForward.humanMotion" in source
assert "crouchClip = cc0CrouchIdle;" in source
assert "crouchWalkClip = cc0CrouchForward;" in source

# Selected CC0 idle must be sampled as its own loop, not the legacy
# KayKit kneeling transition at 10/24 of the source duration.
hold = source[source.index("crouchPlayable.SetSpeed(0d);"):source.index("pronePlayable.SetSpeed(0d);")]
assert "crouchPlayable.SetTime(cc0CrouchLoaded || naturalMocapLoaded ||" in hold
assert "alsCrouchLoaded || communityStanceClipsLoaded" in hold
assert "0d : crouchClip.length * (10d / 24d)" in hold

# CC0's already-crouched Humanoid pose must not inherit old FBX's 73cm
# visual root lowering when ALS/community resources are missing.
lowering = source[source.index("float crouchLowering ="):source.index("float proneLowering =")]
assert "cc0CrouchLoaded || naturalMocapLoaded ||" in lowering
assert "alsCrouchLoaded || communityStanceClipsLoaded ? 0f" in lowering
assert "RealisticSoldierCrouchRigLowering" in lowering

print("PASS: CC0 crouch source owns idle sample and avoids legacy rig lowering")
print("NOT TESTED: Unity native standing/crouching/prone appearance, transitions, Android FPS")
