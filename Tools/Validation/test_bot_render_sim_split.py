#!/usr/bin/env python3
"""Source-contract and deterministic policy check; not a Unity/Android FPS test."""
from pathlib import Path

SOURCE = Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs"
source = SOURCE.read_text(encoding="utf-8")
contracts = (
    "private const int RivalCount = 100;",
    "private const float FarVisualDistance = 485f;",
    "private const float VirtualEntryDistance = 185f;",
    "private const float VirtualExitDistance = 145f;",
    "private const float PhysicalHitboxInterestDistance = 220f;",
    "private const float OffscreenRigKeepDistance = 48f;",
    "scopedHitbox = gameObject.AddComponent<CapsuleCollider>();",
    "scopedHitbox.enabled = false;",
    "skinned.updateWhenOffscreen = false;",
    "animator.cullingMode = AnimatorCullingMode.CullCompletely;",
    "(!cameraRelevant && playerDistanceSqr >= 165f * 165f)",
    "(cameraRelevant && playerDistanceSqr < 165f * 165f)",
    "if (virtualBody)",
    "TickVirtualCombat(now, dt);",
    "scope",
    "return bot == attackerOne || bot == attackerTwo;",
    "targetHealth.ApplyDamage(damage, gameObject);",
)
for item in contracts:
    assert item in source, "Missing bot virtualization contract: " + item

def policy(distance, camera, was_virtual, lod_visible=True):
    sqr = distance * distance
    enter = sqr >= 185 * 185 or (not camera and sqr >= 165 * 165)
    leave = sqr <= 145 * 145 or (camera and sqr < 165 * 165)
    is_virtual = (was_virtual and not leave) or (not was_virtual and enter)
    hitbox = is_virtual and (
        sqr < 220 * 220 or
        (camera and sqr < 485 * 485 and lod_visible)
    )
    rig = sqr < 155 * 155 and (camera or sqr < 48 * 48)
    return is_virtual, hitbox, rig

assert policy(1000, False, False) == (True, False, False)
assert policy(1000, True, False) == (True, False, False)
assert policy(300, False, False) == (True, False, False)
assert policy(300, True, False) == (True, True, False)
assert policy(190, False, False) == (True, True, False)
assert policy(180, False, False) == (True, True, False)
assert policy(180, True, True) == (True, True, False)
assert policy(160, True, True) == (False, False, False)
assert policy(160, False, True) == (True, True, False)
assert policy(100, True, True) == (False, False, True)
assert policy(100, False, True) == (False, False, False)
assert policy(25, False, False) == (False, False, True)

# 100 deterministic world participants, with only a few inside the expensive
# near-field. Every far virtual participant retains a logical simulation slot.
positions = [25, 75, 140, 175, 190, 250, 300, 480, 600, 1000] * 10
cameras = [(i % 3) == 0 for i in range(100)]
states = [policy(d, cameras[i], False) for i, d in enumerate(positions)]
assert len(states) == 100
assert sum(1 for v, _, _ in states if v) >= 65
assert all(not h and not rig for d, (v, h, rig) in zip(positions, states) if d >= 600)
assert all(not rig for d, (_, _, rig) in zip(positions, states) if d >= 190)
print("PASS: 100 logical fighters; offscreen render culling; distant hitbox not movement")
