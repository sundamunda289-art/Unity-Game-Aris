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
assert "now - lastJumpStarted < 0.65f" in support
assert "Mathf.Min(downhillProbeDistance," in support
assert "controller.skinWidth * 2f" in support
assert "controller.radius * 0.25f" in support
assert "HasWalkableGroundWithinStep(seamGap)" in support
assert "Physics.SphereCast" in motor
assert "hit.normal.y >= Mathf.Cos(controller.slopeLimit * Mathf.Deg2Rad)" in motor
assert "!(motor != null ? motor.HasCloseWalkableStanceSupport()" in battle
assert "height > capsule.height && !HasRoomAbove(height)" in battle

def eligible(grounded, contact_age, jump_age, near_walkable_ground):
    return grounded or (
        contact_age <= .13 and jump_age >= .65 and near_walkable_ground
    )

assert eligible(True, 0, 100, False)
assert eligible(False, .08, 100, True)
assert not eligible(False, .08, .1, True)
assert not eligible(False, .15, 100, True)
assert not eligible(False, .06, 100, False)
print("PASS: supported stance seam without midair prone entry")
print("NOT TESTED: Unity rendered poses, native collision, Android FPS")
