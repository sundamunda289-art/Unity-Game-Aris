#!/usr/bin/env python3
"""Settled CC0 prone heel-target reach regression.

This is synthetic anatomical geometry. Real Unity Play Mode and phone profiling
remain independent requirements.
"""
from pathlib import Path
from math import sqrt

repo = Path(__file__).resolve().parents[2]
source = (repo / "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text()
start = source.index("// A nearly straight horizontal heel goal")
end = source.index("Vector3 leftGoal = leftThigh.position", start)
section = source[start:end]
for token in (
    "float commonReach = Mathf.Min(leftReach, rightReach);",
    "float safeReach = Mathf.Max(0.10f, commonReach - 0.012f);",
    "controller.center - Vector3.up *",
    "(controller.height * 0.5f)",
    "leftThigh.position.y - capsuleFootY",
    "rightThigh.position.y - capsuleFootY",
    "safeReach * safeReach -",
    "worstDrop * worstDrop",
    "Mathf.Min(",
    "commonReach * 0.985f, horizontalReach",
):
    assert token in section, token
assert "Physics." not in section and "new " not in section
assert section.count("Mathf.Sqrt") == 1

def horizontal_goal(reach_left, reach_right, left_delta_y, right_delta_y):
    reach = min(reach_left, reach_right)
    safe_reach = max(.10, reach - .012)
    drop = max(abs(left_delta_y), abs(right_delta_y))
    return min(reach * .985,
               sqrt(max(.0025, safe_reach**2 - drop**2)))

left_len, right_len = .3517 + .3566, .3516 + .3564
previous_horizontal = min(left_len, right_len) * .985
recorded_vertical_drop = .28
assert sqrt(previous_horizontal**2 + recorded_vertical_drop**2) > min(left_len, right_len), (
    "Recorded 0.28m vertical transition should exceed the old 3D reach"
)
for dy in (0, .08, .15, .25, .28, .35, .45, .60):
    h = horizontal_goal(left_len, right_len, dy, dy)
    d = sqrt(h*h + dy*dy)
    assert d <= min(left_len, right_len) - .005, (dy, h, d)
assert horizontal_goal(left_len, right_len, .28, .30) < previous_horizontal - .04
assert horizontal_goal(left_len, right_len, 0, 0) <= previous_horizontal
assert "if (cc0SettledProne &&" in source
print("PASS: stationary CC0 prone 3D reach preserved across 0-60cm drops; no new ground raycast")
print("NOT TESTED: native Unity terrain contact or FPS")
