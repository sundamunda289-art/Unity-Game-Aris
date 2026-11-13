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

# The actual visual uses distance-driven gait phases; audio should play at
# alternating contacts (half the cycle), including crouch/prone movement.
for part in (
    "private static float FootfallSpacing(float metresPerSecond, bool armed,",
    "Mathf.InverseLerp(2.0f, 3.4f, metresPerSecond)",
    "armed ? 4.4f : 4.8f, metresPerSecond",
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


def inv(a: float, b: float, v: float) -> float:
    return max(0.0, min(1.0, (v - a) / (b - a)))


def lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def footfall(speed: float, armed: bool, stance: str) -> float:
    if stance == "prone":
        return 0.45
    if stance == "crouch":
        return 0.70
    run = inv(2.0, 3.4, speed)
    sprint = inv(4.0, 4.4 if armed else 4.8, speed)
    return 0.5 * lerp(1.25, lerp(2.55, 3.65, sprint), run)


# Walking, both sprint speeds and both low stances must always play two
# contacts per visual loop. At 10 FPS no frame can produce fake extra steps.
for speed, armed, stance in (
    (1.0, False, "stand"),
    (3.4, False, "stand"),
    (4.4, True, "stand"),
    (4.8, False, "stand"),
    (2.0, False, "crouch"),
    (1.0, False, "prone"),
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

assert math.isclose(footfall(3.4, False, "stand"), 1.275)
assert math.isclose(footfall(4.4, True, "stand"), 1.825)
assert math.isclose(footfall(4.8, False, "stand"), 1.825)
print("PASS: sound contacts match two-foot gait cycle distance at 10–120 FPS")
print("NOT TESTED: Unity playable retarget, ground contact pixels, or Android audio output")
