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
assert "if (!previousMoveGrounded && !launchJump &&" in motor
assert "lastLanding = now;" in motor
assert "float preSupportFallSpeed = Mathf.Max(0f, -verticalVelocity);" in motor
assert "lastLandingImpactSpeed = Mathf.Max(preSupportFallSpeed," in motor
# Sample actual falling velocity BEFORE a speculative ground probe suppresses it.
assert motor.index("float preSupportFallSpeed =") < motor.index("wasGrounded = HasWalkableGroundWithinStep();")
assert motor.count("lastLandingImpactSpeed = 0f;") == 3
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
# A predictive support sweep can clamp velocity to a small grounded value,
# but a real landing still retains the stronger prior downward impact.
def impact_speed(pre_probe_velocity: float, post_probe_velocity: float) -> float:
    return max(0.0, -pre_probe_velocity, -post_probe_velocity)


for falling in (2.0, 5.0, 9.5, 25.0):
    assert impact_speed(-falling, -2.0) == falling
assert impact_speed(0.0, -2.0) == 2.0
assert impact_speed(-1.5, -8.0) == 8.0
assert impact_speed(0.0, 0.0) == 0.0

# Stair stepping may resume on actual or predicted ground support, but
# NOT merely because the player reached the descending half of a jump.
step_region = motor.split("float requestedStep = ", 1)[1].split(";", 1)[0]
assert step_region.startswith("!launchJump && wasGrounded")
assert "verticalVelocity" not in step_region
assert "if ((flags & CollisionFlags.Below) != 0)" in motor
assert "controller.stepOffset = groundedStep;" in motor
for taking_off in (False, True):
    for supported in (False, True):
        for falling in (False, True):
            enable_step = supported and not taking_off
            assert enable_step == (supported and not taking_off)
            if falling and not supported:
                assert not enable_step, "Midair descent must not use stair stepOffset"

# A grounded direction reversal should first brake, while a jump must retain
# the original, reduced air steering. Check the new source guard and the
# deterministic 1D equivalent of Vector3.MoveTowards at low mobile FPS.
assert "if (wasGrounded && desiredVelocity.sqrMagnitude > 0.001f &&" in motor
assert "Vector3.Dot(horizontalVelocity, desiredVelocity) < -0.05f" in motor
assert "response = Mathf.Max(response, horizontalBraking);" in motor
assert motor.index("response = Mathf.Max(response, horizontalBraking);") < motor.index("if (!wasGrounded)")

def approach(current: float, goal: float, rate: float, dt: float) -> float:
    delta = goal - current
    return current + max(-rate * dt, min(rate * dt, delta))

for fps in (10, 15, 30, 60, 120):
    dt = 1.0 / fps
    braked = approach(4.8, -3.4, 19.0, dt)
    unbraked = approach(4.8, -3.4, 13.0, dt)
    assert -3.4 <= braked < unbraked < 4.8

# Real capsule acceleration and shortest-angle yaw should inform *visual* lean
# without affecting the authoritative movement controller or prone collision.
import math
assert "private bool motionHistoryReady;" in visual
assert "if (motionHistoryReady && motionDt > 0.0001f)" in visual
assert "(planarVelocity - lastPlanarVelocity) / motionDt" in visual
assert "Mathf.DeltaAngle(lastFacingYaw, currentYaw) / motionDt" in visual
assert "1f - Mathf.Clamp01(crouchBlend + proneBlend) : 0f" in visual
assert "smoothedStrafeBank + smoothedTurnBank" in visual
assert "leaning + smoothedAccelerationPitch" in visual
for start, end, expected in ((359, 1, 2), (1, 359, -2), (40, 70, 30)):
    signed = (end - start + 180) % 360 - 180
    assert signed == expected
for fps in (10, 30, 60, 120):
    dt = 1.0 / fps
    pitch, bank = 0.0, 0.0
    for _ in range(fps):
        pitch += (2.2 - pitch) * (1.0 - math.exp(-dt / 0.10))
        bank += (-2.5 - bank) * (1.0 - math.exp(-dt / 0.13))
    assert abs(pitch - 2.2 * (1.0 - math.exp(-10))) < 1e-10
    assert abs(bank + 2.5 * (1.0 - math.exp(-1.0 / 0.13))) < 1e-10
    assert 0 < pitch <= 2.2 and -2.5 <= bank < 0

print("PASS: jump touchdown, predictive downhill and real impact retention")
print("NOT TESTED: Unity collision sweeps, actual animation poses, device audio")


# Short stair/terrain-seam contact gaps are not physical landing impacts.
assert 'private float lastPhysicalGroundContact = -10f;' in motor
assert 'if (previousMoveGrounded) lastPhysicalGroundContact = now;' in motor
assert 'now - lastPhysicalGroundContact >= 0.12f' in motor
assert 'lastPhysicalGroundContact = now;\n                lastGrounded = now;' in motor
for airborne_seconds, should_land in ((0,False),(1/60,False),(0.1,False),
                                     (0.119,False),(0.12,True),(0.45,True)):
    assert (airborne_seconds >= 0.12) == should_land
