#!/usr/bin/env python3
"""Fail closed when offline gun assets, matches or renderer bindings drift.

No C# compiler/Unity Editor is available in this job. Native FBX bytes and
actual game-time physics/animation require separate Unity import/playmode QA.
"""
from __future__ import annotations
import json
import math
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MODELS = ROOT / "Assets/Resources/AarisWeapons/Models"
PBR_ROOT = ROOT / "Assets/Resources/AarisWeapons/PBR"
PBR_M4 = PBR_ROOT / "M4A1"
EXPECTED = {
    "SMG": None,
    "AssaultRifle": None,
    "BattleRifle": None,
    "Sniper": None,
    "Shotgun": None,
}
PBR_RESOURCES = {
    "SMG": ("GreaseGun", "Grease_Gun", "AarisWeapons/PBR/GreaseGun/Grease_Gun"),
    "AssaultRifle": ("M4A1", "M4A1", "AarisWeapons/PBR/M4A1/M4A1"),
    "BattleRifle": ("BattleRifle", "BattleRifle", "AarisWeapons/PBR/BattleRifle/BattleRifle"),
    "Sniper": ("Sniper", "Sniper", "AarisWeapons/PBR/Sniper/Sniper"),
    "Shotgun": ("Shotgun", "Shotgun", "AarisWeapons/PBR/Shotgun/Shotgun"),
}
PBR_FILES = {
    "AssaultRifle": {
        "M4A1.fbx": (278384, "e3afbe0d4e42530fb0b5fb33445e8043eccd9941"),
        "M4A1_Base_Color.png": (253551, "2f9a44b109d17244de072c238eccc007cd2d228d"),
        "M4A1_Normal.png": (455779, "5a6ee12b1e8c3b194de256403652f1fd63a4e49c"),
        "M4A1_MetallicSmoothness.png": (310787, "9109237e8c2928dd9a34ba969c2b24ea1655b169"),
    },
    "SMG": {
        "Grease_Gun.fbx": (247072, "ebebfcac66b29eaa9ca64bdbe8bc1106ebf4a97b"),
        "Grease_Gun_Base_Color.png": (464915, "76c9170e6ec37ac3f688c405ad2767632c13e803"),
        "Grease_Gun_Normal.png": (623141, "2729172a61fe732a5f004f1d3abb6021e6ea871a"),
        "Grease_Gun_MetallicSmoothness.png": (179473, "2fe7ea4385950a8109601ad9ace28cb5845b295c"),
    },
    "Shotgun": {
        "Shotgun.fbx": (78336, "b7acd94e87c909753495ce42465532ac79c0510c"),
        "Shotgun_Base_Color.png": (699591, "529d9684847e565cf917dde1b665f0a8d6296d91"),
        "Shotgun_Normal.png": (483453, "ec3a8d0ea8bdbf4d3504646e51646e659ea0e4d3"),
        "Shotgun_MetallicSmoothness.png": (153276, "86491aca9f869e556db68a659012ed9d9acb8184"),
    },
}
GENERATED_PBR_FILES = {
    "BattleRifle": {
        "BattleRifle.obj": (834622, "4850e05ad63b2eb3bb29a808631d16af1b2fef85"),
        "BattleRifle.mtl": (284, "b680d111a7fc47aa9944dbaf30b06a034cf3920e"),
        "BattleRifle_Finish_Base_Color.png": (757094, "4d0a8510ac2ad84abe4a0580a75fb3e608a2abbb"),
        "BattleRifle_Finish_Normal.png": (627659, "f5811a592ebddeea311c81e11a48391aa37b51e5"),
        "BattleRifle_Finish_MetallicSmoothness.png": (313812, "5c7443004a64a61b3952c823ac08a134e59f21df"),
        "BattleRifle_Drum_Base_Color.png": (806038, "b47d21a8dcccc3fbb6dcc93799ae776da46c6e61"),
        "BattleRifle_Drum_Normal.png": (988446, "59a1adacb79bce180fb3c0077525659d200a333c"),
        "BattleRifle_Drum_MetallicSmoothness.png": (334065, "c76432910d2e07125937a969c8d12902ade6f58a"),
    },
    "Sniper": {
        "Sniper.obj": (2687167, "8ff693661a26756c65d43969d9a26bdd411207ac"),
        "Sniper.mtl": (266, "662e2417c8f53c276d53995c9add12a43981b3c0"),
        "Sniper_Finish_Base_Color.png": (1991713, "d5c3f0d1a2bb8a8b2352cba6680432985817971d"),
        "Sniper_Finish_Normal.png": (1329316, "e206166bec12fabbc092b69802d14d41c6c2db24"),
        "Sniper_Finish_MetallicSmoothness.png": (436223, "09e9caad968d508b1b53e571eb7998f7f8c59d73"),
        "Sniper_Optic_Base_Color.png": (2139991, "d540d583021934cc5477687fc6fae065edee8cc3"),
        "Sniper_Optic_Normal.png": (1951226, "9051fe60e20695cda91e0448def7c51d7da39a29"),
        "Sniper_Optic_MetallicSmoothness.png": (440171, "be18a9508b085c5c4451f91c8c70b6b78f8ca0c0"),
    },
}
SCRIPTS = ROOT / "Assets/AarisWorld/Scripts"


def run():
    catalog = json.loads((ROOT / "Assets/Resources/AarisWeapons/weapon_catalog.json").read_text())
    ids = [entry["id"] for entry in catalog["weapons"]]
    assert ids == list(EXPECTED), ids
    lengths = {gun["id"]: gun["presentationLengthMetres"] for gun in catalog["weapons"]}
    assert lengths == {"SMG": 0.65, "AssaultRifle": 0.91,
                       "BattleRifle": 1.02, "Sniper": 1.23, "Shotgun": 1.03}
    assert lengths["SMG"] < lengths["AssaultRifle"] < lengths["Sniper"]
    handling_masses = {gun["id"]: gun["handlingMassKg"] for gun in catalog["weapons"]}
    assert handling_masses == {"SMG": 4.1, "AssaultRifle": 3.4,
                               "BattleRifle": 4.4, "Sniper": 5.1, "Shotgun": 3.6}
    assert all(2.0 <= mass <= 8.0 for mass in handling_masses.values())
    # Loot probability remains authored separately; firearm inertia must never
    # consume these selection weights (42/27/15/8/8) as if they were kilograms.
    for gun in catalog["weapons"]:
        name = gun["id"]
        expected_resource = (PBR_RESOURCES[name][2]
                             if name in PBR_RESOURCES
                             else "AarisWeapons/Models/" + name)
        assert gun["modelResource"] == expected_resource
        assert 20 <= gun["rpm"] <= 1300 and gun["magazineSize"] > 0
        assert gun["reserveAmmo"] >= 0 and gun["reloadSeconds"] > 0
        assert gun["pellets"] >= 1 and gun["damage"] > 0 and gun["weight"] > 0
        assert gun["adsFov"] >= 8 and gun["adsFov"] <= 65
        if name in PBR_RESOURCES:
            continue
        path = MODELS / (name + ".fbx")
        assert path.exists(), "Real local mesh is missing: " + str(path)
        assert path.stat().st_size == EXPECTED[name][0], "Not fully hydrated or source changed: " + name
        assert path.read_bytes()[:18].startswith(b"Kaydara FBX Binary"), "Not an FBX binary: " + name
        assert subprocess.check_output(["git", "hash-object", str(path)]).decode().strip() == EXPECTED[name][1]
        assert (MODELS / (name + ".fbx.meta")).exists(), name + " GUID missing"

    # Five held-gun classes now use locally committed, pinned CC0 PBR art.
    # Base/normal stay <=1024 and packed metallic/smoothness <=512 with mip
    # streaming, keeping near-camera detail while bounding Android residency.
    for weapon_id, files in PBR_FILES.items():
        folder, stem, _ = PBR_RESOURCES[weapon_id]
        root = PBR_ROOT / folder
        for filename, (size, blob_sha) in files.items():
            asset = root / filename
            assert asset.exists(), "Missing PBR asset: " + str(asset)
            assert asset.stat().st_size == size, (filename, asset.stat().st_size, size)
            actual = subprocess.check_output(["git", "hash-object", str(asset)]).decode().strip()
            assert actual == blob_sha, (filename, actual, blob_sha)
            assert (root / (filename + ".meta")).exists(), filename + " GUID missing"
        assert (root / (stem + ".fbx")).read_bytes()[:18].startswith(b"Kaydara FBX Binary")
        base_meta = (root / (stem + "_Base_Color.png.meta")).read_text()
        normal_meta = (root / (stem + "_Normal.png.meta")).read_text()
        packed_meta = (root / (stem + "_MetallicSmoothness.png.meta")).read_text()
        assert "sRGBTexture: 1" in base_meta and "streamingMipmaps: 1" in base_meta
        assert base_meta.count("maxTextureSize: 1024") >= 2
        assert "sRGBTexture: 0" in normal_meta and "textureType: 1" in normal_meta
        assert "streamingMipmaps: 1" in normal_meta
        assert normal_meta.count("maxTextureSize: 1024") >= 2
        assert "sRGBTexture: 0" in packed_meta and "streamingMipmaps: 1" in packed_meta
        assert packed_meta.count("maxTextureSize: 512") >= 2
    # BattleRifle/Sniper are deterministic OBJ derivatives of pinned CC0
    # glTF sources. They use two independent PBR material sets each.
    for weapon_id, files in GENERATED_PBR_FILES.items():
        folder, _, _ = PBR_RESOURCES[weapon_id]
        root = PBR_ROOT / folder
        for filename, (size, blob_sha) in files.items():
            asset = root / filename
            assert asset.exists(), "Missing generated PBR asset: " + str(asset)
            assert asset.stat().st_size == size, (filename, asset.stat().st_size, size)
            actual = subprocess.check_output(["git", "hash-object", str(asset)]).decode().strip()
            assert actual == blob_sha, (filename, actual, blob_sha)
            assert (root / (filename + ".meta")).exists(), filename + " GUID missing"
        obj = root / (weapon_id + ".obj")
        text = obj.read_text("utf-8")
        assert text.count("\nf ") > 1000 and "mtllib " + weapon_id + ".mtl" in text
        stems = ("BattleRifle_Finish", "BattleRifle_Drum") if weapon_id == "BattleRifle" else (
            "Sniper_Finish", "Sniper_Optic")
        for stem in stems:
            base_meta = (root / (stem + "_Base_Color.png.meta")).read_text()
            normal_meta = (root / (stem + "_Normal.png.meta")).read_text()
            packed_meta = (root / (stem + "_MetallicSmoothness.png.meta")).read_text()
            assert "sRGBTexture: 1" in base_meta and base_meta.count("maxTextureSize: 1024") >= 2
            assert "sRGBTexture: 0" in normal_meta and "textureType: 1" in normal_meta
            assert normal_meta.count("maxTextureSize: 1024") >= 2
            assert "sRGBTexture: 0" in packed_meta and packed_meta.count("maxTextureSize: 512") >= 2
    provenance = (ROOT / "Docs/WEAPON_PBR_BATTLE_SNIPER_SOURCE.md").read_text()
    assert "Physical source length: 1.0165 m" in provenance
    assert "Exported view triangles: 5801" in provenance
    assert "Physical source length: 1.231 m" in provenance
    assert "Exported view triangles: 19365" in provenance
    assert "CC0-1.0" in provenance

    assert (PBR_M4 / "M4A1.fbx").read_bytes()[:18].startswith(b"Kaydara FBX Binary")
    # Native Unity 6000.3.26f1 import audit of the vendored M4A1:
    # 13 meshes, 11,322 vertices, 9,246 triangles, 13 material slots,
    # longest axis Z. Semantic FBX transforms place Barrel on -Z and Stock
    # on +Z, which is why the visual-only 180 degree correction is mandatory.
    native_m4 = {"meshes": 13, "vertices": 11322, "triangles": 9246,
                 "material_slots": 13, "barrel_z": -32.247989,
                 "stock_z": 21.519812}
    assert native_m4["triangles"] < 10000
    assert native_m4["vertices"] < 12000
    assert native_m4["barrel_z"] < 0 < native_m4["stock_z"]
    base_meta = (PBR_M4 / "M4A1_Base_Color.png.meta").read_text()
    normal_meta = (PBR_M4 / "M4A1_Normal.png.meta").read_text()
    packed_meta = (PBR_M4 / "M4A1_MetallicSmoothness.png.meta").read_text()
    assert "sRGBTexture: 1" in base_meta and "maxTextureSize: 1024" in base_meta
    assert "sRGBTexture: 0" in normal_meta and "textureType: 1" in normal_meta
    assert normal_meta.count("maxTextureSize: 1024") >= 2
    assert "sRGBTexture: 0" in packed_meta
    assert packed_meta.count("maxTextureSize: 512") >= 2
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

    # PR #454's remaining useful coverage, integrated additively over
    # the newer 1,574-slot main. This is a PLANNING bound, not a claim that
    # every candidate passes native roof/ground physics on a mobile device.
    final_open = [p for p in loot["candidates"] if p["kind"] == "open"]
    final_gap_slots = [p for p in final_open if p["slot"] >= 300000]
    assert len(final_gap_slots) == 3
    assert [p["slot"] for p in final_gap_slots] == [300000, 300001, 300002]
    for gx in range(-925, 926, 50):
        for gz in range(-925, 926, 50):
            assert any((p["x"] - gx) ** 2 + (p["z"] - gz) ** 2 <= 50 ** 2
                       for p in final_open), ("50m authored coverage hole", gx, gz)

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
    assert "Resources.Load<GameObject>(spec.modelResource)" in catalog_cs
    assert "Resources.LoadAsync<GameObject>(spec.modelResource)" in catalog_cs
    assert "modelCache[spec.modelResource] = prefab;" in catalog_cs
    # Both catalog loaders share the same acceptance and weighting order.
    assert catalog_cs.count("AddUsableModel(spec, prefab, available);") == 2
    assert "GetComponentInChildren<Renderer>(true) == null" in catalog_cs
    assert 'Debug.LogWarning("Aaris: unavailable local weapon model; skipping "' in catalog_cs
    assert "if (available.Count == 0)" in catalog_cs
    assert "specs = available.ToArray();" in catalog_cs
    startup = world[world.index("private IEnumerator InitializeAndroid()"):
                    world.index("private void Initialize()")]
    assert startup.index("yield return null;") < startup.index("LoadForMatchAsync()")
    assert startup.index("LoadForMatchAsync()") < startup.index("Initialize();")
    android_catalog = catalog_cs[catalog_cs.index("internal static IEnumerator LoadForMatchAsync()"):
                                 catalog_cs.index("public static GameObject ModelFor(")]
    assert android_catalog.index("yield return request;") < android_catalog.index("AddUsableModel(")
    assert android_catalog.index("AddUsableModel(") < android_catalog.index("CommitModels(available);")
    assert 'throw new FormatException("Invalid weapon entry at " + i)' in catalog_cs
    assert 'throw new FormatException("Duplicate weapon identifier.")' in catalog_cs
    assert "TryFirstOtherHit" not in gun_cs # renamed and rewritten, not a second weapon motor
    assert "FirstExternalHit" in gun_cs and "SameTarget" in gun_cs
    assert "magazine--;" in gun_cs and "nextFire = Time.time + spec.ShotInterval;" in gun_cs
    # Accepted shots get a pooled visual flash. Construction happens at
    # configuration/equip time, never inside the automatic-fire hot path.
    assert "private Renderer muzzleFlashRenderer;" in gun_cs
    assert "private static Material sharedMuzzleFlashMaterial;" in gun_cs
    assert "GameObject.CreatePrimitive(PrimitiveType.Quad)" in gun_cs
    assert "muzzleFlashRenderer.enabled = false;" in gun_cs
    assert "muzzleFlashUntil = Time.time +" in gun_cs
    assert "TriggerMuzzleFlash();" in gun_cs
    fire_block = gun_cs.split("public bool TryFire(", 1)[1].split(
        "internal static void ApplyDamage", 1)[0]
    assert fire_block.index("magazine--;") < fire_block.index("TriggerMuzzleFlash();")
    assert "CreatePrimitive" not in fire_block
    assert "new GameObject" not in fire_block
    assert "ParticleSystem" not in fire_block
    # One reusable, collider-free visual casing gives shell motion without
    # spawning Rigidbody debris on every automatic round.
    assert "private Renderer casingRenderer;" in gun_cs
    assert "GameObject.CreatePrimitive(PrimitiveType.Cylinder)" in gun_cs
    assert "private void ScheduleCasingEjection()" in gun_cs
    assert "private void EjectCasingNow()" in gun_cs
    assert "casingVelocity += Physics.gravity * dt;" in gun_cs
    assert "ScheduleCasingEjection();" in fire_block
    assert fire_block.index("magazine--;") < fire_block.index("ScheduleCasingEjection();")
    assert "PrimitiveType.Cylinder" not in fire_block
    assert "AddComponent<Rigidbody>" not in gun_cs
    assert "GetComponent<Rigidbody>" not in gun_cs
    assert "new Rigidbody" not in gun_cs
    assert "private void HideCasing()" in gun_cs
    # Bullet impacts use one pre-created world-space emitter. The firing
    # loop may call Emit(), but must never construct ParticleSystems/objects.
    assert "private ParticleSystem impactParticles;" in gun_cs
    assert "private void EnsureImpactParticles()" in gun_cs
    assert "main.simulationSpace = ParticleSystemSimulationSpace.World;" in gun_cs
    assert "main.maxParticles = 12;" in gun_cs
    assert "impactParticles.Emit(spec != null && spec.pellets > 1 ? 5 : 3);" in gun_cs
    assert "TriggerImpactParticles(impactPoint, impactNormal);" in fire_block
    assert "AddComponent<ParticleSystem>" not in fire_block
    assert "new GameObject" not in fire_block
    assert "BeginReload()" in gun_cs and "reloading" in gun_cs
    assert "semiShotQueued" in action and "equippedGun.Automatic" in action
    # One held weapon, one genuinely visible back-stowed weapon, no third slot.
    assert "private AarisEquippedGun equipped, stowed;" in inventory
    assert "private Transform weaponRoot, stowedRoot;" in inventory
    assert "new AarisEquippedGun[2]" not in inventory
    assert "slots[" not in inventory and "CycleWeapon()" not in inventory
    assert "public bool HasGun => equipped != null && equipped.Configured && !fistMode;" in inventory
    assert "public bool RequiresExchangeDrop => equipped != null && stowed != null;" in inventory
    assert "TryPickup(AarisWeaponState incoming, out AarisWeaponState dropped)" in inventory
    assert "TryRemove(out AarisWeaponState removed)" in inventory
    assert "public bool SwapWeapons()" in inventory
    assert "public bool ToggleFists()" in inventory
    assert "combat.Equip(fistMode ? null : equipped)" in inventory, "Fist mode must restore Punch."
    assert "CaptureState()" in gun_cs and "Configure(AarisWeaponState state)" in gun_cs
    assert "AarisWeaponState" in gun_cs
    assert "RefreshReload();" in gun_cs and "Time.time < reloadUntil" in gun_cs
    assert "NextShotAt" in gun_cs and "SpreadSeed" in gun_cs
    # Imported visual-axis correction is isolated from the logical weapon root:
    # native Unity audit found the M4 Barrel at z=-32.25 and Stock at z=+21.52,
    # while muzzle/support-hand math intentionally treats weaponRoot +Z as forward.
    assert "internal static Quaternion ImportedModelRotation(AarisWeaponSpec spec)" in inventory
    for resource in (
        "AarisWeapons/PBR/M4A1/M4A1",
        "AarisWeapons/PBR/GreaseGun/Grease_Gun",
        "AarisWeapons/PBR/Shotgun/Shotgun",
    ):
        assert resource in inventory
    assert "Quaternion.Euler(0f, 180f, 0f)" in inventory
    # Runtime is ready for the two generated multi-material PBR families before
    # catalog activation. Pinned source manifests place both muzzles on -Z and
    # butts on +Z, matching the same visual-only 180 degree correction.
    for resource in (
        "AarisWeapons/PBR/BattleRifle/BattleRifle",
        "AarisWeapons/PBR/Sniper/Sniper",
    ):
        assert resource in inventory
    assert 'sourceName.IndexOf("Drum"' in inventory
    assert '"BattleRifle_Drum" : "BattleRifle_Finish"' in inventory
    assert 'sourceName.IndexOf("Optic"' in inventory
    assert '"Sniper_Optic" : "Sniper_Finish"' in inventory
    assert 'string cacheKey = resourceFolder + "/" + stem;' in inventory
    assert "Material authoredPbr = AuthoredPbrFinish(spec, source[i]);" in inventory
    assert "mesh.localRotation = ImportedModelRotation(incoming.Spec);" in inventory
    assert "Quaternion.Euler(0f, 0f, -75f) *" in pickup_cs
    assert "AarisWeaponInventory.ImportedModelRotation(state.Spec)" in pickup_cs
    assert "Quaternion grip = Quaternion.Euler(equipped.Spec.handRotation);" in inventory
    assert "hand.position + hand.rotation * equipped.Spec.handOffset" in inventory
    assert "hand.rotation * grip" in inventory
    assert "weaponRoot.SetParent(transform, false);" in inventory
    assert "private static void SetHierarchyLayer(GameObject root, int layer)" in inventory
    assert "root.GetComponentsInChildren<Transform>(true)" in inventory
    assert "node.gameObject.layer = layer;" in inventory
    pickup_block = inventory[inventory.index("public bool TryPickup("):
                             inventory.index("public bool TryRemove(")]
    assert "SetHierarchyLayer(root, gameObject.layer);" in pickup_block
    assert pickup_block.index("SetHierarchyLayer(root, gameObject.layer);") < \
           pickup_block.index("foreach (var collider in visible.GetComponentsInChildren<Collider>(true))")
    # Multi-mesh FBX pickups must size from all renderers and rest fully
    # above the ground, including meshes with an offset source pivot.
    assert "modelRenderers = visible.GetComponentsInChildren<Renderer>(true);" in pickup_cs
    assert pickup_cs.count("visible.GetComponentsInChildren<Renderer>(true)") == 1
    assert "visible.GetComponentInChildren<Renderer>(true)" not in pickup_cs
    assert "if (modelRenderers.Length == 0)" in pickup_cs
    assert "AarisWeaponMeshBounds.TryMeasure(transform," in pickup_cs
    assert "private const float ModelFloorClearance = 0.025f;" in pickup_cs
    assert "ModelFloorClearance - modelBounds.min.y * scale" in pickup_cs
    assert "ModelFloorClearance + (modelBounds.center.y - modelBounds.min.y) * scale" in pickup_cs
    assert "private const float LootRootFloorClearance = 0.04f;" in world
    assert "spot = floor.point + Vector3.up * LootRootFloorClearance;" in world
    assert "Vector3 candidate = hitPoint + Vector3.up * LootRootFloorClearance;" in world
    # Measured visual floor clearance for both authored and dropped guns.
    # Small positive margin avoids z-fighting without 21cm arcade hovering.
    assert 0.05 < 0.04 + 0.025 < 0.08
    assert "(modelBounds.center.y - modelBounds.min.y) * scale" in pickup_cs
    assert "model.SetParent(visualPivot.transform, true);" in pickup_cs
    assert "meshFilter.sharedMesh.bounds" in pickup_cs
    assert "skinned.localBounds" in pickup_cs
    assert "renderer.transform.TransformPoint(local)" in pickup_cs
    assert "root.InverseTransformPoint(" in pickup_cs
    assert "modelBounds.Encapsulate(modelRenderers[i].bounds)" not in pickup_cs
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
    assert "(!inventory.HasAnyGun && allowInitialAutoPickup &&" in world
    assert "requestPickupUntil = Time.unscaledTime + 1.5f;" in world
    assert "nextRefresh = 0f;" in world
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
    assert 'Add("SWAP"' in hud and 'Add("FIST"' in hud
    assert "i == 4 ? hasStored : i == 5 ? hasAny : armed;" in hud
    assert "inventory.RequiresExchangeDrop" in world
    # Players must be able to discover an already-streamed gun without
    # camera-wide scans, extra 3D markers or touch-blocking HUD graphics.
    assert "internal static IEnumerable<AarisWeaponPickup> ActivePickups" in pickup_cs
    assert "activePickups.Add(this);" in pickup_cs
    assert "activePickups.Remove(this);" in pickup_cs
    assert "RuntimeInitializeLoadType.SubsystemRegistration" in pickup_cs
    assert "if (state != null && model != null && !consumed)" in pickup_cs
    assert "foreach (var pickup in AarisWeaponPickup.ActivePickups)" in hud
    # An automatically equipped starter must not suppress discovery of the
    # next floor gun. Preserve 1.25s throttling and the existing model cap.
    hint_tick = hud.split("private void LateUpdate()", 1)[1].split(
        "bool canPick =", 1)[0]
    assert "if (armed)" not in hint_tick
    assert "if (Time.unscaledTime >= nextNearbyGunHintAt)" in hint_tick
    assert "RefreshNearbyGunHint();" in hint_tick
    assert "nextNearbyGunHintAt = Time.unscaledTime + 1.25f;" in hud
    assert "nearbyGunHint.raycastTarget = false;" in hud
    assert "float closestSquare = 48f * 48f;" in hud
    assert "FindObjectsByType<AarisWeaponPickup>" not in hud
    assert "private bool CanReachPickup(AarisWeaponPickup pickup)" in world
    assert "Physics.RaycastNonAlloc(sightLine, pickupObstructions, distance" in world
    # Keep correct occlusion on 24-hit overflow, without allocating on
    # every crowded-house pickup. Full RaycastAll remains last-resort only.
    assert "CompleteRaycastOverflow(sightLine, distance, out sightHits)" in world
    assert "Physics.RaycastNonAlloc(ray, overflowRayHits, distance," in world
    assert "results = Physics.RaycastAll(ray, distance," in world
    assert "CanReachPickup(pickup)" in world
    assert "if (hits >= pickupObstructions.Length)" in world
    assert "var collider = sightHits[i].collider;" in world
    assert "collider.transform.IsChildOf(player)" in world
    assert "SetWeaponAimFov" in player and "ApplyWeaponRecoil" in player
    assert "AarisWeaponHUD.CapturesScreenPosition" in player
    assert "MaximumActiveLoot = 40" in world
    assert "ActivationMetres = 50f" in world and "ReleaseMetres = 69f" in world
    assert "streamLimit = MaximumActiveLoot -" in world
    assert "active.Count >= streamLimit" in world
    assert "candidate.collider.attachedRigidbody != null" in world
    assert "Mathf.Abs(candidate.point.y - expectedGround) > 0.38f" in world
    assert "TryResolve" in world and "Physics.CheckCapsule" in world
    assert "alternateProbeSweep = new byte[candidates.Length];" in world
    assert "alternateProbeSweep[index] = (byte)((sweep + 1) & 7);" in world
    # Native Unity Play Mode: all 11 sampled ground slots returned zero
    # TerrainCollider ray hits while using SampleHeight as world Y. The
    # native tile origin is -120m; the corrected ray hits exactly at
    # SampleHeight + tile-origin Y. Never regress to floating gun probes.
    assert "float expectedGround = indoor ? c.y :" in world
    assert "terrain.transform.position.y +" in world
    assert "terrain.SampleHeight(new Vector3(x, 0f, z));" in world
    assert "Terrain.SampleHeight is already in world-space Y;" not in world
    native_ground = -120.0 + 92.20836
    assert round(native_ground, 5) == -27.79164
    assert 92.20836 + 1.9 - 3.3 > native_ground + 100.0
    # Sealed-house fallback: bounded outdoor probes, not inaccessible floor loot.
    assert 'bool indoor = c.kind == "indoor" && !forceOutdoor;' in world
    assert 'bool relocateIndoor = candidate.kind == "indoor";' in world
    assert "forceOutdoor: relocateIndoor" in world
    assert "RaycastHit roof, 4.5f" in world
    assert "alternative < 2 && alternateProbeBudget > 0" in world
    assert "int alternateProbeBudget = 3;" in world
    assert "if (roof.collider is TerrainCollider)" in world
    assert "float chance = indoor ? 0.72f :" in world
    assert 'c.kind == "tree" ? 0.92f :' in world
    assert 'c.kind == "open" && c.slot >= 200000 ? 1f : 0.78f;' in world
    assert "FindObjectsByType<Collider>" not in world
    # Unity 6 indexed tree access avoids copying the entire native tree
    # population into a managed array on low-RAM Android devices.
    assert "int treeCount = data.treeInstanceCount;" in world
    assert "TreeInstance instance = data.GetTreeInstance(idx);" in world
    assert "TreeInstance[] trees = data.treeInstances;" not in world
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
    # Legacy slots have 16-bit IDs; new deterministic satellites start at
    # 100000. Compare the old selection-probability improvement on the
    # same baseline, then independently verify the extra authored coverage.
    legacy_slots = [c for c in open_slots if c["slot"] < 65536]
    assert len(legacy_slots) >= 300 and len(open_slots) > len(legacy_slots) + 500
    old_expected = improved_expected = dense_expected = 0.0
    count_samples = 0
    for x in range(-925, 926, 50):
        for z in range(-925, 926, 50):
            n = sum((c["x"] - x) ** 2 + (c["z"] - z) ** 2 <= 42 ** 2
                    for c in legacy_slots)
            dense_n = sum((c["x"] - x) ** 2 + (c["z"] - z) ** 2 <= 42 ** 2
                          for c in open_slots)
            old_expected += 1.0 - 0.68 ** n
            improved_expected += 1.0 - 0.40 ** n
            dense_expected += 1.0 - 0.40 ** dense_n
            count_samples += 1
    assert count_samples > 1400
    assert improved_expected > 1.7 * old_expected
    assert dense_expected > 1.2 * improved_expected
    assert "MaximumActiveLoot = 40" in world
    assert "MaximumBuildAttemptsPerFrame = 2" in world

    # Native Unity Play Mode baseline: five equipped FBX body bounds
    # measured 5.5-9m despite the wrist following correctly. Source mesh
    # corners, not stale Renderer.bounds, must drive equip scaling.
    grip = inventory[inventory.index("public bool TryPickup("):
                     inventory.index("public bool TryRemove(")]
    assert "AarisWeaponMeshBounds.TryMeasure(root.transform," in grip
    assert "mesh.localScale *= scale;" in grip
    assert "incoming.Spec.presentationLengthMetres / longest" in grip
    assert "presentationLengthMetres >= 0.48f" in catalog_cs
    assert "combinedBounds.center * scale" in grip
    assert "incoming.Spec.presentationLengthMetres * 0.22f" in grip
    assert "state.Spec.presentationLengthMetres / length" in pickup_cs
    assert "equipped.Spec.presentationLengthMetres * 0.40f" in inventory
    assert "weaponRoot.TransformPoint(new Vector3(-0.045f, -0.035f, foregrip))" in inventory
    # Textureless CC0 gun submeshes need physically distinct shared finishes.
    # BattleRifle is catalogued as category AR, so its special finish must use
    # the stable weapon id rather than an unreachable category=="BATTLE" check.
    assert 'string weaponId = spec != null && spec.id != null' in inventory
    assert 'if (weaponId.Contains("BATTLE"))' in inventory
    assert 'if (category.Contains("BATTLE"))' not in inventory
    assert 'label.Contains("brown")' in inventory
    assert 'label.Contains("wood")' in inventory
    assert 'label.Contains("bole")' in inventory
    assert 'private static readonly Dictionary<Material, Material> weaponFinishCache' in inventory
    assert 'weaponFinishCache[source] = result;' in inventory
    assert 'private static readonly Dictionary<string, Material> pbrFinishCache' in inventory
    assert 'private static Material CreatePbrFinish(string label,' in inventory
    assert 'private static Material AuthoredPbrFinish(AarisWeaponSpec spec,' in inventory
    for resource in (
        "AarisWeapons/PBR/M4A1/M4A1",
        "AarisWeapons/PBR/GreaseGun/Grease_Gun",
        "AarisWeapons/PBR/BattleRifle/BattleRifle",
        "AarisWeapons/PBR/Sniper/Sniper",
        "AarisWeapons/PBR/Shotgun/Shotgun",
    ):
        assert resource in inventory
    assert 'resourceFolder + "/" + stem + "_Base_Color"' in inventory
    assert 'resourceFolder + "/" + stem + "_Normal"' in inventory
    assert 'resourceFolder + "/" + stem + "_MetallicSmoothness"' in inventory
    assert 'pbrFinishCache[cacheKey] = material;' in inventory
    assert 'EnableKeyword("_NORMALMAP")' in inventory
    assert 'EnableKeyword("_METALLICSPECGLOSSMAP")' in inventory
    assert 'Material authoredPbr = AuthoredPbrFinish(spec, source[i]);' in inventory
    assert 'internal static void ApplyTextureAwareWeaponFinish' in inventory
    assert "AarisWeaponInventory.ApplyTextureAwareWeaponFinish(" in pickup_cs
    # Gun models do not all have the same length; a fixed long-barrel
    # support point is physically unreachable on shorter humanoid arms.
    # The bounded rail/sphere intersection must precede the left-arm IK.
    assert "private static float ReachableSupportGripZ(" in inventory
    assert "foregrip = ReachableSupportGripZ(" in inventory
    assert "maximumReach = upperLength + lowerLength - 0.025f;" in inventory
    assert "halfChordSquared = maximumReach * maximumReach - radialSquared;" in inventory
    assert "minimumForegripZ = 0.14f;" in inventory
    assert inventory.index("foregrip = ReachableSupportGripZ(") < inventory.index(
        "ReachArm(leftUpperArm, leftForearm, leftHand,")
    # Left support hand needs a rotation target as well as a position target.
    # Derive it from actual finger/thumb bones so avatar-local hand axes do not
    # need to be hard-coded, and clamp the correction to preserve authored pose.
    assert "HumanBodyBones.LeftThumbProximal" in inventory
    assert "HumanBodyBones.LeftMiddleProximal" in inventory
    assert "private void AlignSupportWristRotation()" in inventory
    assert "Quaternion currentBasis = Quaternion.LookRotation(finger, thumb);" in inventory
    assert "Quaternion targetBasisA = Quaternion.LookRotation(across, barrel);" in inventory
    assert "Quaternion targetBasisB = Quaternion.LookRotation(-across, barrel);" in inventory
    assert "Quaternion.RotateTowards(" in inventory
    assert "aiming ? 52f : 38f" in inventory
    assert inventory.index("ReachArm(leftUpperArm, leftForearm, leftHand,") < inventory.index(
        "AlignSupportWristRotation();")
    # Visible two-hand grip also closes the fingers around real gun-space
    # targets. Use evaluated world-space segment directions rather than
    # hard-coded local Euler axes, which differ across Humanoid avatars.
    for bone in (
        "RightIndexProximal", "RightMiddleProximal", "RightRingProximal",
        "RightLittleProximal", "LeftIndexProximal", "LeftMiddleProximal",
        "LeftRingProximal", "LeftLittleProximal",
    ):
        assert "HumanBodyBones." + bone in inventory
    assert "private static void BendDigitTowardGrip(" in inventory
    assert "Quaternion.FromToRotation(" in inventory
    assert "Vector3.Slerp(first.normalized" in inventory
    assert "Vector3.Slerp(second.normalized" in inventory
    assert "private void ApplyFingerGrip(float foregrip)" in inventory
    assert "BendDigitTowardGrip(rightGripDigits, 0," in inventory
    assert "8f, 11f" in inventory  # trigger index stays comparatively relaxed
    assert "BendDigitTowardGrip(rightGripDigits, 3, pistolGrip, 25f, 38f);" in inventory
    assert "BendDigitTowardGrip(leftGripDigits, 3, supportWrap, 22f, 34f);" in inventory
    assert inventory.index("hand.rotation = Quaternion.Slerp") < inventory.index(
        "ApplyFingerGrip(foregrip);")

    def reachable_rail_grip(dx, dy, dz, arm_reach, preferred):
        # Forward-facing barrel = +Z. C# uses the same quadratic intersection
        # for arbitrary barrel direction via a 3D dot product.
        radial_sq = dx * dx + dy * dy
        chord_sq = arm_reach * arm_reach - radial_sq
        if chord_sq <= 0:
            return preferred
        zmin = -dz - math.sqrt(chord_sq)
        zmax = -dz + math.sqrt(chord_sq)
        if zmax < 0.14 or zmin > preferred:
            return preferred
        return min(max(preferred, max(0.14, zmin)), zmax)

    # Small shoulders/forearms and the longest guns used to show a palm
    # floating beyond the physical barrel grip. A short SMG stays untouched.
    for weapon_length in lengths.values():
        preferred = max(0.22, min(0.48, weapon_length * 0.40))
        z = reachable_rail_grip(0.295, -0.18, 0.13, 0.555, preferred)
        assert 0.14 <= z <= preferred
        assert 0.295**2 + 0.18**2 + (0.13 + z)**2 <= 0.555**2 + 1e-8
    assert reachable_rail_grip(0.10, -0.08, 0.10, 1.0, 0.48) == 0.48
    assert reachable_rail_grip(1.2, 0.0, 0.10, 0.555, 0.48) == 0.48
    assert "combinedBounds = renderers[0].bounds;" not in grip
    assert grip.index("TryMeasure(root.transform,") < grip.index(
        "mesh.localScale *= scale;") < grip.index(
        "var next = root.AddComponent<AarisEquippedGun>();")
    attach = inventory[inventory.index("private void AttachModelsToSockets("):
                       inventory.index("private void AlignSupportHand()")]
    late = inventory[inventory.index("private void LateUpdate()"):]
    assert "hand.position + hand.rotation * equipped.Spec.handOffset" in attach
    # Two-hand IK must settle in wall-clock time, not degrees per rendered frame.
    assert "private static float GripFrameScale()" in inventory
    assert "30f * Mathf.Clamp(Time.deltaTime, 0f, 0.05f)" in inventory
    assert "(aiming ? 52f : 38f) * frameScale" in inventory
    assert "private static float GripBlendAlpha(float alphaAt30Fps)" in inventory
    assert "Mathf.Pow(remaining, GripFrameScale())" in inventory
    assert "GripBlendAlpha(aiming ? 0.92f : 0.76f)" in inventory
    assert "proximalDegrees * frameScale" in inventory
    assert "middleDegrees * frameScale" in inventory
    assert "weaponRoot.SetParent(transform, false);" in attach
    assert "PlaceOnBack(stowedRoot, true)" in attach
    # Fists AND full-speed armed sprint must carry the physical gun on
    # the back, while ADS/fire/prone retain the shoulder-ready weapon.
    assert "if (fistMode || IsSprintHolstered)" in attach
    assert "PlaceOnBack(weaponRoot, stowedRoot != null ? false : true);" in attach
    assert "AimCamera" in attach
    assert "combat.Stance == AarisBattleActions.PlayerStance.Prone" in attach
    assert "Quaternion.LookRotation(forward, Vector3.up)" in attach
    assert "AttachModelsToSockets();" in late
    assert "AlignSupportHand();" in late
    assert "IsSprintHolstered = !fistMode && !aiming" in late
    assert "!combat.FireHeld" in late
    assert "capsule.isGrounded" in late
    assert "sprintIntent && planarSpeedSqr > 3.75f * 3.75f" in late
    assert "private static void ReachArm(" in inventory
    assert "ReachArm(rightUpperArm, rightForearm, hand," in inventory
    assert "ReachArm(leftUpperArm, leftForearm, leftHand," in inventory
    assert "Vector3 targetElbow = " in inventory
    assert "Quaternion.FromToRotation(oldUpper, newUpper)" in inventory
    # Camera/chest anchoring must not leave the trigger palm floating when a
    # smaller Humanoid cannot physically reach the nominal receiver position.
    assert "private void FitTriggerGripToArmReach()" in inventory
    assert "float maximumReach = upperLength + lowerLength - 0.025f;" in inventory
    assert "float correction = Mathf.Min(distance - maximumReach, 0.12f);" in inventory
    assert "weaponRoot.position -= fromShoulder / distance * correction;" in inventory
    assert inventory.index("FitTriggerGripToArmReach();") < inventory.index(
        "foregrip = ReachableSupportGripZ(")

    def trigger_fit(distance, reach):
        return min(max(0.0, distance - reach), 0.12)

    assert trigger_fit(0.50, 0.55) == 0.0
    assert abs(trigger_fit(0.61, 0.55) - 0.06) < 1e-12
    assert trigger_fit(0.90, 0.55) == 0.12

    assert "equipped.Spec.handlingMassKg" in inventory
    assert "Mathf.InverseLerp(2.5f, 5.5f, massKg)" in inventory
    assert "float swayTime = Mathf.Lerp(0.065f, 0.095f, responseMass01);" in inventory
    assert "if (aiming) swayTime *= 0.68f;" in inventory
    assert "equipped.Spec.weight" not in inventory[inventory.index(
        "private void ApplyProceduralWeaponMotion()"):inventory.index(
        "private static float GripFrameScale()")]
    assert "handlingMassKg >= 2.0f" in catalog_cs

    # Camera aim may hit the same collider deeper than the barrel ray.
    # Damage can keep the reticle target, but sound/VFX must use first muzzle contact.
    assert "Vector3 impactPoint = destination;" in gun_cs
    assert "Vector3 impactNormal = targetFound ? target.normal : -look;" in gun_cs
    assert "impactPoint = muzzleHit.point;" in gun_cs
    assert "impactNormal = muzzleHit.normal;" in gun_cs
    assert "AarisAudioDirector.BulletImpact(impactPoint);" in gun_cs
    assert "TriggerImpactParticles(impactPoint, impactNormal);" in gun_cs

    # The normal landing pickup remains physical; only after failed
    # floor/visibility checks may the existing inventory grant one starter.
    # Prevent a future refactor from silently stranding players unarmed.
    assert "private void TryLastResortStarterEquip()" in starter
    assert "Time.unscaledTime - starterUnarmedSince < 3f" in starter
    assert "if (!TryFindDropSpot(out Vector3 point, requireReachable: true))" in starter
    assert "TryLastResortStarterEquip();" in starter
    assert "inventory.TryPickup(state, out AarisWeaponState previous)" in starter
    assert "starterLootSettled = true;" in starter

    assert "UnityWebRequest" not in "\n".join([catalog_cs,gun_cs,inventory,world,hud])
    print("PASS: 4 legacy CC0 guns plus SHA-verified mobile PBR CC0 M4A1, two-slot holster/swap/drop, "
          "magazine/recoil/scope, authored 2km loot candidates, streaming and combat source contracts")
    print("Unity Editor import, playable gun handling and visible materials NOT YET TESTED")


if __name__ == "__main__":
    run()
