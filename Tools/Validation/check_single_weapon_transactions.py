#!/usr/bin/env python3
"""Two-slot hand/back holster conservation model + C# contract tests.

Not a real Unity physics test. Proves source invariants and exercises
10,000 independent inventory transitions against a minimal reference model.
"""
from dataclasses import dataclass
from pathlib import Path
import json
import random

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets/AarisWorld/Scripts"


def text(name):
    return (SRC / name).read_text(encoding="utf-8")


@dataclass
class Weapon:
    unique_id: int
    kind: str
    magazine: int
    reserve: int
    reload_deadline: float = 0.0

    def complete_reload(self, now):
        if self.reload_deadline and now >= self.reload_deadline:
            maximum = 30
            transfer = min(maximum - self.magazine, self.reserve)
            self.magazine += transfer
            self.reserve -= transfer
            self.reload_deadline = 0.0


def audit():
    inventory = text("AarisWeaponInventory.cs")
    world = text("AarisWeaponLootWorld.cs")
    pickup = text("AarisWeaponPickup.cs")
    gun = text("AarisEquippedGun.cs")
    hud = text("AarisWeaponHUD.cs")
    actions = text("AarisBattleActions.cs")
    assert "private AarisEquippedGun equipped;" in inventory
    assert "private AarisEquippedGun holstered;" in inventory
    assert "AarisEquippedGun[]" not in inventory
    assert "public bool SwitchWeapon()" in inventory
    assert "public void ToggleFists()" in inventory
    assert "public bool TryPickup(AarisWeaponState incoming, out AarisWeaponState dropped)" in inventory
    assert "dropped = holstered != null ? holstered.CaptureState() : null;" in inventory
    assert "if (evictedHolster != null)" in inventory
    assert "evictedHolster.gameObject.SetActive(false);" in inventory
    # A composite FBX cannot be sized from its first child Renderer.
    assert "var renderers = visible.GetComponentsInChildren<Renderer>(true);" in inventory
    # Real Unity Play Mode proved Renderer.bounds can be stale immediately
    # after changing the imported FBX scale. Measure each mesh corner in
    # root-local space once, before applying scale to hand or floor.
    assert "AarisWeaponMeshBounds.TryMeasure(root.transform," in inventory
    assert "Mathf.Max(combinedBounds.size.x" in inventory
    assert "combinedBounds.center * scale;" in inventory
    assert "renderer.bounds.size" not in inventory
    assert "internal static class AarisWeaponMeshBounds" in pickup
    assert "renderer.transform.TransformPoint(local)" in pickup
    assert "combat.Equip(null)" in inventory and "combat.SetFireHeld(false)" in inventory
    assert "public bool TryRemove(out AarisWeaponState removed)" in inventory
    assert "Configure(AarisWeaponState state)" in gun
    for field in ("Magazine", "Reserve", "ReloadUntil", "NextShotAt", "Reloading", "SpreadSeed"):
        assert "readonly " in gun and field in gun, field
    assert "public AarisWeaponState CaptureState()" in gun
    assert "RefreshReload();" in gun
    assert "public AarisWeaponState State => state;" in pickup
    assert "public bool Initialize(AarisWeaponState gunState, int index)" in pickup
    assert "private readonly List<AarisDroppedWeapon> dropped" in world
    assert "var outgoing = inventory.ReplacementOnPickup;" in world
    assert "if (outgoing != null && replacement == null)" in world
    assert "pickup.ReleaseReservation();" in world
    assert "pickup.CommitConsumed();" in world
    assert "public bool TryReserve()" in pickup
    reserve = world.index("CreateDropped(outgoing, pickup.transform.position, 1.25f,")
    commit = world.index("inventory.TryPickup(pickup.State, out AarisWeaponState exchanged)")
    assert reserve < commit, "World must reserve old weapon on floor before swap."
    assert "UndoDrop(replacement)" in world
    assert "if (id >= 0)" in world and "consumed[id] = true;" in world
    assert "DeleteDrop(id);" in world
    assert "TryFindDropSpot(out Vector3 point)" in world
    assert "inventory.TryRemove(out AarisWeaponState removed)" in world
    assert "rec.visible = null;" in world
    assert "if (record.visible != null) continue;" in world
    assert "Time.unscaledTime < record.blockedUntil" in world
    assert "(!inventory.HasGun && allowInitialAutoPickup &&" in world
    # PICK is time-buffered and forces the next scheduled physics refresh.
    assert "requestPickupUntil = Time.unscaledTime + 1.5f;" in world
    assert "nextRefresh = 0f;" in world
    assert "allowInitialAutoPickup = false;" in world
    assert "HasPickupInRange = bestPickup != null;" in world
    assert "Physics.RaycastNonAlloc(sightLine, pickupObstructions, distance" in world
    # Full hit sets remain mandatory on overflow; reuse 128-slot scratch
    # first so ordinary dense-house overflow does not allocate each time.
    assert "CompleteRaycastOverflow(sightLine, distance, out sightHits)" in world
    assert "Physics.RaycastNonAlloc(groundRay," in world
    assert "CompleteRaycastOverflow(groundRay, 3.3f, out floorHits)" in world
    assert "if (hits >= candidateFloorHits.Length)" in world
    assert "RaycastHit candidate = floorHits[i];" in world
    assert "CompleteRaycastOverflow(ray, 3.5f, out resolvedFloors)" in world
    assert "Physics.RaycastNonAlloc(ray, overflowRayHits, distance," in world
    assert "results = Physics.RaycastAll(ray, distance," in world
    assert "Physics.OverlapCapsuleNonAlloc(" in world
    assert "if (blockers >= overflowBlockers.Length)" in world
    assert "nearbyBlockers = Physics.OverlapCapsule(" in world
    assert "var obstacle = nearbyBlockers[i];" in world
    assert "RetryDelaySeconds = 3f;" in world
    assert "nextCandidateProbeAt[i] = now + RetryDelaySeconds;" in world
    assert 'Add("PICK"' in hud and 'Add("DROP"' in hud
    assert 'Add("SWAP"' in hud and 'Add("FIST"' in hud
    assert "i == 5 ? canSwap : armed" in hud
    # A KayKit punch locks the next swing through the full authored motion;
    # its delayed impact is sampled at the extension/contact frame, not
    # instantly on input, and must remain available with empty hands.
    assert "combat.Equip(null)" in inventory
    assert "nextAttack = lastPunchTime + Mathf.Max(punchInterval, PunchVisualDuration);" in actions
    assert "punchContactTime = lastPunchTime + PunchContactDelay;" in actions
    assert "if (punchContactTime >= 0f && Time.time >= punchContactTime)" in actions
    assert "punchContactTime = -1f;" in actions

    catalog = json.loads((ROOT / "Assets/Resources/AarisWeapons/weapon_catalog.json").read_text())
    ids = {x["id"] for x in catalog["weapons"]}
    assert len(ids) == 5
    points = json.loads((ROOT / "Assets/Resources/AarisWeapons/loot_candidates.json").read_text())
    c = points["candidates"]
    # The first 514 original candidate indices must never be renumbered.
    # New outdoor satellites are appended after the legacy source slots.
    assert len(c) > 514
    legacy = c[:514]
    indoor = [x for x in c if x["kind"] == "indoor"]
    open_ground = [x for x in c if x["kind"] == "open"]
    assert len([x for x in legacy if x["kind"] == "indoor"]) == 156
    assert len([x for x in legacy if x["kind"] == "open"]) == 358
    assert len(indoor) == 156 and len(open_ground) >= 1000
    assert all(x["kind"] == "open" and x["origin"] == "grid" and
               x["slot"] >= 100000 for x in c[514:])
    assert all(-1000 < x["x"] < 1000 and -1000 < x["z"] < 1000 for x in c)

    # Reference model: two physical slots, a hand gun and a back gun.
    # The third pickup evicts only the old back gun, never duplicating ammo.
    rng = random.Random(991207)
    gun_kinds = sorted(ids)
    ground = [Weapon(i, gun_kinds[i % len(gun_kinds)], 30-i % 25, 120+i)
              for i in range(75)]
    hand = back = None
    fists = False
    universe = set(range(75))
    now = 0.0
    for step in range(10000):
        now += rng.random() * .1
        if hand and rng.random() < .17:
            hand.reload_deadline = now + 2.4
            hand.magazine = min(hand.magazine, 10)
        action = rng.random()
        if action < .15 and hand:
            # Drop the hand weapon; the stowed one is promoted.
            ground.append(hand)
            hand, back = back, None
            fists = False
        elif action < .28 and hand:
            fists = not fists
        elif action < .43 and back:
            hand, back = back, hand
            fists = False
        elif ground:
            incoming = ground.pop(rng.randrange(len(ground)))
            incoming.complete_reload(now)
            if back:
                ground.append(back)
            back, hand = hand, incoming
            fists = False
        inventory_tokens = [x.unique_id for x in ground]
        inventory_tokens += [x.unique_id for x in (hand, back) if x is not None]
        assert len(inventory_tokens) == 75, (step, "lost weapon")
        assert set(inventory_tokens) == universe, (step, "duplicate weapon")
        assert len(set(inventory_tokens)) == 75
        assert back is None or hand is not None
        assert not fists or hand is not None
        assert all(w.magazine >= 0 and w.reserve >= 0 for w in ground)
    print("PASS: 10,000 randomized two-slot pick/holster/swap/drop/reload ownership cases, "
          "514 stable authored candidate locations, wall-safe pickup and saved ammo state")
    print("NOTE: static validation only; Unity Editor and Android tests still required.")


if __name__ == "__main__":
    audit()
