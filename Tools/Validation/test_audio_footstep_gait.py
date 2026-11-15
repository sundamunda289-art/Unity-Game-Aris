#!/usr/bin/env python3
"""Verify footstep audio contacts follow character visual gait distance.

Static source + deterministic maths only. Real Unity animation and Android audio
timing require an Editor/device test, not a successful Python check.
"""
from __future__ import annotations

import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
audio = (ROOT / "Assets/AarisWorld/Scripts/AarisAudioDirector.cs").read_text("utf-8")
visual = (ROOT / "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text("utf-8")

# Vendored 100STYLE clips may replace the pose source, but locomotion remains
# capsule-authoritative and distance-driven. Missing/corrupt clips must fall
# back to the already-reviewed local animation set instead of breaking play.
for part in (
    'private const string MocapLocomotion = "AarisMocap/mocap_locomotion";',
    'Resources.LoadAll<AnimationClip>(MocapLocomotion)',
    'FindMocapClip(mocap, "mocap_walk")',
    'FindMocapClip(mocap, "mocap_jog")',
    'FindMocapClip(mocap, "mocap_run")',
    'FindMocapClip(mocap, "mocap_crouch")',
    'if (mocapWalk == null || mocapJog == null || mocapRun == null)',
    'walkClip = mocapWalk;',
    'runClip = mocapJog;',
    'sprintClip = mocapRun;',
    'runPhaseOffset = 0f;',
    'sprintPhaseOffset = 0f;',
    'animator.applyRootMotion = false;',
):
    assert part in visual, part
assert 'mocapLocomotionLoaded = true;' in visual
assert 'distance-driven gait, root motion disabled.' in visual

# The actual visual uses distance-driven gait phases; audio should play at
# alternating contacts (half the cycle), including crouch/prone movement.
for part in (
    "private float FootfallSpacing(float metresPerSecond, bool armed,",
    "float jogCeiling = player != null",
    "player.JogSpeedMetresPerSecond",
    "player.ArmedSprintSpeedMetresPerSecond",
    "player.UnarmedSprintSpeedMetresPerSecond",
    ": (armed ? 5.7f : 6.5f);",
    "float runStart = Mathf.Max(1.8f, jogCeiling * 0.55f);",
    "float sprintStart = Mathf.Max(jogCeiling * 0.95f,",
    "sprintCeiling * 0.72f);",
    "Mathf.Lerp(2.55f, 3.65f, sprintBlend)",
    "return cycleDistance * 0.5f;",
    "PlayerStance.Prone) return 0.45f;",
    "PlayerStance.Crouching) return 0.70f;",
    "if (stance != lastFootstepStance)",
    "stepDistance = 0f;",
    "FootfallSpacing(speed, combat != null && combat.IsArmed, stance)",
    "speed >= 4.0f ? 0.61f : 0.48f",
):
    assert part in audio, part
assert "speed > 5.4f" not in audio, "Unreachable old sprint sound threshold"
assert "float standStride = Mathf.Lerp(1.25f," in visual
assert "Mathf.Lerp(2.55f, 3.65f, sprintBlend)" in visual
assert "distance / standStride" in visual
assert "distance / 1.40f" in visual
assert "distance / 0.90f" in visual

# Contact timing is driven by the actual displacement of the collision
# capsule, not a capped animation blend delta. A 200 ms hitch or wall slide
# must not create an artificial change of footstep frequency.
assert "Vector3 frameTravel = motorPosition - lastMotorPosition;" in visual
assert "float resolvedGroundTravelMetres = motionHistoryReady && !movementDiscontinuity" in visual
assert "Mathf.Sqrt(frameTravel.x * frameTravel.x + frameTravel.z * frameTravel.z)" in visual
assert "float distance = resolvedGroundTravelMetres;" in visual
assert "float distance = horizontal * dt;" not in visual
assert "float motionDt = Time.deltaTime;" in visual
assert "(planarVelocity - lastPlanarVelocity) / motionDt" in visual
assert "Mathf.DeltaAngle(lastFacingYaw, currentYaw) / motionDt" in visual

# At normal refresh rates both integrations agree, but after a hitch the
# old min(deltaTime, .15) foot phase would lag actual movement.
for frame_dt in (1 / 120, 1 / 60, 1 / 30, 1 / 10, 0.20, 0.30):
    actual_metres = 6.5 * frame_dt
    stride = 3.65
    new_phase = actual_metres / stride
    old_phase = 6.5 * min(frame_dt, 0.15) / stride
    assert math.isclose(new_phase, actual_metres / stride)
    assert new_phase + 1e-12 >= old_phase
    if frame_dt > 0.15:
        assert new_phase > old_phase
# Teleport or vehicle displacement is never interpreted as a stride.
assert "motionHistoryReady && !movementDiscontinuity" in visual


def inv(a: float, b: float, v: float) -> float:
    return max(0.0, min(1.0, (v - a) / (b - a)))


def lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def footfall(speed: float, armed: bool, stance: str) -> float:
    if stance == "prone":
        return 0.45
    if stance == "crouch":
        return 0.70
    jog = 4.2
    sprint_ceiling = 5.7 if armed else 6.5
    run_start = max(1.8, jog * 0.55)
    sprint_start = max(jog * 0.95, sprint_ceiling * 0.72)
    run = inv(run_start, jog, speed)
    sprint = inv(sprint_start, sprint_ceiling, speed)
    return 0.5 * lerp(1.25, lerp(2.55, 3.65, sprint), run)


# Walking, both sprint speeds and both low stances must always play two
# contacts per visual loop. At 10 FPS no frame can produce fake extra steps.
for speed, armed, stance in (
    (1.0, False, "stand"),
    (4.2, False, "stand"),
    (5.7, True, "stand"),
    (6.5, False, "stand"),
    (2.4, False, "crouch"),
    (1.1, False, "prone"),
):
    contact_distance = footfall(speed, armed, stance)
    assert 0.44 <= contact_distance <= 1.83
    if stance == "crouch":
        assert math.isclose(contact_distance * 2, 1.40)
    elif stance == "prone":
        assert math.isclose(contact_distance * 2, 0.90)
    for fps in (10, 15, 20, 30, 60, 120):
        accumulator = 0.0
        contacts = 0
        for _ in range(4 * fps):
            accumulator += speed / fps
            if accumulator >= contact_distance:
                contacts += 1
                accumulator %= contact_distance
        expected = 4.0 * speed / contact_distance
        assert abs(contacts - math.floor(expected)) <= 1, (
            speed, armed, stance, fps, contacts, expected
        )

assert math.isclose(footfall(4.2, False, "stand"), 1.275)
assert math.isclose(footfall(5.7, True, "stand"), 1.825)
assert math.isclose(footfall(6.5, False, "stand"), 1.825)
print("PASS: sound contacts match two-foot gait cycle distance at 10–120 FPS")
print("NOT TESTED: Unity playable retarget, ground contact pixels, or Android audio output")
