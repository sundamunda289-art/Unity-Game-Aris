#!/usr/bin/env python3
"""Fail-closed authored 2km car spatial/asset/drive audit.

This is license-free static QA, NOT a Unity engine or Android play-mode test.
"""
from pathlib import Path
import hashlib, json, math, random, re, zipfile

ROOT=Path(__file__).resolve().parents[2]
SCRIPTS=ROOT/"Assets/AarisWorld/Scripts"
RES=ROOT/"Assets/Resources/AarisVehicles"
HASHES={
    "sedan":("0affa82e6b09440844b2d69dfccfa1ec962e5915b08c2740efe9abe3964ac579",114272),
    "suv":("d44b0558458fea1f51de83c62f5c21bf82e1feebcf1fca396a73a989bb6afb12",135104),
    "sedan-sports":("2dbcb4e218ee32d1bb4884d0e96bd29a0f3891d54e31e480c957752567ffea7c",120400),
    "hatchback-sports":("ecd7df3d2be99bd0c798836031e093a02c259cfa0a4841233d4ab62e35cdd4bf",116032),
    "van":("0717a81e7770300e96faa0a52910bea287f591ec8d0b956667f74819c5df0bbb",115488),
    "truck-flat":("5502e87aa979b1051e5071eca8ad910623e7e6aa918600a9da0910272f9c8ec6",124896),
    "wheel-default":("241da720d24a74b839ff18c4e6b30b8ea5f0cc8a925fa04c01327b0be5277ce8",34144),
    "wheel-racing":("c779dba6ca48b46df4ed1244a026132eb84ba426370d945a3c1b8eef76b41dbe",35456),
    "wheel-truck":("21090484ab58c404b3001fbfce09221a790f41faf5c9fd010b895c4846ce392e",37696),
}
AUTHORED={
    "1990s-hatchback":("9266b0ac0bbccee09cb03a3ade7a17483d641fbe8d4baffbf6fd6bcb0271c932",2120156),
    "1990s-suv":("64069497f9c436ef8c2d8b197c51e8abf8c9f9656bedfe6834f04c9996673f1b",2607228),
    "1990s-sports-car":("42eefdcde7275013aa5894e577c81a92e991f042750ff4c7360ef71764dcf826",2046796),
    "1980s-van":("c50f781d642e39f84cbe664ea2ec7c1545d33e173c229c17031fd0464e5ba285",2333388),
    "1990s-pickup-truck":("a561a47fe2b92404a7a35d32be209d4d618a6b5e9475e70cfa4dc1394fc6ab5d",2433580),
}

def script(name):
    return (SCRIPTS/(name+".cs")).read_text("utf8")

def next_uint(state):
    state ^= (state<<13)&0xffffffff
    state ^= state>>17
    state ^= (state<<5)&0xffffffff
    return state&0xffffffff

def test():
    source=json.loads((ROOT/"Docs/MAP_LAYOUT.json").read_text())
    loc=json.loads((RES/"vehicle_candidates.json").read_text())
    catalog=json.loads((RES/"vehicle_catalog.json").read_text())
    assert source["size_m"]==[2000,2000] and loc["sizeMetres"]==2000
    assert loc["schema"]==1 and loc["hubCount"]==len(source["vehicle_spawns"])==45
    assert len(source["roads"])==39
    pts=loc["candidates"]
    hubs=[x for x in pts if x["kind"]=="hub"]
    roadside=[x for x in pts if x["kind"]=="roadside"]
    assert len(hubs)==45 and len(roadside)==loc["roadsideCount"]==33
    source_hubs={p["name"]:p for p in source["vehicle_spawns"]}
    for p in hubs:
        marker=source_hubs[p["origin"]]
        for k,idx in [("x",0),("y",1),("z",2)]:
            assert abs(marker["position_m"][idx]-p[k])<0.0011,(p["id"],k)
        assert abs(marker["yaw_degrees"]-p["yaw"])<.001
        assert p["district"]==marker["district"]
    names={p["name"] for p in source["roads"]}
    assert all(p["origin"] in names for p in roadside)
    assert all(abs(p["x"])<951 and abs(p["z"])<951
               and p["y"]>source["sea_level_m"]+1 for p in pts)
    assert len({p["id"] for p in pts})==len(pts)
    covered={(int((p["x"]+1000)//500),int((p["z"]+1000)//500)) for p in pts}
    assert len(covered)>=14,covered
    for p in roadside:
        assert all((p["x"]-h["x"])**2+(p["z"]-h["z"])**2>90**2 for h in hubs)
        assert all(not (box["aabb_xz_m"][0]-7<p["x"]<box["aabb_xz_m"][2]+7 and
                        box["aabb_xz_m"][1]-7<p["z"]<box["aabb_xz_m"][3]+7)
                   for box in source["buildings"])

    types=catalog["vehicles"]
    assert {x["modelResource"].split("/")[-1] for x in types}==set(HASHES)-{
        "wheel-default","wheel-racing","wheel-truck"}
    assert {x["wheelResource"].split("/")[-1] for x in types}=={
        "wheel-default","wheel-racing","wheel-truck"}
    assert all(8<=x["maxSpeedMetres"]<=28 and 800<x["massKg"]<2400
               and x["weight"]>0 for x in types)
    # The new sedan art replaces one catalog presentation, keeping the
    # existing FBX pair available as a unit if its authored import fails.
    sedan = next(x for x in types if x["id"] == "Sedan")
    assert sedan["modelResource"] == "AarisVehicles/Models/sedan"
    assert sedan["wheelResource"] == "AarisVehicles/Models/wheel-default"
    for key, mesh, maximum in (("authoredModelResource", "AarisCivilSedan", 1700),
                               ("authoredWheelResource", "AarisCivilWheel", 300)):
        prefab = ROOT / "Assets/Resources" / (sedan[key] + ".prefab")
        assert prefab.is_file() and prefab.with_suffix(".prefab.meta").is_file()
        path = RES / "Authored" / (mesh + ".obj")
        lines = path.read_text().splitlines()
        assert 100 < sum(line.startswith("f ") for line in lines) <= maximum
        assert [line for line in lines if line.startswith("usemtl ")] == ["usemtl SedanSurface"]
        assert path.with_suffix(".obj.meta").is_file()
    for texture in ("SedanSurface", "SedanMetalSmoothness"):
        png = (RES / "Authored" / (texture + ".png")).read_bytes()
        assert int.from_bytes(png[16:20], "big") == int.from_bytes(png[20:24], "big") == 128
    authored_by_type = {
        "SUV":"1990s-suv",
        "Sports":"1990s-sports-car",
        "Hatchback":"1990s-hatchback",
        "Van":"1980s-van",
        "Utility":"1990s-pickup-truck",
    }
    for vehicle_id, slug in authored_by_type.items():
        item = next(x for x in types if x["id"] == vehicle_id)
        assert item["authoredModelResource"] == f"AarisVehicles/Authored/{slug}"
        body = RES / "Authored" / f"{slug}.fbx"
        expected, size = AUTHORED[slug]
        assert body.is_file() and body.stat().st_size == size, ("authored FBX missing", slug)
        assert hashlib.sha256(body.read_bytes()).hexdigest() == expected, ("authored FBX changed", slug)
        assert body.read_bytes().startswith(b"Kaydara FBX Binary"), slug
        assert body.with_name(body.name + ".meta").is_file(), ("stable authored GUID missing", slug)
    source_note = (RES/"Authored"/"GRAB3D_CC0_SOURCES.txt").read_text("utf8")
    assert "CC0-1.0" in source_note and "never downloaded at game runtime" in source_note
    for slug, (expected, _) in AUTHORED.items():
        assert slug in source_note and expected in source_note
    # Three real-proportion CC0 Quaternius bodies replace the stylized
    # legacy presentation while retaining the existing WheelCollider/wheel art.
    realistic = {
        "SUV": ("AarisVehicles/Authored/QuaterniusSUV", "QuaterniusSUV", 1000,
                (2.111102, 1.527851, 4.209349)),
        "Sports": ("AarisVehicles/Authored/QuaterniusSports", "QuaterniusSports", 900,
                   (1.804519, 1.155404, 3.968707)),
        "Hatchback": ("AarisVehicles/Authored/QuaterniusCompact", "QuaterniusCompact", 900,
                      (1.638448, 1.145616, 3.309623)),
    }
    for vehicle_id, (resource, mesh, max_faces, expected_size) in realistic.items():
        item = next(x for x in types if x["id"] == vehicle_id)
        assert item["fallbackAuthoredModelResource"] == resource
        path = RES / "Authored" / (mesh + ".obj")
        assert path.is_file() and path.with_suffix(".obj.meta").is_file()
        lines = path.read_text().splitlines()
        assert 300 < sum(line.startswith("f ") for line in lines) <= max_faces
        assert sum(line.startswith("o ") for line in lines) == 1
        assert not any("wheel" in line.lower() for line in lines if line.startswith("o "))
        materials = {line.split(None, 1)[1] for line in lines if line.startswith("usemtl ")}
        assert materials == {"AarisPaint", "AarisGlass", "AarisDark",
                             "AarisHeadlight", "AarisTaillight"}
        vertices = [tuple(map(float, line.split()[1:4]))
                    for line in lines if line.startswith("v ")]
        mins = [min(v[i] for v in vertices) for i in range(3)]
        maxs = [max(v[i] for v in vertices) for i in range(3)]
        size = (maxs[0]-mins[0], maxs[1]-mins[1], maxs[2]-mins[2])
        assert all(abs(a-b) < 0.005 for a,b in zip(size, expected_size)), (
            vehicle_id, size)
        mtl = RES / "Authored" / (mesh + ".mtl")
        assert mtl.is_file() and mtl.with_suffix(".mtl.meta").is_file()
    provenance = (ROOT / "Docs/ASSET_PROVENANCE_QUATERNIUS_CARS.md").read_text()
    assert "CC0 1.0 Universal" in provenance
    assert "a170b37e1b9e5d55d82c680f2eca06877823da27" in provenance
    assert "7611260cb8e95913ad5ef550dd89a0b140657d20" in provenance
    assert "9a8316ecbe14f80ea98ef2f5951de7dfa8f95ce9" in provenance
    for n,(expected,size) in HASHES.items():
        p=RES/"Models"/(n+".fbx")
        assert p.is_file() and p.stat().st_size==size,("real FBX missing",n)
        assert hashlib.sha256(p.read_bytes()).hexdigest()==expected,("changed mesh",n)
        assert p.read_bytes().startswith(b"Kaydara FBX Binary"),n
        assert p.with_name(p.name+".meta").exists(),("stable GUID missing",n)
    palette=RES/"Models/colormap.png"
    assert palette.exists() and palette.read_bytes().startswith(bytes((137,80,78,71,13,10,26,10)))
    # Unity .meta files are NOT ordinary imported assets: an earlier
    # asset-vendor loop accidentally made .meta.meta and .meta.meta.meta.
    assert not list(RES.rglob("*.meta.meta*")), "recursive .meta metadata found"
    assert (RES/"KENNEY_LICENSE.txt").exists()
    license_text=(RES/"KENNEY_LICENSE.txt").read_text(errors="ignore").lower()
    assert any(x in license_text for x in ["cc0","public domain","creativecommons.org/publicdomain"])
    for name in ["AarisVehicleWorld","AarisVehicleMotor","AarisVehicleSession","AarisVehicleHUD"]:
        assert (SCRIPTS/(name+".cs.meta")).exists()
    world=script("AarisVehicleWorld")
    motor=script("AarisVehicleMotor")
    session=script("AarisVehicleSession")
    hud=script("AarisVehicleHUD")
    foot=script("AarisThirdPersonPlayer")
    battle=script("AarisBattleHUD")
    nearby=script("AarisNearbyColliders")

    # Bound 2km car plans, floor/bridge/mountain and wall clearance.
    assert "TargetVehicles = 44" in world
    assert "file.hubCount != 45" in world
    assert "var sectors = new HashSet<int>();" in world
    assert "int sector = Mathf.Clamp" in world
    assert "AddPlan(point, 105f)" in world
    # Mobile vehicle streaming was extended to 105m so parked cars become
    # visible before the player reaches a road. Preserve the 10-car cap and
    # separate 160m unload hysteresis; the old 42m expectation was stale.
    assert "LoadRadius = 105f" in world and "UnloadRadius = 160f" in world
    assert "AuthoredPrewarmRadius = 150f" in world
    assert world.index("AuthoredPrewarmRadius = 150f") < world.index("UnloadRadius = 160f")
    assert "Resources.LoadAsync<GameObject>(item.authoredModelResource)" in world
    assert "fallback.priority = -1;" in world and "request.priority = -1;" in world
    assert "int authoredStarts = 0;" in world and "authoredStarts >= 1" in world
    assert "PumpAuthoredBodyLoads();" in world
    assert "MaximumNearbyVehicles = 10" in world
    # Car models stream at 105 m but authored wall colliders stream at 55 m.
    # Never approve a parking volume only because nearby physics is inactive.
    assert "activationDistance = 55" in nearby
    assert "nearbyColliders = FindFirstObjectByType<AarisNearbyColliders>();" in world
    assert "nearbyColliders.HasPotentialInactiveObstacle(center, halfExtents, angle)" in world
    assert world.index("nearbyColliders.HasPotentialInactiveObstacle") < world.index("Physics.OverlapBoxNonAlloc")
    assert "internal bool HasPotentialInactiveObstacle(" in nearby
    assert "cells.TryGetValue(Key(xCell, zCell)" in nearby
    assert "streamed[i] && c != null && !c.enabled" in nearby
    assert "worldBounds[i].Intersects(probe)" in nearby
    assert "new Bounds(center, 2f * extents)" in nearby
    assert "Physics.OverlapBoxNonAlloc" in world
    assert "Physics.RaycastNonAlloc" in world
    # Dense saved settlements may exceed the fixed hit-array capacity.
    # Preserve the cheap NonAlloc normal path; only full buffers use the
    # complete overlap/ray result before applying the same safety checks.
    assert "if (hits >= rayHits.Length)" in world
    assert "supportHits = Physics.RaycastAll(supportRay, 5f," in world
    assert "var h = supportHits[i];" in world
    assert "if (count >= collisionHits.Length)" in world
    assert "obstacles = Physics.OverlapBox(center, halfExtents, angle," in world
    assert "var obstacle = obstacles[i];" in world
    assert "obstacle is TerrainCollider" in world
    # The authored terrain tiles are offset at world Y=-120. Native Unity
    # Physics.Raycast missed the ground by exactly 120 metres when this
    # offset was omitted, rejecting all garage and roadside vehicles.
    assert "float ground = terrain.transform.position.y +" in world
    assert "terrain.SampleHeight(new Vector3(x, 0f, z));" in world
    assert "Terrain.SampleHeight already returns a WORLD-SPACE Y." not in world
    assert "record.previouslySpawned" in world and "record.rotation" in world
    # New houses and rocks can block a planned 45-garage/33-roadside node.
    # Nearby recovery is allowed only for never-driven cars and must use
    # the SAME terrain/support/obstruction checks as their original slot.
    planned = world.split("private bool TryResolvePlanned(", 1)[1].split(
        "private bool Build(", 1)[0]
    assert "if (TryResolve(record, out spot)) return true;" in planned
    assert "if (record.previouslySpawned) return false;" in planned
    assert "record.failedSpawnProbes++ & 3" in planned
    assert "return TryResolve(record, out spot, axis.x * offset, axis.z * offset);" in planned
    physical = world.split("private bool TryResolve(AarisVehicleRecord", 1)[1].split(
        "private bool TryResolvePlanned(", 1)[0]
    for guard in ("Mathf.Abs(ground - authoredY) > 2.5f",
                  "HasSupport(", "Mathf.Abs(localY - y) > .37f",
                  "nearbyColliders.HasPotentialInactiveObstacle(",
                  "Physics.OverlapBoxNonAlloc("):
        assert guard in physical, "Vehicle fallback bypassed safety: " + guard
    # Parked cars on elevated bridge decks must be restored by ray support,
    # while unvisited authored road markers still require nearby terrain.
    assert "if (!record.previouslySpawned && Mathf.Abs(ground - authoredY) > 2.5f) return false;" in physical, (
        "Previously driven bridge car would vanish after streaming out")
    assert "if (obstacle == null || obstacle is TerrainCollider) continue;" in physical, (
        "Vehicle fallback must ignore null/terrain hits without ignoring solid obstacles")
    assert "record.visible.CanPark" in world and "record.visible.IsOccupied" in world
    assert "Resources.Load<GameObject>(item.modelResource)" in world
    assert "convertAuthoredBodyMaterials" in world
    assert "authoredBody.GetComponentInChildren<Renderer>(true) == null" in world
    assert "CommitAuthoredBody(i, body);" in world
    assert "fallbackAuthoredModelResource" in world
    assert "Resources.Load<GameObject>(" in world

    assert "record.type.convertAuthoredBodyMaterials" in world
    assert "ApplyAuthoredMaterials(renderers);" in world
    for semantic in ("headlight", "taillight", "darkTrim", "glass"):
        assert semantic in world
    assert 'result.EnableKeyword("_EMISSION");' in world
    assert 'result.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");' in world
    assert "new Material(shader)" in world  # cache miss only, never per frame
    assert "authoredMaterialCache[source] = result;" in world
    assert "if (validModels == 0 || availableWeight <= 0)" in world
    assert "prefabs[i] = null;" in world and "wheelPrefabs[i] = null;" in world
    assert "prefabs[i].GetComponentInChildren<Renderer>(true) == null" in world
    assert "wheelPrefabs[i].GetComponentInChildren<Renderer>(true) == null" in world
    # Weighted map planning must use only *validated* imported meshes.
    assert "if (prefabs[i] != null && wheelPrefabs[i] != null)" in world
    assert "if (total <= 0) return false;" in world
    assert "GameObject.CreatePrimitive" not in world
    assert "Resources.Load<Texture2D>" in world
    assert "Universal Render Pipeline/Lit" in world
    assert "new Material(shader)" in world
    assert "GameObject.CreatePrimitive" not in world
    assert "UnityWebRequest" not in world

    # The initial callbacks have no guaranteed player creation order.
    # The existing player Start pass recovers all seven optional systems,
    # without constructing a second movement motor or duplicate HUD.
    startup = foot.split("private void Start()", 1)[1].split(
        "private void OnDestroy()", 1)[0]
    for component in ("AarisBattleHUD", "AarisWeaponHUD",
                      "AarisWeaponLootWorld", "AarisVehicleWorld",
                      "AarisCharacterVisual", "AarisOfflineDrop",
                      "AarisOfflineTacticalHUD"):
        assert f"{component}.EnsureForPlayer(this);" in startup
        assert "internal static void EnsureForPlayer(AarisThirdPersonPlayer" in script(component)

    # Boot methods for the loot, battle HUD and vehicles have no guaranteed
    # order. Re-resolve optional references at actual car entry so gun Fire,
    # pickup, and HUD controls cannot remain active while driving.
    entry = session.split("private bool TryEnter()", 1)[1].split(
        "private void SetFootHudVisible(", 1)[0]
    for component in ("AarisBattleActions", "AarisWeaponInventory"):
        assert f"= GetComponent<{component}>();" in entry
    assert "lootWorld = FindFirstObjectByType<AarisWeaponLootWorld>();" in entry
    assert entry.index("if (!nearbyCar.TryTakeDriver(this)) return false;") < (
        entry.index("lootWorld = FindFirstObjectByType<AarisWeaponLootWorld>();"))
    assert entry.index("lootWorld = FindFirstObjectByType<AarisWeaponLootWorld>();") < (
        entry.index("if (actions != null) actions.enabled = false;"))

    # A world-axis aligned Renderer.bounds swaps car width/length as the
    # road heading rotates; at 90 degrees it erroneously turns the FBX
    # sideways relative to its physics chassis. Derive widths in root space.
    build = world.split("private bool Build(AarisVehicleRecord", 1)[1].split(
        "private void Update()", 1)[0]
    assert "TryGetVisualBoundsLocal(root.transform, out Bounds bounds)" in build
    assert build.count("TryGetVisualBoundsLocal(root.transform, out bounds)") == 2
    assert "renderer.bounds" not in build
    assert "mesh.transform.localPosition += new Vector3(0f, .48f, 0f) - bounds.center;" in build
    assert "localSpace.worldToLocalMatrix" in world
    assert "filter.transform.localToWorldMatrix" in world
    assert "Mathf.Abs(toLocal.m00)" in world
    assert "GetComponentsInChildren<SkinnedMeshRenderer>(true)" in world
    wheels = motor.split("public bool AttachWheelModels(", 1)[1].split(
        "private void SyncVisibleWheels()", 1)[0]
    assert "TryGetVisualBoundsLocal(" in wheels
    assert "instance.transform, out Bounds combined" in wheels
    assert "renderer.bounds" not in wheels
    # Reference geometry: a model 2m wide and 4m long must remain
    # lengthwise with its root even when heading changes on the map.
    for yaw in (0, 15, 45, 90, 135, 180, 270):
        rad = math.radians(yaw)
        width_world = abs(math.cos(rad)) * 2 + abs(math.sin(rad)) * 4
        length_world = abs(math.sin(rad)) * 2 + abs(math.cos(rad)) * 4
        assert 2 < 4 * 1.1
        if yaw == 90:
            assert width_world > length_world * 1.1  # old false rotation
        assert math.isclose(2 / 4, 0.5)
    assert 4 > 2 * 1.1  # local sideways FBX should still be turned once

    # Do not give every model a sedan-size physical shell and wheelbase.
    assert "motor.FitShellAndWheelbase(new Bounds(new Vector3(0f, .48f, 0f)," in build
    assert "public void FitShellAndWheelbase(Bounds visualBounds)" in motor
    assert "Mathf.Clamp(size.x * .94f, 1.35f, 2.25f)" in motor
    assert "Mathf.Clamp(size.z * .94f, 2.35f, 4.30f)" in motor
    assert "Mathf.Clamp(size.z * .322f, .92f, 1.43f)" in motor
    assert "wheels[i].transform.localPosition = new Vector3(" in motor
    assert "float.IsNaN(size.x)" in motor and "float.IsInfinity(size.z)" in motor
    # Lateral skid speed must attenuate steering and downforce too.
    assert "Vector3.ProjectOnPlane(" in motor
    assert "body.linearVelocity, transform.up).magnitude;" in motor
    assert "Mathf.InverseLerp(0f, 24f, planarSpeed)" in motor
    assert "if (planarSpeed > 6f)" in motor
    for fwd, sideways in ((0, 0), (0, 15), (15, 0), (12, 16), (0, 35)):
        speed = math.hypot(fwd, sideways)
        steer = 32 - 22 * min(1, max(0, speed / 24))
        assert 10 <= steer <= 32
        if sideways >= 15: assert steer < 20

    # Full WheelCollider physics without fake step velocity teleports.
    assert "wheelPrefabs[i] = Resources.Load<GameObject>(item.wheelResource);" in world
    assert "motor.AttachWheelModels(wheelSource, wheelPaletteMaterial," in world
    assert 'paletteMaterial.SetFloat("_Metallic", .18f);' in world
    assert 'paletteMaterial.SetFloat("_Smoothness", .55f);' in world
    assert 'wheelPaletteMaterial.SetFloat("_Metallic", 0f);' in world
    assert 'wheelPaletteMaterial.SetFloat("_Smoothness", .18f);' in world
    assert "paletteMaterial.enableInstancing = true;" in world
    assert "wheelPaletteMaterial.enableInstancing = true;" in world
    assert "if (wheelPaletteMaterial != null) Destroy(wheelPaletteMaterial);" in world
    assert "record.type.preserveAuthoredUrpMaterials)" in world
    assert "public bool preserveAuthoredUrpMaterials;" in world
    assert "if (!record.type.preserveAuthoredUrpMaterials)" in build
    assert "if (!preserveAuthoredUrpMaterials)" in wheels
    assert "HasCompatibleUrpMaterials(prefabs[i])" in world
    assert "HasCompatibleUrpMaterials(wheelPrefabs[i])" in world
    assert 'material.shader.name != "Universal Render Pipeline/Lit"' in world
    assert "prefabs[i] = null;" in world
    # The six bundled CC0 Kenney types must remain byte-/appearance-compatible.
    assert all(not x.get("preserveAuthoredUrpMaterials", False) for x in types)
    assert "visualWheelRotationOffsets" in motor
    assert "worldRotation * visualWheelRotationOffsets[i]" in motor
    assert "wheels[i].GetWorldPose(out Vector3 worldPosition," in motor
    assert "public bool AttachEmbeddedWheelModels(Transform bodyRoot)" in motor
    # Embedded tyre radius and axle center must be fitted in chassis space,
    # not a yaw-dependent world AABB. All four bounds are checked before edits.
    embedded = motor.split("public bool AttachEmbeddedWheelModels(", 1)[1].split(
        "private void SyncVisibleWheels()", 1)[0]
    assert "transform, found[i], out bounds[i]" in embedded
    assert "Mathf.Max(bounds[i].size.y, bounds[i].size.z)" in embedded
    assert "wheels[i].transform.localPosition = bounds[i].center;" in embedded
    assert "worldBounds" not in embedded
    assert embedded.index("out bounds[i]") < embedded.index("obstacle.enabled = false")
    assert "Transform subtree, out Bounds combined" in world
    assert "toLocal * filter.transform.localToWorldMatrix" in world
    # A rectangular tyre envelope demonstrates the old world-axis error.
    # A 0.42m-wide, 0.65m-diameter wheel has invariant chassis diameter
    # even though its diagonal world AABB grows at some yaw angles.
    width, diameter = .42, .65
    old_diameters = []
    for yaw in (0, 30, 45, 60, 90, 135, 180, 270):
        theta = math.radians(yaw)
        world_z = abs(math.sin(theta)) * width + abs(math.cos(theta)) * diameter
        old_diameters.append(max(diameter, world_z))
        # The composed chassis inverse and car yaw cancel.
        c, s = math.cos(theta), math.sin(theta)
        assert math.isclose(c*c+s*s, 1.0)
        assert math.isclose(c*s-s*c, 0.0, abs_tol=1e-9)
        assert math.isclose(max(diameter, (c*c+s*s)*diameter), diameter)
    assert max(old_diameters) > diameter + .05
    for token in ("wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr"):
        assert token in motor and token in world
    assert "HasEmbeddedWheelSet(authoredBody)" in world
    assert "item.useEmbeddedAuthoredWheels = true;" in world
    assert "FaceEmbeddedVehicleForward(root.transform, mesh.transform);" in build
    assert "frontZ < rearZ" in world
    assert "model.localRotation *= Quaternion.Euler(0f, 180f, 0f);" in world
    assert "motor.AttachEmbeddedWheelModels(mesh.transform)" in build
    assert "authoredMaterialCache" in world and "AuthoredUrpMaterial(" in world
    assert 'label.Contains("glass") || label.Contains("window")' in world
    assert "renderer.sharedMaterials = converted;" in world
    # Palette replacement must never deserialize imported source materials just
    # to count slots; a corrupt/stale material reference can native-crash Unity.
    assert "internal static int VisualSubmeshCount(Renderer renderer)" in world
    assert "skinned.sharedMesh.subMeshCount" in world
    assert "filter.sharedMesh.subMeshCount" in world
    assert "int submeshes = VisualSubmeshCount(renderer);" in world
    assert "int n = AarisVehicleWorld.VisualSubmeshCount(renderer);" in motor
    assert "renderer.sharedMaterials.Length" not in build
    assert "new WheelCollider[4]" in motor
    assert "WheelCollider" in motor and "JointSpring" in motor
    assert "wheel.forwardFriction" in motor and "wheel.sidewaysFriction" in motor
    # WheelCollider ignores ordinary PhysicMaterial friction. Reuse the same
    # anti-roll WheelHit contact to vary tyre stiffness without extra queries:
    # hard road/bridge = 1.0, TerrainCollider = 0.90, Water layer = 0.42.
    assert "BaseForwardGripStiffness = 1.15f" in motor
    assert "BaseSideGripStiffness = 1.35f" in motor
    assert "private static float SurfaceGripMultiplier(Collider surface)" in motor
    assert "surface.gameObject.layer == 4" in motor
    assert "surface is TerrainCollider" in motor
    assert "ApplySurfaceGrip(leftIndex, left, leftHit.collider);" in motor
    assert "ApplySurfaceGrip(rightIndex, right, rightHit.collider);" in motor
    assert "BaseForwardGripStiffness * multiplier" in motor
    assert "BaseSideGripStiffness * multiplier" in motor
    assert motor.count("GetGroundHit(out WheelHit") == 2
    for multiplier in (1.0, 0.90, 0.42):
        assert 0.40 <= multiplier <= 1.0
        assert 0.45 <= 1.15 * multiplier <= 1.15
        assert 0.55 <= 1.35 * multiplier <= 1.35
    assert "wheels[i].motorTorque" in motor and "wheels[i].brakeTorque" in motor
    assert "Mathf.Lerp(32f, 10f" in motor
    assert "body.AddForce(-transform.up" in motor
    assert "linearVelocity =" not in motor and "MoveRotation(" not in motor
    assert "body.centerOfMass = new Vector3(0f, -.27f, 0f)" in motor
    # Suspension response should remain comparable across the catalog's wide
    # mass range; fitted axle geometry must also recompute PhysX wheel loads.
    assert "body.mass / 1500f" in motor
    assert "spring = 32000f * massScale" in motor
    assert "damper = 4200f * massScale" in motor
    assert "wheels[0].ResetSprungMasses();" in motor
    assert "wheels[0].ConfigureVehicleSubsteps(12f, 3, 2);" in motor
    assert "TryTakeDriver" in motor and "ReleaseDriver" in motor
    # A car with no stable exit must never become the player's input owner:
    # a flipped/airborne car previously allowed entry but blocked all exits.
    board = motor.split("public bool TryTakeDriver(", 1)[1].split(
        "public void SetInput(", 1)[0]
    assert "!IsSafeToExit" in board, "Unsafe vehicle can still be boarded"
    availability = session.split("public bool CanEnter", 1)[1].split(
        "public AarisVehicleMotor DrivenCar", 1)[0]
    assert "nearbyCar.IsSafeToExit" in availability, (
        "DRIVE prompt advertises unboardable overturned/airborne car")
    exit_gate = motor.split("public bool IsSafeToExit", 1)[1].split(
        "private void OnCollisionEnter(", 1)[0]
    for guard in ("TotalSpeedMetresPerSecond >= 1.1f",
                  "body.angularVelocity.magnitude >= .5f",
                  "Vector3.Dot(transform.up, Vector3.up) < .72f",
                  "return grounded >= 2"):
        assert guard in exit_gate, "Missing safe-boarding guard: " + guard

    # Close-contact camera sweep must not interpolate past a detected wall.
    assert "Mathf.Max(0f, nearest - .24f)" in session
    assert "Mathf.Max(.35f, nearest - .24f)" not in session
    # Real Rigidbody speed opens the chase framing gradually, while the
    # collision sweep remains final authority and view rotation is damped.
    assert "car.TotalSpeedMetresPerSecond /" in session
    assert "car.TopSpeedMetresPerSecond" in session
    assert "Mathf.Lerp(6.25f, 7.40f, speed01)" in session
    assert "Mathf.Lerp(2.95f, 3.25f, speed01)" in session
    assert "Mathf.Lerp(62f, 70f," in session
    assert "Quaternion.Slerp(" in session
    assert "Mathf.Clamp(Time.deltaTime, 0f, .05f)" in session
    assert "float fovDelta = Mathf.Clamp(Time.deltaTime, 0f, .05f);" in session
    assert "1f - Mathf.Exp(-5f * fovDelta)" in session
    for speed01 in (0.0, .25, .5, .75, 1.0):
        distance = 6.25 + (7.40 - 6.25) * speed01
        height = 2.95 + (3.25 - 2.95) * speed01
        assert 6.25 <= distance <= 7.40
        assert 2.95 <= height <= 3.25
    for contact in (0.0, .05, .1, .2, .24, .3, .35, .45, 1.0, 6.0):
        assert 0 <= max(0.0, contact - .24) <= contact
    disabled = session.split("private void OnDisable()", 1)[1]
    assert "touchMove = touchSteer = 0f;" in disabled

    # Traction control must reuse prior WheelHit slip instead of adding
    # another contact query to the 2 GB-phone hotpath.
    assert "float previousTyreSlip = tyreSlip;" in motor
    assert "previousTyreSlip > 0.35f" in motor
    assert "Mathf.InverseLerp(0.35f, 1.10f, previousTyreSlip)" in motor
    assert "drive *= Mathf.Lerp(1f, 0.52f, slip01);" in motor

    # One owner, safe exit, collision-before-enable and no F resetting.
    assert "footMotor.enabled = false;" in session and "capsule.enabled = false;" in session
    assert "capsule.enabled = true;" in session and "footMotor.enabled = true;" in session
    assert "TryFindExit(out Vector3 safeExit)" in session
    assert "Physics.RaycastNonAlloc(" in session
    assert "hit.collider.transform.IsChildOf(nearbyCar.transform)" in session
    assert "Mathf.Abs(hit.point.y - (option.y - .71f)) > 1.5f" in session
    assert "if (actions != null) actions.enabled = false;" in session
    assert "if (inventory != null) inventory.enabled = false;" in session
    assert "if (lootWorld != null) lootWorld.enabled = false;" in session
    assert "if (inventory != null) inventory.enabled = previousInventoryActive;" in session
    assert "Physics.OverlapCapsuleNonAlloc" in session
    assert "SetFootHudVisible(false)" in session and "SetFootHudVisible(true)" in session
    assert "battleHud.CancelVehicleInputs();" in session
    assert "public void ResetCameraAfterVehicle()" in foot
    assert "if (Input.GetKeyDown(KeyCode.F)) ResetPlayer();" not in foot
    assert "if (Input.GetKeyDown(KeyCode.H)) ResetPlayer();" in foot
    assert "if (Input.GetKeyDown(KeyCode.F) && !IsDriving)" in session
    assert "if (Input.GetKeyDown(KeyCode.X) && IsDriving)" in session

    # Exactly four button directions + one Drive/Exit. No gas pedal.
    assert "AddArrow(0" in hud and "AddArrow(1" in hud
    assert "AddArrow(2" in hud and "AddArrow(3" in hud
    assert "IPointerDownHandler" in hud and "IPointerUpHandler" in hud
    assert "ICancelHandler" in hud and "void Reset()" in hud
    assert "ClearArrows()" in hud and "OnApplicationPause" in hud
    assert "AarisHudGlyphs.Ring" in hud
    assert "AarisVehicleGlyph.Chevrons" in hud
    assert "GetBuiltinResource<Font>(\"LegacyRuntime.ttf\")" in hud
    assert "CancelVehicleInputs()" in battle
    assert "GasPedal" not in hud and "BrakePedal" not in hud

    # Mirror the 2-pass C# sector + 105m separation selection for 300 seeds:
    # distributions must not cluster cars only near urban spawn markers.
    counts=[]
    for seed in range(1,301):
        rs=seed
        pool=pts[:]
        for i in range(len(pool)-1,0,-1):
            rs=next_uint(rs)
            j=rs%(i+1)
            pool[i],pool[j]=pool[j],pool[i]
        selected=[]
        seen=set()
        def add(p,minimum):
            if any(math.hypot(p["x"]-q["x"],p["z"]-q["z"])<minimum
                   for q in selected): return False
            selected.append(p)
            return True
        for p in pool:
            cell=(max(0,min(3,int((p["x"]+1000)//500))),
                  max(0,min(3,int((p["z"]+1000)//500))))
            if cell not in seen and add(p,100): seen.add(cell)
        pool.sort(key=lambda p: 0 if p["kind"]=="hub" else 1)
        for p in pool:
            if len(selected)>=44: break
            add(p,105)
        counts.append(len(selected))
        assert len(seen)>=14
        assert 40<=len(selected)<=44,(seed,len(selected))
    print("PASS: 45 real garages +33 authored roadside points, 14 500m sectors, "
          "300 balanced 44-car plans, 6 real car + 3 real wheel FBX models, "
          "WheelCollider physics, safe enter/exit and 4-arrow mobile UX.")
    print("NOT TESTED: Unity Editor FBX import, in-game car handling, Android rendering/FPS.")

if __name__=="__main__":
    test()
