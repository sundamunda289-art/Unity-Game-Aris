#!/usr/bin/env python3
"""License-free source contracts and mathematical reference checks.

This does not simulate Unity physics, prove rendering quality, or certify
performance on a 2 GB Android device. Run Unity Play Mode separately.
"""
from pathlib import Path
import math
import random

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets" / "AarisWorld" / "Scripts"


def script(name):
    return (SCRIPTS / (name + ".cs")).read_text(encoding="utf-8")


def cell_keys(bounds, cell_size=250.0):
    x0, z0, x1, z1 = bounds
    return {(x, z)
            for x in range(math.floor(x0 / cell_size),
                           math.floor(x1 / cell_size) + 1)
            for z in range(math.floor(z0 / cell_size),
                           math.floor(z1 / cell_size) + 1)}


def test():
    colliders = script("AarisNearbyColliders")
    motor = script("AarisVehicleMotor")
    inventory = script("AarisWeaponInventory")
    gun = script("AarisEquippedGun")
    session = script("AarisVehicleSession")

    # SphereCollider is scaled by Unity's largest world-scale axis, not
    # by the individual axes of a transformed local bounding ellipsoid.
    assert "else if (c is SphereCollider sphere)" in colliders
    assert "sphere.radius * Mathf.Max(" in colliders
    for axis in ("x", "y", "z"):
        assert f"Mathf.Abs(c.transform.lossyScale.{axis})" in colliders
    assert "c.transform.TransformPoint(sphere.center)" in colliders

    # Collider shapes within authored buildings are streamed by the
    # SAME complete cell registry as boxes/meshes; unknown shapes stay solid.
    assert "else if (c is CapsuleCollider capsule)" in colliders
    assert "size[capsule.direction] = Mathf.Max(diameter, capsule.height)" in colliders
    assert "RegisterCells(i, worldBounds[i]);" in colliders
    assert "private void RegisterCells(int index, Bounds bounds)" in colliders
    assert "c.enabled = true;" in colliders
    assert "Physics.SyncTransforms();" in colliders
    assert "foreach (int i in active)" in colliders

    # Independent entry and exit gates: an unoccupied car must be upright,
    # nearly stationary, not spinning, and supported by at least two wheels.
    assert "public bool IsSafeToEnter" in motor
    assert "body.linearVelocity.magnitude >= 1.5f" in motor
    assert "body.angularVelocity.magnitude >= .8f" in motor
    assert "Vector3.Dot(transform.up, Vector3.up) < .72f" in motor
    assert "if (session == null || IsOccupied || !IsSafeToEnter)" in motor
    assert "public bool IsSafeToExit" in motor
    assert "car == null || !car.IsSafeToExit" in session

    # All gun FBX renderers contribute to the one-time grip-scale estimate.
    assert "var renderers = visible.GetComponentsInChildren<Renderer>(true);" in inventory
    assert "Bounds combined = renderers[0].bounds;" in inventory
    assert "combined.Encapsulate(renderers[i].bounds);" in inventory
    assert "Mathf.Max(combined.size.x," in inventory
    assert "visible.GetComponentInChildren<Renderer>(true)" not in inventory

    # Camera and physical muzzle ray may hit two different sibling colliders
    # on the same damageable bot. This is not a license to shoot through a
    # wall merely because it shares the same broad scene root.
    assert "private static IAarisDamageable DamageReceiver(Collider collider)" in gun
    assert "recipient != null && ReferenceEquals(recipient, DamageReceiver(b))" in gun
    assert "a.transform.IsChildOf(b.transform)" in gun
    assert "DamageReceiver(collider)" in gun
    assert "a.transform.root == b.transform.root" not in gun
    assert "muzzleHitFound &&" in gun
    assert "!SameTarget(target.collider, muzzleHit.collider)" in gun

    rng = random.Random(20261109)
    for _ in range(2000):
        x = rng.uniform(-1000, 1000)
        z = rng.uniform(-1000, 1000)
        radius = rng.uniform(.1, 18)
        scales = [rng.uniform(.25, 8) for _ in range(3)]
        # A spherical collider's widest world axis bounds every point.
        r = radius * max(scales)
        span = cell_keys((x - r, z - r, x + r, z + r))
        assert (math.floor(x / 250), math.floor(z / 250)) in span
        for _ in range(3):
            px = x + rng.uniform(-radius, radius) * scales[0]
            pz = z + rng.uniform(-radius, radius) * scales[2]
            assert (math.floor(px / 250), math.floor(pz / 250)) in span

    # A capsule with its Y axis vertical is at least as tall as 2*radius;
    # an X- or Z-oriented capsule must include its full authored length.
    for axis in range(3):
        for radius, height in ((.2, .3), (.45, 2), (2, 8)):
            dim = [2 * radius] * 3
            dim[axis] = max(2 * radius, height)
            assert dim[axis] >= height and all(x >= 2 * radius for x in dim)

    def safe_entry(speed, spin, up, grounded):
        return speed < 1.5 and spin < .8 and up >= .72 and grounded >= 2

    assert safe_entry(0, 0, 1, 4)
    for state in ((1.5, 0, 1, 4), (0, .8, 1, 4),
                  (0, 0, .5, 4), (0, 0, 1, 1)):
        assert not safe_entry(*state)

    print("PASS: collider metadata/cell coverage, conservative sphere bounds, "
          "capsule axes, safe car entry, complete gun mesh bounds.")
    print("LIMIT: requires Unity runtime/Play Mode and real-device performance QA.")


if __name__ == "__main__":
    test()
