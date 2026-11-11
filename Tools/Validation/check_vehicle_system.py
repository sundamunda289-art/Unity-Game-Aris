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

    # Bound 2km car plans, floor/bridge/mountain and wall clearance.
    assert "TargetVehicles = 26" in world
    assert "file.hubCount != 45" in world
    assert "var sectors = new HashSet<int>();" in world
    assert "int sector = Mathf.Clamp" in world
    assert "AddPlan(point, 105f)" in world
    assert "LoadRadius = 48f" in world and "UnloadRadius = 160f" in world
    assert "supports = Physics.RaycastAll(" in world
    assert "record.previouslySpawned ||" in world
    assert "MaximumNearbyVehicles = 10" in world
    assert "Physics.OverlapBoxNonAlloc" in world
    assert "Physics.RaycastNonAlloc" in world
    assert "float ground = terrain.SampleHeight(" in world
    assert "terrain.transform.position.y +" not in world
    assert "Terrain.SampleHeight already returns a WORLD-SPACE Y." in world
    assert "record.previouslySpawned" in world and "record.rotation" in world
    assert "record.visible.CanPark" in world and "record.visible.IsOccupied" in world
    assert "Resources.Load<GameObject>(item.modelResource)" in world
    assert "Resources.Load<Texture2D>" in world
    assert "Universal Render Pipeline/Lit" in world
    assert "new Material(shader)" in world
    assert "GameObject.CreatePrimitive" not in world
    assert "UnityWebRequest" not in world

    # Full WheelCollider physics without fake step velocity teleports.
    assert "wheelPrefabs[i] = Resources.Load<GameObject>(item.wheelResource);" in world
    assert "motor.AttachWheelModels(wheelSource, paletteMaterial)" in world
    assert "visualWheels[i].SetPositionAndRotation(worldPosition, worldRotation);" in motor
    assert "wheels[i].GetWorldPose(out Vector3 worldPosition," in motor
    assert "new WheelCollider[4]" in motor
    assert "WheelCollider" in motor and "JointSpring" in motor
    assert "wheel.forwardFriction" in motor and "wheel.sidewaysFriction" in motor
    assert "wheels[i].motorTorque" in motor and "wheels[i].brakeTorque" in motor
    assert "Mathf.Lerp(32f, 10f" in motor
    assert "body.AddForce(-transform.up" in motor
    assert "linearVelocity =" not in motor and "MoveRotation(" not in motor
    assert "body.centerOfMass = new Vector3(0f, -.27f, 0f)" in motor
    assert "TryTakeDriver" in motor and "ReleaseDriver" in motor

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
            if len(selected)>=26: break
            add(p,105)
        counts.append(len(selected))
        assert len(seen)>=14
        assert 22<=len(selected)<=26,(seed,len(selected))
    print("PASS: 45 real garages +33 authored roadside points, 14 500m sectors, "
          "300 balanced 26-car plans, 6 real car + 3 real wheel FBX models, "
          "WheelCollider physics, safe enter/exit and 4-arrow mobile UX.")
    print("NOT TESTED: Unity Editor FBX import, in-game car handling, Android rendering/FPS.")

if __name__=="__main__":
    test()
