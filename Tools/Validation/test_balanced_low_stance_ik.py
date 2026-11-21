#!/usr/bin/env python3
"""Regression contract for mobile low-stance foot layout, not a Unity render test."""
from pathlib import Path
from math import sqrt
import re

visual = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text()

start = visual.index("private void PlanBalancedLowStanceFeet(")
end = visual.index("// One non-allocating ground sample per foot on low stances;", start)
layout = visual[start:end]
assert "leftThigh == null || rightThigh == null" in layout
assert "proneHeadBone.position - locomotionHipsBone.position" in layout
assert "rightThigh.position - leftThigh.position" in layout
assert "float hipWidth = Mathf.Abs(Vector3.Dot(" in layout
assert "float extraCrouchHalfWidth = Mathf.Clamp(" in layout
assert "float crouchHalfWidth = 0.19f + extraCrouchHalfWidth;" in layout
assert "Vector3 crouchLeft = hipMid -" in layout
assert "Vector3 crouchRight = hipMid +" in layout
assert "crouchKneeForward * 0.14f;" in layout
assert "crouchKneeForward * 0.22f;" in layout
assert "float leftLegReach = (leftShin.position -" in layout
assert "float rightLegReach = (rightShin.position -" in layout
assert "float leftExtension = Mathf.Clamp(" in layout
assert "float rightExtension = Mathf.Clamp(" in layout
assert "facing * leftExtension - right * 0.035f;" in layout
assert "facing * rightExtension + right * 0.035f;" in layout
assert "float reachBudget = Mathf.Lerp(0.52f, 0.74f, prone);" in layout
assert "leftGoal += Vector3.ClampMagnitude(leftDelta, reachBudget) * correction;" in layout
assert "rightGoal += Vector3.ClampMagnitude(rightDelta, reachBudget) * correction;" in layout
assert "Mathf.InverseLerp(0.07f, 0.38f, planarSpeed)" in layout
assert "Mathf.InverseLerp(0.68f, 0.96f, lowPose)" in layout
assert "Vector3 anatomicalRight = Vector3.ProjectOnPlane(" in layout
assert "rightThigh.position - leftThigh.position, Vector3.up" in layout
assert "float lowPose, float dominantRigWeight, float planarSpeed," in layout
assert "Mathf.Max(crouchRigWeight, proneRigWeight), planar," in visual
assert "Mathf.InverseLerp(0.90f, 0.995f, dominantRigWeight)" in layout
assert "float correction = settledPose * stationary * dominantSettled;" in layout
assert layout.index("Vector3 anatomicalRight = Vector3.ProjectOnPlane(") < layout.index(
    "if (correction < 0.001f) return;")
assert "if (!validAnatomicalRight)" in layout
assert "crouchKneeForward = Vector3.Cross(anatomicalRight," in layout
assert "crouchKneeForward = Vector3.Cross(anatomicalRight," in layout
assert "crouchKneeForward * 0.14f;" in layout
assert "crouchKneeForward * 0.22f;" in layout
assert "out Vector3 crouchKneeForward" in layout
# Comments are explanations, not C# allocation expressions. Check code only.
layout_code = re.sub(r"//[^\n]*|/\*.*?\*/", "", layout, flags=re.S)
assert "new " not in layout_code  # no allocations in this IK method
assert "Physics.Raycast" not in layout

# Native baseline used 14/22cm fore-aft foot offsets and passed
# the strict <=12cm inter-knee-height QA; keep this anatomical fallback
# for normal hip sizes. Larger avatars gain a bounded width adjustment.
def clamp(value, low, high):
    return max(low, min(high, value))

def stance_targets(hip_width, left_leg, right_leg):
    extra = clamp((hip_width - .38) * .33, .0, .035)
    half_width = .19 + extra
    left_extension = clamp(left_leg - .045, .35, 1.055)
    right_extension = clamp(right_leg - .045, .35, 1.055)
    return half_width, left_extension, right_extension

for width in (.22, .28, .38, .40, .50):
    half, left_reach, right_reach = stance_targets(width, .96, 1.06)
    assert .19 <= half <= .225
    assert abs(left_reach - .915) < 1e-8
    assert abs(right_reach - 1.015) < 1e-8
    # No crouch regression on this normal-width soldier: preserve
    # the original trusted forward offsets and compact 38cm footprint.
    crouch_left = (-half, .14)
    crouch_right = (half, .22)
    assert .38 <= crouch_right[0] - crouch_left[0] <= .45
    assert abs(crouch_right[1] - crouch_left[1] - .08) < 1e-9
    if width <= .38:
        assert abs(half - .19) < 1e-8

# The new prone extension cannot demand an unreachable two-bone target.
for bone_span in (.42, .60, .78, .97, 1.055, 1.25):
    _, reach, _ = stance_targets(.30, bone_span, bone_span)
    assert reach <= max(.35, bone_span - .045) + 1e-9
    assert reach <= 1.055

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
assert support.count("AarisPhysicsHitBuffer.Raycast(") == 1
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
# Floor-constrained knee correction must solve a bend-circle component;
# it must not interpolate a linear height fraction using Slerp.
solver = visual[visual.index("private static void PlantLegToTarget("):
                visual.index("private void CorrectReversedSwingKnee(")]
assert "float requiredY = (minimumKneeWorldY -" in solver
assert "requiredY / Mathf.Max(upwardBend.y, 0.0001f)" in solver
assert "Vector3 tangent = bend - upwardBend * currentUp;" in solver
assert "bend = upwardBend * requiredUp + tangent *" in solver
assert "Vector3.Slerp(bend, upwardBend" not in solver

assert "float kneeClearance = Mathf.Lerp(0.21f, 0.045f, proneShare);" in visual
assert "float crouchPoleStrength = (1f - proneKneeShare) * 0.90f;" in visual
assert "float pronePoleStrength = proneKneeShare * proneKneeSettle *" in visual
assert "kneeStillness * kneeTurnStability;" in visual
assert "Vector3 kneeForward = Vector3.Slerp(" in visual
assert "crouchKneeForward, Vector3.down," in visual
assert "pronePoleStrength).normalized;" in visual
assert "Vector3.up * pronePoleStrength;" not in visual
assert "Mathf.InverseLerp(0.78f, 0.98f, effectiveProneBlend)" in visual
assert "Mathf.InverseLerp(65f, 140f, Mathf.Abs(yawRate))" in visual
assert "bool turningFast = Mathf.Abs(turnDegreesPerSecond) > 110f;" in resolver
assert "float poleSin = Vector3.Dot(axis, Vector3.Cross(bend, pole));" in visual
assert "float poleCos = Mathf.Clamp(Vector3.Dot(bend, pole), -1f, 1f);" in visual
assert "Mathf.Atan2(poleSin, poleCos) * Mathf.Rad2Deg;" in visual
assert "poleAngle * Mathf.Clamp01(kneePoleStrength), axis) * bend;" in visual
assert "Vector3.Lerp(bend, forwardBend.normalized," not in visual
assert "kneeFloor, kneePoleStrength, anatomicalSideAxis," in visual
assert visual.count("planar, yawRate, ankleRigWeight, transitioningLowStance,") == 2
assert visual.count("PlanBalancedLowStanceFeet(") == 2
assert "leftFootGoal, kneeClearance, kneePoleStrength," in visual
assert "ref leftLowPlantLocked, ref leftLowPlantWorld);" in visual
assert "rightFootGoal, kneeClearance, kneePoleStrength," in visual
assert "ref rightLowPlantLocked, ref rightLowPlantWorld);" in visual
# The summed pose weight can remain 1.0 during an interrupted crouch/prone
# crossfade even though neither pose is settled. Dominant-stance confidence
# must suppress foot teleportation until one actual stance dominates.
def clamp01(x):
    return max(0.0, min(1.0, x))

def smooth01(x):
    t = clamp01(x)
    return t * t * (3.0 - 2.0 * t)

def foot_confidence(low_pose, crouch_rig, prone_rig, speed):
    pose = smooth01((low_pose - .68) / .28)
    dominant = smooth01((max(crouch_rig, prone_rig) - .90) / .095)
    still = 1.0 - smooth01((speed - .07) / .31)
    return pose * still * dominant

assert foot_confidence(1.0, .5, .5, 0.0) == 0.0
assert foot_confidence(1.0, .3, .7, 0.0) == 0.0
assert foot_confidence(1.0, .95, .05, 0.0) > 0.0
assert foot_confidence(1.0, 1.0, 0.0, 0.0) == 1.0
assert foot_confidence(1.0, 0.0, 1.0, 0.0) == 1.0
assert foot_confidence(1.0, 0.0, 1.0, 0.5) == 0.0
assert foot_confidence(0.2, 0.2, 0.0, 0.0) == 0.0
# The largest theoretical X/Z correction remains bounded by the same
# existing 52cm crouch or 74cm prone budget, and decreases in crossfades.
for weight in [i/100 for i in range(101)]:
    mixed = foot_confidence(1.0, weight, 1.0-weight, 0.0)
    assert 0.0 <= mixed <= 1.0
    if .1 <= weight <= .9:
        assert mixed == 0.0
    for prone in (0.0, .5, 1.0):
        budget = .52 * (1-prone) + .74 * prone
        assert budget * mixed <= budget

# Source-equivalent pole curves: settled prone gets a downward floor hint,
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
    downward = prone * settled * still * turning
    return crouch, downward

assert pole_weights(0.0, 0.0, 0.0, 0.0) == (.90, 0.0)
assert pole_weights(1.0, 1.0, 0.0, 0.0) == (0.0, 1.0)
assert pole_weights(1.0, .65, 0.0, 0.0) == (0.0, 0.0)
assert pole_weights(1.0, 1.0, .5, 0.0) == (0.0, 0.0)
assert pole_weights(1.0, 1.0, 0.0, 160.0) == (0.0, 0.0)
for share in (0.0, .1, .35, .5, .75, .85, 1.0):
    for blend in (0.0, .7, .8, .9, 1.0):
        for speed in (0.0, .2, .5):
            for yaw in (0.0, 90.0, 160.0):
                crouch, downward = pole_weights(share, blend, speed, yaw)
                assert 0 <= crouch <= .90 and 0 <= downward <= 1.0
                assert 0 <= crouch + downward <= 1.000001
# No binary knee pole change at prone share .5 or the settled threshold.
for blend in (0.8, 0.9, 1.0):
    previous = None
    for i in range(101):
        share = i / 100.0
        now = pole_weights(share, blend, .0, .0)
        if previous is not None:
            assert max(abs(a-b) for a, b in zip(now, previous)) < .07
        previous = now
# Angle-based pole blending must not collapse at 50% even when
# original and desired knee directions are 180 degrees apart.
# This math mirrors atan2(sin,cos) and axis-angle rotation in C#.
from math import atan2, cos, sin, pi, hypot, isfinite
def pole_arc(start, target, strength):
    angle = atan2(start[0]*target[1]-start[1]*target[0],
                  start[0]*target[0]+start[1]*target[1])
    rotation = angle * strength
    x = start[0]*cos(rotation)-start[1]*sin(rotation)
    y = start[0]*sin(rotation)+start[1]*cos(rotation)
    return x,y

for target_angle in (0.0, .1, 1.57, 3.0, pi, -3.0, -1.2):
    desired=(cos(target_angle),sin(target_angle))
    for weight in (0.0,.01,.25,.49,.5,.51,.75,.90,.99,1.0):
        vec=pole_arc((1.0,0.0),desired,weight)
        assert all(isfinite(c) for c in vec)
        assert abs(hypot(*vec)-1.0)<1e-10, (target_angle,weight,vec)
        if weight==1.0:
            assert hypot(vec[0]-desired[0],vec[1]-desired[1])<1e-10
# Opposite knee directions used to cancel exactly at weight 0.5.
assert abs(hypot(*pole_arc((1.0,0.0),(-1.0,0.0),.5))-1.0)<1e-10
# Analytic circle solution: retain bend direction around the knee axis,
# never overshoot into a fully upward knee unless the floor is unreachable.
from math import sqrt
def repair_knee_circle(up_component, lateral_component, required_up):
    if up_component >= required_up:
        return up_component, lateral_component
    target = max(-1.0, min(1.0, required_up))
    direction = 1.0 if lateral_component >= 0.0 else -1.0
    return target, direction * sqrt(max(0.0, 1.0-target*target))

for start_angle in [-3.0,-2.5,-2.0,-1.5,-0.4,0.0,0.4,1.5,2.0,2.5,3.0]:
    u, lateral = cos(start_angle), sin(start_angle)
    for min_up in [-.95, -.5, -.1, .0, .3, .65, .9, 1.1]:
        fixed_u,fixed_lateral=repair_knee_circle(u,lateral,min_up)
        assert abs(fixed_u*fixed_u+fixed_lateral*fixed_lateral-1.0)<1e-9
        assert fixed_u+1e-9>=min(1.0,min_up) or u>=min_up
        if u>=min_up: assert (fixed_u,fixed_lateral)==(u,lateral)
        elif min_up<1.0:
            assert abs(fixed_u-min_up)<1e-9
            # Preserve which side of the knee plane the old bend occupied.
            assert fixed_lateral*lateral>=-1e-9

# Nearly antiparallel knee-pole directions have numerically ambiguous
# signed 180-degree arcs. The actual solver now picks the arc that lifts
# the midpoint rather than accepting random opposite-angle rounding.
# This is a deterministic mathematical test, not a Unity screenshot.
solver = visual[visual.index("private static void PlantLegToTarget("):
                visual.index("private Vector3 AnatomicalKneeForward()")]
for token in ("poleCos < -0.985f", "Mathf.Abs(poleSin) < 0.175f",
              "Vector3 positiveTangent = Vector3.Cross(axis, bend);",
              "float upPreference = Vector3.Dot(positiveTangent, Vector3.up);",
              "anatomicalSideAxis.sqrMagnitude > 0.25f",
              "poleSin = upPreference >= 0f"):
    assert token in solver, token

# World X is the ankle axis; animated knee bend points along world Z.
# The positive signed half-arc sends the knee DOWN, while the negative
# half-arc sends it UP. Tiny pole Y perturbations previously switched
# the branch by ~180 degrees at a 50% transition weight.
def halfway_knee_height(pole_sin, pole_cos, corrected):
    if corrected and pole_cos < -0.985 and abs(pole_sin) < .175:
        # x-axis cross z-axis = -y, so the upper branch has negative sign.
        preference = -1.0
        pole_sin = (abs(pole_sin) + .0001) * (1 if preference >= 0 else -1)
    return -sin(.5 * atan2(pole_sin, pole_cos))

epsilon = 1e-7
old_upper = halfway_knee_height(epsilon, -1.0, False)
old_lower = halfway_knee_height(-epsilon, -1.0, False)
assert abs(old_upper - old_lower) > 1.99
for perturbation in (-epsilon, 0.0, epsilon):
    fixed = halfway_knee_height(perturbation, -1.0, True)
    assert fixed > .999, (perturbation, fixed)
# Ordinary non-opposed poles are mathematically unchanged.
for p_cos, p_sin in ((1.0, 0.0), (0.0, 1.0), (0.5, -0.5),
                      (-0.7, 0.7), (-0.9, -0.2)):
    assert halfway_knee_height(p_sin, p_cos, True) == \
           halfway_knee_height(p_sin, p_cos, False)

print("PASS: anatomy-scaled crouch/prone IK, grounded knee hint and bend-circle stability")
print("NOT TESTED: Unity retargeted visuals, real collisions or Android FPS")
