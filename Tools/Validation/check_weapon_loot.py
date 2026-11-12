#!/usr/bin/env python3
"""Fail closed when offline gun assets, matches or renderer bindings drift.

No C# compiler/Unity Editor is available in this job. Native FBX bytes and
actual game-time physics/animation require separate Unity import/playmode QA.
"""
from __future__ import annotations
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MODELS = ROOT / "Assets/Resources/AarisWeapons/Models"
EXPECTED = {
    "SMG": (70348, "2fadbba99ac6d3373b8f6b04786374c42c54394f"),
    "AssaultRifle": (92204, "8f2109f415755ad0af8aaa1d911ddfea7c2064cd"),
    "BattleRifle": (91644, "24f2bfee4166e2208cb070b3de273f0989fafc9d"),
    "Sniper": (86636, "a743e79ea184966021aba937d30cb6876e2cbaba"),
    "Shotgun": (60604, "3590c4b06c378037190edd870ca0c24fdaf5ae8a"),
}
SCRIPTS = ROOT / "Assets/AarisWorld/Scripts"


def run():
    catalog = json.loads((ROOT / "Assets/Resources/AarisWeapons/weapon_catalog.json").read_text())
    ids = [entry["id"] for entry in catalog["weapons"]]
    assert ids == list(EXPECTED), ids
    for gun in catalog["weapons"]:
        name = gun["id"]
        assert gun["modelResource"] == "AarisWeapons/Models/" + name
        assert 20 <= gun["rpm"] <= 1300 and gun["magazineSize"] > 0
        assert gun["reserveAmmo"] >= 0 and gun["reloadSeconds"] > 0
        assert gun["pellets"] >= 1 and gun["damage"] > 0 and gun["weight"] > 0
        assert gun["adsFov"] >= 8 and gun["adsFov"] <= 65
        path = MODELS / (name + ".fbx")
        assert path.exists(), "Real local mesh is missing: " + str(path)
        assert path.stat().st_size == EXPECTED[name][0], "Not fully hydrated or source changed: " + name
        assert path.read_bytes()[:18].startswith(b"Kaydara FBX Binary"), "Not an FBX binary: " + name
        assert subprocess.check_output(["git", "hash-object", str(path)]).decode().strip() == EXPECTED[name][1]
        assert (MODELS / (name + ".fbx.meta")).exists(), name + " GUID missing"
    assert catalog["weapons"][3]["automatic"] is False
    assert catalog["weapons"][4]["pellets"] > 1
    loot = json.loads((ROOT / "Assets/Resources/AarisWeapons/loot_candidates.json").read_text())
    assert loot["schema"] == 1 and loot["sizeMetres"] == 2000
    source = json.loads((ROOT / "Docs/MAP_LAYOUT.json").read_text())
    origins = {x["name"] for x in source["buildings"]}
    assert any(x["kind"] == "indoor" for x in loot["candidates"])
    assert any(x["kind"] == "open" for x in loot["candidates"])
    assert all(x["origin"] in origins if x["kind"] == "indoor" else x["origin"] == "grid"
               for x in loot["candidates"])
    import importlib.util
    module_path = ROOT / "Tools/Weapons/bake_loot.py"
    spec = importlib.util.spec_from_file_location("aaris_bake_loot", module_path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    rebuilt = mod.build(source)
    if rebuilt != loot:
        diffs = [(i, a, b) for i, (a, b) in enumerate(zip(rebuilt["candidates"], loot["candidates"])) if a != b]
        raise AssertionError(f"Authored candidate drift, counts={len(rebuilt['candidates'])}/{len(loot['candidates'])}, examples={diffs[:6]}")

    catalog_cs = (SCRIPTS / "AarisWeaponCatalog.cs").read_text()
    gun_cs = (SCRIPTS / "AarisEquippedGun.cs").read_text()
    inventory = (SCRIPTS / "AarisWeaponInventory.cs").read_text()
    pickup_cs = (SCRIPTS / "AarisWeaponPickup.cs").read_text()
    world = (SCRIPTS / "AarisWeaponLootWorld.cs").read_text()
    player = (SCRIPTS / "AarisThirdPersonPlayer.cs").read_text()
    action = (SCRIPTS / "AarisBattleActions.cs").read_text()
    hud = (SCRIPTS / "AarisWeaponHUD.cs").read_text()
    assert "JsonUtility.FromJson<AarisWeaponCatalogDocument>" in catalog_cs
    assert "public static GameObject ModelFor(AarisWeaponSpec spec)" in catalog_cs
    assert "Resources.Load<GameObject>(modelPath)" in catalog_cs
    assert "modelCache[modelPath] = prefab;" in catalog_cs
    # One missing or renderer-less Android gun FBX must not disable all
    # other imported weapons. Invalid catalog data still fails closed.
    assert "var available = new List<AarisWeaponSpec>(doc.weapons.Length);" in catalog_cs
    assert "GetComponentInChildren<Renderer>(true) == null" in catalog_cs
    assert 'Debug.LogWarning("Aaris: unavailable local weapon model; skipping "' in catalog_cs
    assert 'Debug.LogWarning("Aaris: weapon model has no renderer; skipping "' in catalog_cs
    assert "if (available.Count == 0)" in catalog_cs
    assert "specs = available.ToArray();" in catalog_cs
    assert 'throw new FormatException("Invalid weapon entry at " + i)' in catalog_cs
    assert 'throw new FormatException("Duplicate weapon identifier.")' in catalog_cs
    assert "TryFirstOtherHit" not in gun_cs # renamed and rewritten, not a second weapon motor
    assert "FirstExternalHit" in gun_cs and "SameTarget" in gun_cs
    assert "magazine--;" in gun_cs and "nextFire = Time.time + spec.ShotInterval;" in gun_cs
    assert "BeginReload()" in gun_cs and "reloading" in gun_cs
    assert "semiShotQueued" in action and "equippedGun.Automatic" in action
    # Exactly ONE weapon in hand, no hidden second slot or backpack gun.
    assert "private AarisEquippedGun equipped;" in inventory
    assert "new AarisEquippedGun[2]" not in inventory
    assert "slots[" not in inventory and "CycleWeapon()" not in inventory
    assert "public bool HasGun => equipped != null && equipped.Configured;" in inventory
    assert "TryPickup(AarisWeaponState incoming, out AarisWeaponState dropped)" in inventory
    assert "TryRemove(out AarisWeaponState removed)" in inventory
    assert "combat.Equip(null)" in inventory, "Unarmed Fire must return to Punch."
    assert "CaptureState()" in gun_cs and "Configure(AarisWeaponState state)" in gun_cs
    assert "AarisWeaponState" in gun_cs
    assert "RefreshReload();" in gun_cs and "Time.time < reloadUntil" in gun_cs
    assert "NextShotAt" in gun_cs and "SpreadSeed" in gun_cs
    # The imported FBX child applies no duplicate hand-grip rotation.
    assert "mesh.localRotation = Quaternion.identity;" in inventory
    assert "Quaternion grip = Quaternion.Euler(equipped.Spec.handRotation);" in inventory
    assert "hand.TransformPoint(equipped.Spec.handOffset)" in inventory
    assert "hand.rotation * grip" in inventory
    assert "weaponRoot.SetParent(transform, false);" in inventory
    # Multi-mesh FBX pickups must size from all renderers and rest fully
    # above the ground, including meshes with an offset source pivot.
    assert "visible.GetComponentsInChildren<Renderer>(true)" in pickup_cs
    assert "modelBounds.Encapsulate(renderers[i].bounds)" in pickup_cs
    assert "transform.position.y + 0.08f - modelBounds.min.y" in pickup_cs
    assert "modelBounds.center + delta" in pickup_cs
    assert "model.SetParent(visualPivot.transform, true);" in pickup_cs
    # Pickup swaps must reserve the old gun BEFORE committing, then retire
    # the collected world pickup without duping its ammo/weapon state.
    assert "private AarisDroppedWeapon CreateDropped" in world
    assert "private bool TryTake(AarisWeaponPickup pickup)" in world
    assert "CreateDropped(outgoing, pickup.transform.position, 1.25f," in world
    assert "deferVisual: true" in world and "pickup.CommitConsumed();" in world
    assert "UndoDrop(replacement)" in world
    assert "inventory.TryPickup(pickup.State, out AarisWeaponState exchanged)" in world
    assert "consumed[id] = true;" in world and "DeleteDrop(id);" in world
    assert "TryDropEquipped()" in world and "TryFindDropSpot" in world
    assert "TryRemove(out AarisWeaponState removed)" in world
    assert "MaximumDroppedLoot = 320" in world
    assert "rec.visible = null;" in world and "record.state" in world
    # Deliberate exchange, never auto-pick a recently dropped gun.
    assert "(!inventory.HasGun && allowInitialAutoPickup &&" in world
    assert "requestPickupUntil = Time.unscaledTime + 0.7f;" in world
    assert "allowInitialAutoPickup = false;" in world
    assert "IsBlockedDrop(pickup)" in world
    # A nearby gun must actually complete the pickup transaction before the
    # one-time landing fallback is marked settled. Merely seeing a floor gun
    # can strand a fast-moving player unarmed between streaming polls.
    starter = world.split("private void EnsureStarterLoot(Vector3 playerPosition)", 1)[1]
    starter = starter.split("private bool IsCurrentlyActive", 1)[0]
    assert "if (TryTake(pickup))" in starter
    assert "foreach (var pickup in active)" not in starter
    assert "starterLootSettled = true;" in starter
    # The newly created fallback weapon is subject to the same transaction:
    # merely creating its ground model must never settle the starter.
    assert "if (starter.visible != null && TryTake(starter.visible))" in starter
    assert "UndoDrop(starter);" in starter
    assert "starterLootId = 0;" in starter
    assert 'Add("PICK"' in hud and 'Add("DROP"' in hud
    assert 'Add("SWAP"' not in hud
    assert "private bool CanReachPickup(AarisWeaponPickup pickup)" in world
    assert "Physics.RaycastNonAlloc(new Ray(eye, toTarget / distance)" in world
    assert "CanReachPickup(pickup)" in world
    assert "if (hits >= pickupObstructions.Length) return false;" in world
    assert "SetWeaponAimFov" in player and "ApplyWeaponRecoil" in player
    assert "AarisWeaponHUD.CapturesScreenPosition" in player
    assert "MaximumActiveLoot = 40" in world
    assert "ActivationMetres = 50f" in world and "ReleaseMetres = 69f" in world
    assert "streamLimit = MaximumActiveLoot -" in world
    assert "active.Count >= streamLimit" in world
    assert "candidate.collider.attachedRigidbody != null" in world
    assert "Mathf.Abs(candidate.point.y - expectedGround) > 0.38f" in world
    assert "TryResolve" in world and "Physics.CheckCapsule" in world
    assert "if (roof.collider is TerrainCollider)" in world
    assert "float chance = indoor ? 0.72f :" in world
    assert 'c.kind == "tree" ? 0.85f : 0.60f;' in world
    assert "FindObjectsByType<Collider>" not in world
    assert "TreeInstance[] trees = data.treeInstances;" in world
    assert "while (accepted < 48 && attempts++ < 240)" in world
    # 4 tiles x 48 accepted tree-side nodes, still strictly planning-only.
    # Existing 50m stream radius, 40 visible pickup cap and 2 builds/frame
    # remain unchanged on 2 GB Android devices.
    assert "int accepted = 0, attempts = 0;" in world
    assert 'kind = "tree", x = x, y = 0f, z = z,' in world
    assert "distance = 2f + AarisWeaponRandom.Next01(ref rng) * 7f;" in world
    assert "private const int MaximumActiveLoot = 40;" in world
    assert "private const int MaximumBuildAttemptsPerFrame = 2;" in world
    assert "float distance = 2f + AarisWeaponRandom.Next01(ref rng) * 7f;" in world
    assert "AppendNativeTreeCover(allCandidates);" in world
    # Compare authored outdoor access across an evenly sampled 2 km map.
    # This is the expected planned-loot coverage BEFORE physical floor
    # validation, not a claim about rendered Android pickup visibility.
    open_slots = [c for c in loot["candidates"] if c["kind"] == "open"]
    assert len(open_slots) >= 300
    old_expected = improved_expected = 0.0
    count_samples = 0
    for x in range(-925, 926, 50):
        for z in range(-925, 926, 50):
            n = sum((c["x"] - x) ** 2 + (c["z"] - z) ** 2 <= 42 ** 2
                    for c in open_slots)
            old_expected += 1.0 - 0.68 ** n
            improved_expected += 1.0 - 0.40 ** n
            count_samples += 1
    assert count_samples > 1400
    assert improved_expected > 1.7 * old_expected
    assert "MaximumActiveLoot = 40" in world
    assert "MaximumBuildAttemptsPerFrame = 2" in world

    assert "UnityWebRequest" not in "\n".join([catalog_cs,gun_cs,inventory,world,hud])
    print("PASS: 5 locally committed, SHA-verified CC0 weapon FBXs, single-weapon swap/drop, magazine/recoil/scope, "
          "authored 2km loot candidates, streaming and combat source contracts")
    print("Unity Editor import, playable gun handling and visible materials NOT YET TESTED")


if __name__ == "__main__":
    run()
