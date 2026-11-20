#!/usr/bin/env python3
"""Static source/weight contract for Quaternius crouch foot contact.

This is NOT a substitute for native Unity multi-angle and movement QA.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[2]
visual = (root / "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text()
start = visual.index("if (cc0CrouchLoaded && crouching && !visuallyAirborne &&")
end = visual.index("// The old <0.5 binary knee pole", start)
section = visual[start:end]

assert "leftFootBone != null && rightFootBone != null" in section
assert "Mathf.InverseLerp(0.82f, 0.99f, effectiveCrouchBlend)" in section
assert "Mathf.InverseLerp(0.02f, 0.18f, proneShare)" in section
assert "leftThigh != null && rightThigh != null" in section
assert "pelvisRight = Vector3.ProjectOnPlane(" in section
assert "rightThigh.position - leftThigh.position" in section
assert "pelvisRight.magnitude * 0.28f, 0.035f, 0.11f" in section
assert "leftFootBone.position - leftFootGoal" in section
assert "rightFootBone.position - rightFootGoal" in section
assert "Vector3.ClampMagnitude(" in section
assert "authoredLeftDelta, footAllowance) * authoredFootShare" in section
assert "authoredRightDelta, footAllowance) * authoredFootShare" in section
assert "leftFootGoal = Vector3.Lerp" not in section
assert "rightFootGoal = Vector3.Lerp" not in section
assert "Physics." not in section
assert "new " not in "\n".join(s.split("//", 1)[0] for s in section.splitlines())

# Equivalent scalar smoothstep, evaluated at the exact compiled C# thresholds.
def blend(low, high, x):
    t = min(1.0, max(0.0, (x - low) / (high - low)))
    return t * t * (3.0 - 2.0 * t)

for crouch in (0, 0.5, 0.82, 0.85, 0.90, 0.98, 0.99, 1):
    weights = []
    for prone in (0, 0.02, 0.05, 0.10, 0.15, 0.18, 0.5, 1):
        weight = blend(0.82, 0.99, crouch) * (1.0 - blend(0.02, 0.18, prone))
        assert 0.0 <= weight <= 1.0
        if crouch <= 0.82 or prone >= 0.18:
            assert weight == 0.0
        weights.append(weight)
    assert weights == sorted(weights, reverse=True)
# A settled humanoid crouch boot cannot be retargeted by more than
# the rig-derived per-shoe X/Z allowance, never more than 11 cm.
for hip_width in (0.03, 0.1, 0.25, 0.38, 0.6, 1.0):
    allowance = max(0.035, min(0.11, hip_width * 0.28))
    assert 0.035 <= allowance <= 0.11
    for requested_m in (-2.0, -0.5, 0, 0.06, 0.3, 3.0):
        displacement = max(-allowance, min(allowance, requested_m))
        assert abs(displacement) <= allowance + 1e-12

assert blend(0.82, 0.99, 1.0) == 1.0

print("PASS: CC0 crouch X/Z clip delta bounded by pelvis width, fading off for prone")
print("NOT TESTED: Unity 6000.3.26f1 bone/boot visual contacts and Android FPS")
