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
assert "leftFootGoal = Vector3.Lerp(leftFootGoal," in section
assert "rightFootGoal = Vector3.Lerp(rightFootGoal," in section
assert "leftFootBone.position, authoredFootShare)" in section
assert "rightFootBone.position, authoredFootShare)" in section
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
assert blend(0.82, 0.99, 1.0) == 1.0

print("PASS: clip-authored crouch X/Z fades in without jumps, fades off for prone")
print("NOT TESTED: Unity 6000.3.26f1 bone/boot visual contacts and Android FPS")
