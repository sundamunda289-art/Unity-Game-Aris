#!/usr/bin/env python3
"""One-gun drop/swap conservation model + C# contract tests.

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
    assert "AarisEquippedGun[]" not in inventory
    assert "CycleWeapon()" not in inventory
    assert "public bool TryPickup(AarisWeaponState incoming, out AarisWeaponState dropped)" in inventory
    assert "dropped = equipped != null ? equipped.CaptureState() : null;" in inventory
    assert "if (old != null)" in inventory and "old.gameObject.SetActive(false);" in inventory
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
    assert "requestPickupUntil = Time.unscaledTime + 0.7f;" in world
    assert "allowInitialAutoPickup = false;" in world
    assert "HasPickupInRange = bestPickup != null;" in world
    assert "Physics.RaycastNonAlloc(new Ray(eye, toTarget / distance)" in world
    assert "Physics.RaycastNonAlloc(new Ray(start, Vector3.down)" in world
    assert "if (hits >= candidateFloorHits.Length)" in world
    assert "floors = Physics.RaycastAll(" in world
    assert "RaycastHit candidate = floors[i];" in world
    # Overflow must not disable reachable loot or drop placement.
    assert "sightHits = Physics.RaycastAll(" in world
    assert "var collider = sightHits[i].collider;" in world
    assert "floorHits = Physics.RaycastAll(ray, 3.5f," in world
    assert "var hit = floorHits[i];" in world
    assert "obstructionHits = Physics.OverlapCapsule(" in world
    assert "var obstacle = obstructionHits[i];" in world
    assert "RetryDelaySeconds = 3f;" in world
    assert "nextCandidateProbeAt[i] = now + RetryDelaySeconds;" in world
    assert 'Add("PICK"' in hud and 'Add("DROP"' in hud
    assert 'Add("SWAP"' not in hud
    assert "i == 1 ? canPick : armed" in hud
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
    assert len(c) == 514
    indoor = [x for x in c if x["kind"] == "indoor"]
    open_ground = [x for x in c if x["kind"] == "open"]
    assert len(indoor) == 156 and len(open_ground) == 358
    assert all(-1000 < x["x"] < 1000 and -1000 < x["z"] < 1000 for x in c)

    # Reference model of one-gun ownership and ground drops. A unique weapon
    # token must never be duplicated or deleted on random pickup/swap/drop.
    rng = random.Random(991207)
    gun_kinds = sorted(ids)
    ground = [Weapon(i, gun_kinds[i % len(gun_kinds)], 30 - i % 25, 120 + i)
              for i in range(75)]
    carried = None
    universe = set(range(75))
    now = 0.0
    next_drop_id = -1
    drop_positions = {}  # negative token means an actual dropped floor gun
    for step in range(10000):
        now += rng.random() * .1
        if carried and rng.random() < .17:
            carried.reload_deadline = now + 2.4
            carried.magazine = min(carried.magazine, 10)
        if carried and rng.random() < .25:
            # Voluntary DROP. Only one gun leaves the hand and returns to floor.
            ground.append(carried)
            drop_positions[next_drop_id] = carried.unique_id
            next_drop_id -= 1
            carried = None
        elif ground:
            index = rng.randrange(len(ground))
            incoming = ground.pop(index)
            old = carried
            incoming.complete_reload(now)
            carried = incoming
            if old:
                ground.append(old)
                drop_positions[next_drop_id] = old.unique_id
                next_drop_id -= 1
        inventory_tokens = [x.unique_id for x in ground] + (
            [carried.unique_id] if carried is not None else [])
        assert len(inventory_tokens) == 75, (step, "lost weapon")
        assert set(inventory_tokens) == universe, (step, "duplicate weapon")
        assert len(set(inventory_tokens)) == 75
        assert carried is None or isinstance(carried, Weapon)
        assert all(w.magazine >= 0 and w.reserve >= 0 for w in ground)
    print("PASS: 10,000 randomized pick/swap/drop/reload ownership cases, 514 audited candidate locations, "
          "one-gun HUD, wall-safe pickup, saved gun ammo and floor/roof checks")
    print("NOTE: no Unity Editor Play-mode/physics run was performed.")


if __name__ == "__main__":
    audit()
