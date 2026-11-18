#!/usr/bin/env python3
"""Source and numerical regression for third-person sprint pivot foot cadence.

Gait direction is a visual-only animation clock; the player movement motor,
collision capsule, camera and other gameplay mechanics are unchanged.
"""
from pathlib import Path

source = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text(
              encoding="utf-8"
          )
start = source.index("bool sustainedSprint = sprintBlend >= 0.35f &&")
end = source.index("float phaseDirection = gaitDirection;", start)
body = source[start:end]
assert "horizontal >= Mathf.Max(2.8f, jogCeiling * 0.92f)" in body
assert "if (horizontal < 0.06f || sustainedSprint)" in body
assert "else if (forwardSpeed < -0.32f &&" in body
assert "-forwardSpeed > sideSpeed * 0.75f" in body
assert "gaitDirection = -1f;" in body
assert "sideSpeed > -forwardSpeed * 1.40f" in body
assert "new " not in body  # No per-frame managed allocations.
assert "Physics." not in body  # Presentation must not introduce queries.

def gait(horizontal, sprint_blend, forward_speed, side_speed,
         jog_ceiling=4.2, previous=1.0):
    sustained = (sprint_blend >= 0.35 and
                 horizontal >= max(2.8, jog_ceiling * 0.92))
    if horizontal < 0.06 or sustained:
        return 1.0
    if forward_speed < -0.32 and -forward_speed > side_speed * 0.75:
        return -1.0
    if forward_speed > -0.08 or side_speed > -forward_speed * 1.40:
        return 1.0
    return previous

cases = [
    # stopped -> natural next step, ordinary forward run and walk
    ((0.0, 0.0, 0.0, 0.0), 1),
    ((2.4, 0.0, 2.1, 0.2), 1),
    # genuine slow backpedal still reverses the continuous clip sample
    ((1.8, 0.0, -1.7, 0.1), -1),
    ((2.1, 0.0, -1.1, 0.2), -1),
    # fast sprint + abrupt 180 degree yaw -> OLD world momentum behind yaw
    ((5.4, 0.91, -5.0, 0.3), 1),
    ((5.1, 0.52, -4.8, 0.6), 1),
    # high-speed sideways turn should not play backwards
    ((5.2, 0.73, -0.5, 5.0), 1),
    # jog at a high camera angle should remain smooth
    ((2.9, 0.0, -0.28, 2.8), 1),
    # strong backward movement while NOT sprinting still reverses
    ((3.2, 0.1, -3.0, 0.1), -1),
]
for args, expected in cases:
    actual = gait(*args)
    assert actual == expected, (args, actual, expected)

# On the same travel displacement, a sprint pivot must not wind the
# authored gait phase backward, even across the 1.0 -> 0.0 wrap.
for start_phase in (0.03, 0.48, 0.98):
    direction = gait(5.4, 0.8, -5.0, 0.1)
    delta = direction * (0.15 / 3.65)
    phase = (start_phase + delta) % 1
    assert abs((phase - start_phase) % 1 - delta) < 1e-9

print("PASS: sprint pivot keeps forward stride phase; slow backpedal preserved")
print("NOT TESTED: native rendering, rapid-turn player input or Android FPS")
