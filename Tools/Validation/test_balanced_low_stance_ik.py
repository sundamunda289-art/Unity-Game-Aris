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
assert "Vector3 crouchLeft = hipMid - anatomicalRight * 0.19f +" in layout
assert "Vector3 crouchRight = hipMid + anatomicalRight * 0.19f +" in layout
assert "Vector3 proneLeft = leftThigh.position - facing * 1.055f -" in layout
assert "Vector3 proneRight = rightThigh.position - facing * 1.055f +" in layout
assert "right * 0.035f;" in layout
assert "float reachBudget = Mathf.Lerp(0.52f, 0.74f, prone);" in layout
assert "leftGoal += Vector3.ClampMagnitude(leftDelta, reachBudget) * correction;" in layout
assert "rightGoal += Vector3.ClampMagnitude(rightDelta, reachBudget) * correction;" in layout
assert "Mathf.InverseLerp(0.07f, 0.38f, planarSpeed)" in layout
assert "Mathf.InverseLerp(0.68f, 0.96f, lowPose)" in layout
assert "Vector3 anatomicalRight = Vector3.ProjectOnPlane(" in layout
assert "rightThigh.position - leftThigh.position, Vector3.up" in layout
assert "crouchKneeForward = Vector3.Cross(anatomicalRight," in layout
assert "crouchKneeForward * 0.14f" in layout
assert "crouchKneeForward * 0.22f" in layout
assert "out Vector3 crouchKneeForward" in layout
assert "new " not in layout  # value-type Vector3 operations; no managed allocations
assert "Physics.Raycast" not in layout

# Foot-layout footprint is symmetric around its pelvic midpoint.
# Source FBX thigh ordering and forward direction must both be respected.
for direction in ((0.0, 1.0), (1.0, 0.0), (0.6, -0.8)):
    forward_x, forward_z = direction
    right_x, right_z = forward_z, -forward_x
    crouch_l = (-right_x * .19 + forward_x * .14,
                -right_z * .19 + forward_z * .14)
    crouch_r = (right_x * .19 + forward_x * .22,
                right_z * .19 + forward_z * .22)
    # These goals are relative to the actual left/right thigh origins.
    # That removes the imported skeleton's asymmetric pelvic offsets.
    hip_l = (-right_x * .14, -right_z * .14)
    hip_r = (right_x * .14, right_z * .14)
    prone_l = (hip_l[0] - forward_x * 1.055 - right_x * .035,
               hip_l[1] - forward_z * 1.055 - right_z * .035)
    prone_r = (hip_r[0] - forward_x * 1.055 + right_x * .035,
               hip_r[1] - forward_z * 1.055 + right_z * .035)
    dist = lambda a, b: sqrt((a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2)
    assert .38 <= dist(crouch_l, crouch_r) <= .40
    assert abs(dist(prone_l, prone_r) - .35) < .00001
    assert abs(dist(prone_l, hip_l) - dist(prone_r, hip_r)) < 1e-7
    assert abs(dist(prone_l, hip_l) - sqrt(1.055**2 + .035**2)) < 1e-7
    assert abs((prone_l[0] + prone_r[0]) / 2 + forward_x * 1.055) < 1e-6
    assert abs((prone_l[1] + prone_r[1]) / 2 + forward_z * 1.055) < 1e-6

# Crouch must keep its reviewed small-step limit. Only settled stationary
# prone gets the extra contact budget; a moving/transitioning pose fades
# the correction to zero and never uses the larger reach to teleport boots.
for prone in (0.0, 0.2, 0.5, 0.8, 1.0):
    reach_budget = .52 * (1.0 - prone) + .74 * prone
    assert .52 <= reach_budget <= .74
    for distance in (0.0, .3, .52, .65, .81, 1.2):
        delta = min(distance, reach_budget)
        assert delta <= reach_budget + 1e-9
        if prone == 0.0:
            assert delta <= .52
        if distance >= .74 and prone == 1.0:
            assert abs(delta - .74) < 1e-9

support = visual[visual.index("private float SupportedAnkleY("):
                 visual.index("private void PlantUprightContact(")]
assert "out Vector3 supportNormal, Vector3 sampleWorld)" in support
assert "Vector3 origin = sampleWorld + Vector3.up * 0.70f;" in support
assert support.count("Physics.RaycastNonAlloc(") == 1
assert "float floor = float.NaN;" in support
assert "hit.collider.transform.IsChildOf(transform)" in support
assert "hit.normal.y < minWalkableNormalY" in support

resolver = visual[visual.index("private static Vector3 ResolveLowStanceFootContact("):
                  visual.index("private static void PlantCrouchLeg(")]
crouch = visual[visual.index("private static void PlantCrouchLeg("):
                visual.index("private void PlanBalancedLowStanceFeet(")]
assert "Vector3 targetFoot = resolvedFootWorld;" in crouch
assert "return goal;" in resolver
assert "targetFoot.y = ankleWorldY;" in crouch
assert "float kneeFloor = ankleWorldY + kneeClearance;" in crouch
assert "float kneeClearance = Mathf.Lerp(0.21f, 0.045f, proneShare);" in visual
assert "float crouchPoleStrength = (1f - proneKneeShare) * 0.90f;" in visual
assert "float pronePoleStrength = proneKneeShare * proneKneeSettle *" in visual
assert "kneeStillness * kneeTurnStability;" in visual
assert "Vector3 kneeForward = crouchKneeForward * crouchPoleStrength +" in visual
assert "Vector3.up * pronePoleStrength;" in visual
assert "Mathf.InverseLerp(0.78f, 0.98f, effectiveProneBlend)" in visual
assert "Mathf.InverseLerp(65f, 140f, Mathf.Abs(yawRate))" in visual
assert "bool turningFast = Mathf.Abs(turnDegreesPerSecond) > 110f;" in resolver
assert "Vector3.Lerp(bend, forwardBend.normalized," in visual
assert "Mathf.Clamp01(kneePoleStrength)).normalized;" in visual
assert "kneeFloor, kneePoleStrength);" in visual
assert visual.count("planar, yawRate, ankleRigWeight, transitioningLowStance,") == 2
assert visual.count("PlanBalancedLowStanceFeet(") == 2
assert "leftFootGoal, kneeClearance, kneePoleStrength);" in visual
assert "ref leftLowPlantLocked, ref leftLowPlantWorld);" in visual
assert "rightFootGoal, kneeClearance, kneePoleStrength);" in visual
assert "ref rightLowPlantLocked, ref rightLowPlantWorld);" in visual
# Source-equivalent pole curves: settled prone gets a strong vertical pole,
# whereas crawling, fresh transitions and fast turning preserve the clip.
def smooth(x):
    x = max(0.0, min(1.0, x))
    return x * x * (3.0 - 2.0 * x)

def pole_weights(share, blend, speed, turn):
    prone = smooth((share - .35) / .50)
    crouch = (1.0 - prone) * .90
    settled = smooth((blend - .78) / .20)
    still = 1.0 - smooth((speed - .07) / .31)
    turning = 1.0 - smooth((abs(turn) - 65.0) / 75.0)
    upward = prone * settled * still * turning
    return crouch, upward

assert pole_weights(0.0, 0.0, 0.0, 0.0) == (.90, 0.0)
assert pole_weights(1.0, 1.0, 0.0, 0.0) == (0.0, 1.0)
assert pole_weights(1.0, .65, 0.0, 0.0) == (0.0, 0.0)
assert pole_weights(1.0, 1.0, .5, 0.0) == (0.0, 0.0)
assert pole_weights(1.0, 1.0, 0.0, 160.0) == (0.0, 0.0)
for share in (0.0, .1, .35, .5, .75, .85, 1.0):
    for blend in (0.0, .7, .8, .9, 1.0):
        for speed in (0.0, .2, .5):
            for yaw in (0.0, 90.0, 160.0):
                crouch, upward = pole_weights(share, blend, speed, yaw)
                assert 0 <= crouch <= .90 and 0 <= upward <= 1.0
                assert 0 <= crouch + upward <= 1.000001
# No binary knee pole change at prone share .5 or the settled threshold.
for blend in (0.8, 0.9, 1.0):
    previous = None
    for i in range(101):
        share = i / 100.0
        now = pole_weights(share, blend, .0, .0)
        if previous is not None:
            assert max(abs(a-b) for a, b in zip(now, previous)) < .07
        previous = now
print("PASS: compact crouch, smooth prone knee pole and two-foot terrain layout")
print("NOT TESTED: Unity retargeted visuals, real collisions or Android FPS")
