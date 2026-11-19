#!/usr/bin/env python3
"""Static regression for upright contact continuity on stair/terrain seams.

This is a source contract, NOT an Editor Play Mode or Android visual test.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[2]
visual = (root / "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text()
start = visual.index("bool useUprightContact =")
end = visual.index("if (!useUprightContact &&", start)
gate = visual[start:end]
assert "!visuallyAirborne && graphReady &&" in gate, gate
assert "groundedNow &&" not in gate, "Transient isGrounded drops must retain support IK"
assert "lowPose < 0.02f" in gate
assert "!crouching && !prone && lowPose < 0.18f" in gate

# Same stable 130ms grace used for gait phase must gate foot IK.
start = visual.index("bool visuallyAirborne =")
end = visual.index("float takeoffStanceSuppression", start)
airborne = visual[start:end]
assert "now - lastReliableGroundContact > 0.13f" in airborne
assert "(deliberateJump ||" in airborne

start = visual.index("private void PlantUprightContact(")
end = visual.index("private void AlignFootToSupport(", start)
plant = visual[start:end]
assert "SupportedAnkleY(foot, UprightAnkleClearanceMetres" in plant
assert "if (float.IsNaN(supportY))" in plant
assert "plantLocked = false;" in plant
assert "CorrectReversedSwingKnee" in plant
assert "bool turningFast =" in plant
assert "gap > UprightPlantReleaseGapMetres" in plant

start = visual.index("private float SupportedAnkleY(")
end = visual.index("private struct AnkleSupportSample", start)
support = visual[start:end]
assert "float floor = float.NaN;" in support
assert "hit.normal.y < minWalkableNormalY" in support
assert "hit.collider.transform.IsChildOf(transform)" in support
assert support.count("AarisPhysicsHitBuffer.Raycast(") == 1

# Modeling only the gate: brief seam loss can keep IK when ray support exists,
# but deliberate/sustained flight or an unsupported ledge cannot plant a boot.
cases = [
    # visually_airborne, low_pose, transitioning_to_stand, ray_supported, expected
    (False, 0.0, False, True, True),
    (False, 0.0, False, False, False),
    (True, 0.0, False, True, False),
    (False, 0.12, True, True, True),
    (False, 0.3, True, True, False),
    (False, 1.0, False, True, False),
]
for air, pose, leaving_stance, supported, expected in cases:
    gated = (not air) and (pose < .02 or (leaving_stance and pose < .18))
    assert (gated and supported) == expected
print("PASS: upright foot IK uses grounded grace, real support and stance handoff")
print("NOT TESTED: Unity retargeted knee/ankle visuals or Android frame rate")
