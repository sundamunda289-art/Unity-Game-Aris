#!/usr/bin/env python3
"""Regression contract: a one-frame contact seam is not airborne permission.

Checks source contracts and a small gate model; Unity native Play Mode and
Android FPS must still be validated before merge.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[2]
motor = (root / "Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs").read_text()
battle = (root / "Assets/AarisWorld/Scripts/AarisBattleActions.cs").read_text()
start = motor.index("public bool HasCloseWalkableStanceSupport()")
end = motor.index("public bool IsInAir", start)
support = motor[start:end]
assert "controller == null || !controller.enabled" in support
assert "if (controller.isGrounded) return true;" in support
assert "now - lastPhysicalGroundContact > StanceGroundGraceSeconds" in support
assert "if (verticalVelocity > 0f) return false;" in support
assert "Mathf.Min(downhillProbeDistance," in support
assert "controller.skinWidth * 2f" in support
assert "controller.radius * 0.25f" in support
assert "HasWalkableGroundWithinStep(seamGap)" in support
assert "Physics.SphereCast" in motor
assert "hit.normal.y >= Mathf.Cos(controller.slopeLimit * Mathf.Deg2Rad)" in motor
assert "!(motor != null ? motor.HasCloseWalkableStanceSupport()" in battle
assert "height > capsule.height && !HasRoomAbove(height)" in battle

def eligible(grounded, contact_age, upwards, near_walkable_ground):
    # Rising overrides the previous-Move grounded bit. Real landing can
    # immediately enable prone, without an arbitrary post-jump cooldown.
    if upwards:
        return False
    return grounded or (
        contact_age <= .13 and near_walkable_ground
    )

assert eligible(True, 0, False, False)
assert eligible(True, 0, False, True)
assert not eligible(True, 0, True, True)
assert eligible(False, .08, False, True)
assert not eligible(False, .08, True, True)
assert not eligible(False, .15, False, True)
assert not eligible(False, .06, False, False)
print("PASS: supported stance seam without midair prone entry")
print("NOT TESTED: Unity rendered poses, native collision, Android FPS")
