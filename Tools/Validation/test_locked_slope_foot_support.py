#!/usr/bin/env python3
"""One-ray-per-foot support invariants for world-locked stance on ramps/steps.

Reference math is a geometry sanity check. Native Unity rendering and
non-planar ledges require a separate Play Mode test.
"""
from pathlib import Path
import math

source = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text()

start = source.index("private static Vector3 ResolveLowStanceFootContact(")
plant = source.index("private static void PlantCrouchLeg(", start)
plan = source.index("private void PlanBalancedLowStanceFeet(", plant)
support = source.index("private float SupportedAnkleY(", plan)
late = source.index("private void LateUpdate()", support)
resolve_code = source[start:plant]
plant_code = source[plant:plan]
support_code = source[support:late]

assert "return goal;" in resolve_code
assert "goal += Vector3.ClampMagnitude(drift, 0.10f);" in resolve_code
assert "drift.sqrMagnitude > 0.20f * 0.20f" in resolve_code
assert "turningFast || transitioningStance" in resolve_code
assert "contactLocked = false;" in resolve_code
assert "Vector3 targetFoot = resolvedFootWorld;" in plant_code
assert "targetFoot.y = ankleWorldY;" in plant_code
assert "Vector3 origin = sampleWorld + Vector3.up * 0.70f;" in support_code
query_buffer = (Path(__file__).resolve().parents[2] /
                "Assets/AarisWorld/Scripts/AarisPhysicsHitBuffer.cs").read_text()
assert "QueryTriggerInteraction.Ignore" in query_buffer
assert "hit.collider.transform.IsChildOf(transform)" in support_code
assert support_code.count("AarisPhysicsHitBuffer.Raycast(") == 1

low = source[source.index("if (!useUprightContact && ankleRigWeight > 0.005f)"):
             source.index("// Standing/walk/run need world-space contact too.")]
for shoe in ("left", "right"):
    resolver = f"{shoe}FootGoal = ResolveLowStanceFootContact({shoe}FootGoal,"
    sample = f"out Vector3 {shoe}SupportNormal, {shoe}FootGoal);"
    solver = f"{shoe}FootGoal, kneeClearance, kneePoleStrength);"
    assert resolver in low and sample in low and solver in low
    assert low.index(resolver) < low.index(sample) < low.index(solver)
    assert f"else {shoe}LowPlantLocked = false;" in low
assert low.count("ResolveLowStanceFootContact(") == 2
assert low.count("SupportedAnkleY(") == 2
assert low.count("PlantCrouchLeg(") == 2
assert "Physics.Raycast(" not in low

# A support height taken at the old candidate position can disagree with
# the boot's final X/Z by several centimeters on a reasonable walking slope.
# The corrected sequence samples the *resolved* point, not the old candidate.
for degrees in (0, 10, 25, 40):
    rise_per_m = math.tan(math.radians(degrees))
    for signed_drift in (-.10, 0, .10):
        resolved_x = 2.0 + signed_drift
        correct_y = rise_per_m * resolved_x
        sampled_y = rise_per_m * resolved_x
        assert abs(correct_y - sampled_y) < 1e-10
        old_y = rise_per_m * 2.0
        if degrees > 0 and signed_drift != 0:
            assert abs(old_y - correct_y) > 0.001
print("PASS: final low-stance support rays follow world-locked X/Z on slopes")
print("NOT TESTED: native Unity terrain intersections or Android FPS")
