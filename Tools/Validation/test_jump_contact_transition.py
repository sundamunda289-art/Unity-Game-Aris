#!/usr/bin/env python3
"""Prevent predictive downhill support from eating real touchdown events.

This models movement/controller state transitions independently; native Unity
collision and landing footage still need Editor/Android verification.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
motor = (ROOT / "Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs").read_text("utf-8")
visual = (ROOT / "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text("utf-8")
audio = (ROOT / "Assets/AarisWorld/Scripts/AarisAudioDirector.cs").read_text("utf-8")

assert "bool previousMoveGrounded = controller.isGrounded;" in motor
assert "bool wasGrounded = previousMoveGrounded;" in motor
assert "wasGrounded = HasWalkableGroundWithinStep();" in motor
assert "if (!previousMoveGrounded && !launchJump)" in motor
assert "lastLanding = now;" in motor
assert "lastLandingImpactSpeed = Mathf.Max(0f, -verticalVelocity);" in motor
assert "if (!wasGrounded && !launchJump) lastLanding = now;" not in motor
assert "if ((flags & CollisionFlags.Below) != 0)" in motor
assert "if (player.LastLandingTime > previousLand &&" in audio
assert "Time.unscaledTime - playerMotor.LastLandingTime" in visual
assert "jumpLandPlayable.SetTime(0d);" in visual

# Contact from the real CharacterController.Move must win over a predictive
# probe that says ground is close but has not yet been collided with.
def landed(real_previous_ground: bool, predictive_ground: bool,
           below_contact: bool, new_jump: bool) -> bool:
    was_grounded = real_previous_ground or predictive_ground
    # This is intentionally not gated by 'was_grounded': it includes the
    # ahead-of-time downhill prediction, not the prior physical contact.
    return below_contact and (not real_previous_ground) and (not new_jump)


for real_prev in (False, True):
    for predicted in (False, True):
        for contact in (False, True):
            for jump in (False, True):
                expected = contact and not real_prev and not jump
                assert landed(real_prev, predicted, contact, jump) == expected

assert landed(False, True, True, False), "Downhill probe must not swallow landing"
assert not landed(False, True, False, False), "No impact before a real contact"
assert not landed(True, True, True, False), "Standing must not replay impact"
assert not landed(False, False, True, True), "Jump initiation must not fake landing"
print("PASS: jump touchdown, predictive downhill and no duplicate landing event")
print("NOT TESTED: Unity collision sweeps, actual animation poses, device audio")
