#!/usr/bin/env python3
"""Regression contract for mobile low-stance foot layout, not a Unity render test."""
from pathlib import Path
from math import sqrt

visual = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text()

start = visual.index("private void PlanBalancedLowStanceFeet(")
end = visual.index("// One non-allocating ground sample per foot on low stances;", start)
layout = visual[start:end]
assert "leftThigh == null || rightThigh == null" in layout
assert "proneHeadBone.position - locomotionHipsBone.position" in layout
assert "rightThigh.position - leftThigh.position" in layout
assert "Vector3 crouchLeft = hipMid - right * 0.19f - facing * 0.06f;" in layout
assert "Vector3 crouchRight = hipMid + right * 0.19f + facing * 0.07f;" in layout
assert "Vector3 proneLeft = hipMid - facing * 0.73f - right * 0.16f;" in layout
assert "Vector3 proneRight = hipMid - facing * 0.73f + right * 0.16f;" in layout
assert "leftGoal += Vector3.ClampMagnitude(leftDelta, 0.42f) * correction;" in layout
assert "rightGoal += Vector3.ClampMagnitude(rightDelta, 0.42f) * correction;" in layout
assert "Mathf.InverseLerp(0.07f, 0.38f, planarSpeed)" in layout
assert "Mathf.InverseLerp(0.68f, 0.96f, lowPose)" in layout
assert "new " not in layout  # value-type Vector3 operations; no managed allocations
assert "Physics.Raycast" not in layout

# Foot-layout footprint is symmetric around its pelvic midpoint.
# Source FBX thigh ordering and forward direction must both be respected.
for direction in ((0.0, 1.0), (1.0, 0.0), (0.6, -0.8)):
    forward_x, forward_z = direction
    right_x, right_z = forward_z, -forward_x
    crouch_l = (-right_x * .19 - forward_x * .06,
                -right_z * .19 - forward_z * .06)
    crouch_r = (right_x * .19 + forward_x * .07,
                right_z * .19 + forward_z * .07)
    prone_l = (-forward_x * .73 - right_x * .16,
               -forward_z * .73 - right_z * .16)
    prone_r = (-forward_x * .73 + right_x * .16,
               -forward_z * .73 + right_z * .16)
    dist = lambda a, b: sqrt((a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2)
    assert .40 <= dist(crouch_l, crouch_r) <= .41
    assert abs(dist(prone_l, prone_r) - .32) < .00001
    assert abs((prone_l[0] + prone_r[0]) / 2 + forward_x * .73) < 1e-6
    assert abs((prone_l[1] + prone_r[1]) / 2 + forward_z * .73) < 1e-6

support = visual[visual.index("private float SupportedAnkleY("):
                 visual.index("private void PlantUprightContact(")]
assert "out Vector3 supportNormal, Vector3 sampleWorld)" in support
assert "Vector3 origin = sampleWorld + Vector3.up * 0.70f;" in support
assert support.count("Physics.RaycastNonAlloc(") == 1
assert "float floor = float.NaN;" in support
assert "hit.collider.transform.IsChildOf(transform)" in support
assert "hit.normal.y < minWalkableNormalY" in support

crouch = visual[visual.index("private static void PlantCrouchLeg("):
                visual.index("private void PlanBalancedLowStanceFeet(")]
assert "targetFoot.x = desiredFootWorld.x;" in crouch
assert "targetFoot.z = desiredFootWorld.z;" in crouch
assert "targetFoot.y = ankleWorldY;" in crouch
assert "float kneeFloor = ankleWorldY + 0.02f;" in crouch
assert "bool turningFast = Mathf.Abs(turnDegreesPerSecond) > 110f;" in crouch
assert "Vector3.Lerp(bend, forwardBend.normalized, 0.90f)" in visual
assert visual.count("ankleRigWeight, planar, yawRate,") == 2
assert visual.count("PlanBalancedLowStanceFeet(") == 2
assert "leftFootGoal, ref leftLowPlantLocked" in visual
assert "rightFootGoal, ref rightLowPlantLocked" in visual
print("PASS: compact crouch and aligned prone two-foot layout with reused support rays")
print("NOT TESTED: Unity retargeted visuals, real collisions or Android FPS")
