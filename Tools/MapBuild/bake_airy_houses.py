"""Bake the shared prefabs, paired scene representations and house loot manifest.

Run from the repository root with the four TerrainData assets hydrated.
The authoring pass consumes the authored scenes once. --check audits the saved
result without regenerating, resculpting or opening an Editor project.
"""
import argparse
import base64
import json
import re
from collections import Counter
from math import atan2, degrees, hypot
from pathlib import Path
from zlib import crc32

import numpy as np
import yaml

from airy_house_geometry import HOUSES, build
from airy_house_scatter import Placement, Terrain, local_limits, rotate
from native_assets import Dumper, Flow, guid, material_asset, mesh_asset, quat_y, ref, vec
from native_scene import Scene


ASSETS = Path("Assets/AarisWorld/Models/AiryHouses")
MODELS = Path("Assets/AarisWorld/Models")
RESOURCES = Path("Assets/Resources/AarisBuildings")
CHUNKS = Path("Assets/AarisWorld/Scenes/Chunks")
BOOTSTRAP = Path("Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity")
DOCUMENT = Path("Assets/Resources/AarisWeapons/loot_candidates.json")
EXPANSION = Path("Assets/Resources/AarisExpansion/WorldExpansionPlan.json")
AUDIT = Path("Docs/AIRY_HOUSE_AUDIT.json")
NEARBY_GUID = "f99aebde001f56119c583c593f64c271"
SIMPLE_LIT = "8d2bb70cbf9db8d4da26e15b26e74248"
HEADER = "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"
SEPARATOR = re.compile(r"(?m)^--- !u!(\d+) &(-?\d+)( stripped)?\n")

MODEL_TYPE = {
    "a_frame_cabin":"RidgeHouse", "bungalow_house":"RidgeHouse",
    "pitched_roof_cottage":"CourtyardHouse", "detached_two_storey_house":"TerraceHouse",
    "garage_apartment":"CoastalHouse", "row_three_townhouses":"LongBarracks",
    "two_bedroom_garage_house":"CoastalHouse", "warehouse_loading_dock":"LongBarracks",
    "TownHall":"TerraceHouse", "CanalTown":"LongBarracks", "HanokVillage":"BrokenPavilion",
    "Harbour":"LongBarracks", "Airfield":"LongBarracks", "ParcelDepot":"LongBarracks",
}
RESOURCE_TYPE = {"OpenBungalow":"RidgeHouse", "OpenCottage":"CourtyardHouse", "OpenDetached":"TerraceHouse"}
COMPOUND_YAWS = dict(zip(("CanalMarket","ClockTowerTown","CoastalHamlet","EasternPort",
                          "FreightDistrict","HighlandObservatory","LakesideHamlet",
                          "NorthernRidgeTown","OldVillage"), (90,45,135,180,45,90,135,180,90)))
# Keep a flush approach on the same district pad. CanalTown's old footprint
# crossed a break in grade, and EasternPort's crossed the saved pier deck.
FLAT_PAD_ANCHORS = {
    "CanalMarket_CanalTown_01": (-120,691),
    "EasternPort_two_bedroom_garage_house_03": (881.6,388),
}


def unity_doc(kind, ident, name, data, stripped=False):
    addition = " stripped" if stripped else ""
    return f"--- !u!{kind} &{ident}{addition}\n" + yaml.dump(
        {name:data}, Dumper=Dumper, sort_keys=False, width=1024)


def asset_guid(path):
    meta = Path(str(path)+".meta")
    return re.search(r"(?m)^guid: ([a-f0-9]+)$", meta.read_text())[1] if meta.is_file() else guid(path)


class UnityFile:
    def __init__(self, path):
        self.path = path
        self.docs = {}
        self.kinds = {}
        self.names = {}
        self.roots = {}
        text = path.read_text()
        matches = list(SEPARATOR.finditer(text))
        for i, m in enumerate(matches):
            kind, ident = int(m[1]), int(m[2])
            doc = text[m.start():matches[i+1].start() if i+1 < len(matches) else len(text)]
            self.docs[ident] = doc
            self.kinds[ident] = kind
            if kind == 1:
                match = re.search(r"(?m)^  m_Name: (.*)$",doc)
                if match is not None:
                    self.names[match[1]] = ident
            elif kind == 4 and "  m_Father: {fileID: 0}" in doc:
                self.roots[ident] = self.reference(doc,"m_GameObject")
        self.next_id = 8700000000000000000

    def new_id(self):
        while self.next_id in self.docs:
            self.next_id += 1
        ident = self.next_id
        self.next_id += 1
        return ident

    @staticmethod
    def reference(doc, field):
        return int(re.search(rf"(?m)^  {field}: {{fileID: (-?\d+)",doc)[1])

    def components(self, go):
        return [int(m) for m in re.findall(r"(?m)^  - component: {fileID: (-?\d+)}",self.docs[go])]

    def component(self, go, kind):
        match, = [identity for identity in self.components(go) if self.kinds[identity]==kind]
        return match

    def vector(self, identity, key):
        value = re.search(rf"(?m)^  {key}: ({{.+}})$",self.docs[identity])[1]
        dims = yaml.safe_load(value)
        return tuple(dims[axis] for axis in "xyzw" if axis in dims)

    def set_field(self, identity, field, value):
        self.docs[identity], changed = re.subn(rf"(?m)^  {field}:.*$", f"  {field}: {value}",self.docs[identity])
        if changed != 1:
            raise ValueError(f"Expected one {field} on {self.path}:{identity}")

    def transform(self, identity, position, yaw, scale):
        for name,value in (("m_LocalPosition",vec(*position)), ("m_LocalScale",vec(*scale)),
                           ("m_LocalRotation",quat_y(yaw)), ("m_LocalEulerAnglesHint",vec(0,yaw,0))):
            self.set_field(identity,name,yaml.dump(value,Dumper=Dumper,width=1000).strip())

    def replace_mesh(self, identity, mesh):
        self.set_field(identity,"m_Mesh",f"{{fileID: 4300000, guid: {mesh}, type: 2}}")

    def replace_materials(self, identity, materials):
        block = "  m_Materials:\n" + "".join(
            f"  - {{fileID: 2100000, guid: {material}, type: 2}}\n" for material in materials)
        self.docs[identity], changed = re.subn(r"(?ms)^  m_Materials:\n(?:  - .*\n)+",block,self.docs[identity], count=1)
        if changed != 1:
            raise ValueError(f"Missing material slots on {self.path}:{identity}")

    def drop_tree(self, root):
        go = self.reference(self.docs[root],"m_GameObject")
        match = re.search(r"(?m)^  m_Children:\n((?:  - {fileID: -?\d+}\n)+)",self.docs[root])
        if match:
            for child in re.findall(r"fileID: (-?\d+)",match[1]):
                self.drop_tree(int(child))
        docs = set(self.components(go)) | {go}
        for identity in docs:
            del self.docs[identity]
            del self.kinds[identity]

    def add_references(self, component, property_name, identities):
        line = f"  {property_name}:\n"
        references = "".join(f"  - {{fileID: {ident}}}\n" for ident in identities)
        if line not in self.docs[component]:
            raise ValueError(f"Missing {property_name} on {self.path}:{component}")
        self.docs[component] = self.docs[component].replace(line,line+references,1)

    def prefab(self, prefab, name, position, yaw, scale, root=None, detail=False):
        source_guid, go, transform, collider = prefab
        instance = self.new_id()
        root = self.new_id() if root is None else root
        changes = []

        def override(target, key, value):
            changes.append({"target":ref(target,source_guid,3),"propertyPath":key,
                            "value":value,"objectReference":ref()})

        override(go,"m_Name",name)
        for field, values in (("m_LocalPosition",vec(*position)),("m_LocalRotation",quat_y(yaw)),
                              ("m_LocalScale",vec(*scale)),("m_LocalEulerAnglesHint",vec(0,yaw,0))):
            for axis,value in values.items():
                override(transform,field+"."+axis,round(float(value),8))
        self.docs[instance] = unity_doc(1001,instance,"PrefabInstance",{
            "m_ObjectHideFlags":0,"serializedVersion":2,
            "m_Modification":{"serializedVersion":3,"m_TransformParent":ref(),"m_Modifications":changes,
                              "m_RemovedComponents":[ref(collider,source_guid,3)] if detail else [],
                              "m_RemovedGameObjects":[],"m_AddedGameObjects":[],"m_AddedComponents":[]},
            "m_SourcePrefab":ref(100100000,source_guid,3)})
        stripped = {"m_CorrespondingSourceObject":ref(transform,source_guid,3),
                    "m_PrefabInstance":ref(instance),"m_PrefabAsset":ref()}
        self.docs[root] = unity_doc(4,root,"Transform",stripped,True)
        if detail:
            return None,None
        physics = self.new_id()
        stripped["m_CorrespondingSourceObject"] = ref(collider,source_guid,3)
        self.docs[physics] = unity_doc(64,physics,"MeshCollider",stripped,True)
        return root,physics

    def save(self):
        self.path.write_text(HEADER+"".join(self.docs.values()))


def render_materials():
    limestone = asset_guid(RESOURCES/"ArchitecturalLimestone.png")
    normal = asset_guid(RESOURCES/"ArchitecturalLimestoneNormal.png")
    weathered = asset_guid(MODELS/"RuinedHouses/WeatheredMasonry.png")
    palette = {
        "linen":("Limestone",(.87,.83,.76),limestone,normal),
        "ochre":("Ochre stucco",(.78,.63,.46),limestone,normal),
        "sage":("Sage stucco",(.68,.74,.67),limestone,normal),
        "stone":("Weathered concrete",(.61,.62,.61),weathered,None),
        "wood":("Dark timber",(.36,.255,.192),limestone,None),
        "roof":("Slate coping",(.39,.44,.46),weathered,None),
    }
    out = {}
    for key,(name,color,albedo,bump) in palette.items():
        tex = {"_BaseMap":albedo}
        if bump is not None:
            tex["_BumpMap"] = bump
        mat = material_asset(ASSETS/(key+".mat"),name,(*color,1),smoothness=.15,textures=tex,
                             shader=SIMPLE_LIT,extra={"_Cull":2,"_BumpScale":.52})
        out[key] = mat
    return out


def prefab_asset(path, name, geometry, lods, collider_guid):
    previous = UnityFile(path) if path.is_file() else None
    meta_path = Path(str(path)+".meta")
    old_meta = meta_path.read_bytes() if meta_path.is_file() else None
    scene = Scene()
    scene.next_id = 7000000000000000000
    root,transform,obj = scene.object(name)
    obj["m_StaticEditorFlags"] = 8
    coll = scene.component(root,obj,64,"MeshCollider",{
        "m_Material":ref(),"m_IncludeLayers":{"serializedVersion":2,"m_Bits":0},
        "m_ExcludeLayers":{"serializedVersion":2,"m_Bits":0},"m_LayerOverridePriority":0,
        "m_IsTrigger":0,"m_ProvidesContacts":0,"m_Enabled":1,"serializedVersion":5,
        "m_Convex":0,"m_CookingOptions":30,"m_Mesh":ref(4300000,collider_guid)})
    levels = []
    for level,(metadata,mats) in enumerate(lods):
        scene.model(f"LOD{level}",metadata["guid"],mats,parent=transform,collider=False,lod=False)
        levels.append({"screenRelativeHeight":(.085,.026,.005)[level],"fadeTransitionWidth":0,
                       "renderers":[{"renderer":ref(scene.renderers[-1])}]})
        scene.objects[-1][3]["m_LightProbeUsage"] = 0
        scene.objects[-1][3]["m_ReflectionProbeUsage"] = 0
        if level==2:
            scene.objects[-1][3]["m_CastShadows"] = 0
    bounds = lods[0][0]["bounds"]
    scene.component(root,obj,205,"LODGroup",{
        "serializedVersion":2,"m_LocalReferencePoint":bounds["m_Center"],
        "m_Size":2*max(bounds["m_Extent"].values()),"m_FadeMode":0,
        "m_AnimateCrossFading":0,"m_LastLODIsBillboard":0,"m_LODs":levels,"m_Enabled":1})
    _,routes,_ = scene.object("WalkRoute",parent=transform,static=False)
    for number, point in enumerate(geometry.route,1):
        scene.object(f"Step{number:02}",point,parent=routes,static=False)
    _,sockets,_ = scene.object("LootSpawnSockets",parent=transform,static=False)
    for number, point in enumerate(geometry.sockets,1):
        kind = "Roof" if point[1]>=geometry.house.storeys*2.85-.01 else "Indoor"
        scene.object(f"{kind}{number:02}",point,parent=sockets,static=False)
    if previous is not None and len(previous.roots)==1:
        old_transform,old_root = next(iter(previous.roots.items()))
        mapping = {root:old_root,transform:old_transform}
        if 64 in (previous.kinds[i] for i in previous.components(old_root)):
            mapping[coll] = previous.component(old_root,64)

        def remap(value):
            if isinstance(value,list):
                return [remap(n) for n in value]
            if isinstance(value,dict):
                output = type(value)((k,remap(v)) for k,v in value.items())
                if "fileID" in output and "guid" not in output:
                    output["fileID"] = mapping.get(output["fileID"],output["fileID"])
                return output
            return value

        scene.objects = [(kind,mapping.get(i,i),n,remap(data)) for kind,i,n,data in scene.objects]
        root,transform,coll = old_root,old_transform,mapping.get(coll,coll)
    assert len({i for _,i,_,_ in scene.objects})==len(scene.objects)
    scene.save(path,prefab=True)
    if old_meta is not None:
        meta_path.write_bytes(old_meta)
    return asset_guid(path),root,transform,coll


def make_assets(geometry):
    ASSETS.mkdir(parents=True,exist_ok=True)
    Path(str(ASSETS)+".meta").write_text(
        f"fileFormatVersion: 2\nguid: {guid(ASSETS)}\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {{}}\n")
    raw_template = base64.b64decode(Path("Tools/MapBuild/mesh_template.b64").read_text()).decode()
    template = "\n".join(line.rstrip() for line in raw_template.splitlines()) + "\n"
    materials = render_materials()
    prefabs, meshes, checks = {},{},{}
    for name,g in geometry.items():
        lods = []
        for level,mesh in enumerate(g.lods):
            p,n,u,i,groups,keys = mesh.arrays()
            info = mesh_asset(ASSETS/f"{name}_LOD{level}.asset",p,n,u,i,template,groups=groups)
            mapped = [materials[g.house.tint if key=="wall" else key] for key in keys]
            lods.append((info,mapped))
        p,n,u,i,_,_ = g.collider.arrays()
        collision = mesh_asset(ASSETS/f"{name}_Walkable.asset",p,n,u,i,template)
        prefabs[name] = prefab_asset(ASSETS/(name+".prefab"),name,g,lods,collision["guid"])
        meshes[name] = (lods,collision["guid"])
        checks[name] = {"storeys":g.house.storeys,"stairs":g.house.stairs,
                        "lod_triangles":[t[0]["triangles"] for t in lods],
                        "collision_triangles":collision["triangles"],
                        "indoor_sockets":sum(p[1]<g.house.storeys*2.85 for p in g.sockets),
                        "roof_sockets":sum(p[1]>=g.house.storeys*2.85 for p in g.sockets)}
    for old,preset in MODEL_TYPE.items():
        candidate = MODELS/old/(old+".prefab")
        if candidate.is_file():
            g = geometry[preset]
            lods,physics = meshes[preset]
            prefab_asset(candidate,old,g,lods,physics)
    for old,preset in RESOURCE_TYPE.items():
        g = geometry[preset]
        lods,physics = meshes[preset]
        prefab_asset(RESOURCES/(old+".prefab"),old,g,lods,physics)
    return prefabs,meshes,checks


def house_layout(layout, bootstrap, geometry, terrain):
    by_name = {b["name"]:b for b in layout["buildings"]}
    authored = []
    preserved = []
    used_compounds = set()
    for path in sorted(CHUNKS.glob("*_Detail.unity")):
        scene = UnityFile(path)
        for transform,go in scene.roots.items():
            name = re.search(r"(?m)^  m_Name: (.*)$",scene.docs[go])[1]
            source = by_name.get(name)
            if source:
                preset = MODEL_TYPE.get(source["model"])
            elif "_OpenDetached_" in name:
                district = name.split("_",1)[0]
                styles = ("TerraceHouse","RidgeHouse","CoastalHouse","CourtyardHouse","BrokenPavilion")
                preset = styles[crc32(name.encode()) % len(styles)]
            else:
                raise ValueError(f"Unclassified world structure: {name}")
            if preset is None:
                preserved.append(name)
                continue
            x,y,z = scene.vector(transform,"m_LocalPosition")
            x,z = FLAT_PAD_ANCHORS.get(name,(x,z))
            qx,qy,qz,qw = scene.vector(transform,"m_LocalRotation")
            if abs(qx)+abs(qz) > .001:
                raise ValueError(f"Unexpected sloped transform on {name}")
            yaw = round(degrees(2*atan2(qy,qw))%360,3)
            district = name.split("_",1)[0]
            g = geometry[preset]
            wide = source is not None and min(source["world_dimensions_m"][0::2])>22
            if wide and district in COMPOUND_YAWS:
                yaw = COMPOUND_YAWS[district]
                used_compounds.add(district)
            elif source and district in COMPOUND_YAWS and district not in used_compounds:
                bx,bz,bxx,bzz = source["aabb_xz_m"]
                if bxx-bx >= 15 and bzz-bz >= 15:
                    yaw = COMPOUND_YAWS[district]
                    used_compounds.add(district)
            elif source is None and district not in used_compounds and (
                    name == "CoastalHamlet_OpenDetached_Random_01" or
                    name == "LakesideHamlet_OpenDetached_Random_02"):
                # Both pivots have more than 29 m of road-centreline clearance.
                yaw = COMPOUND_YAWS[district]
                used_compounds.add(district)
            lo,hi = local_limits(g)
            dx = np.array((lo[0],hi[0],hi[0],lo[0]))
            dz = np.array((lo[1],lo[1],hi[1],hi[1]))
            scale = 1.2 if wide else 1.
            if source is not None and not wide:
                sw,_,sd = source["world_dimensions_m"]
                scale = min(1.16,max(.79,min(sw/(hi[0]-lo[0]),sd/(hi[1]-lo[1]))))
            xx,zz = rotate(dx*scale,dz*scale,yaw)
            built_bounds = [x+float(xx.min()),z+float(zz.min()),x+float(xx.max()),z+float(zz.max())]
            centre = float(terrain.heights(x,z))
            if abs(y-centre)>1.0 and name not in FLAT_PAD_ANCHORS:
                raise ValueError(f"House root elevation mismatches terrain: {name}, {y-centre:.2f} m")
            y = round(max(y,centre+.018),6)
            authored.append({"name":name,"prefab":preset,"category":"previous-native",
                             "district":district,"position":[x,y,z],"yaw":yaw,"scale":[scale,1,scale],
                             "chunk":str(path),"transform":transform,"bounds":built_bounds})
    return authored,preserved,used_compounds


def rewrite_scenes(bootstrap, homes, new, prefabs, meshes):
    collider_ids = []
    for path in sorted(CHUNKS.glob("*_Detail.unity")):
        scene = UnityFile(path)
        for home in (row for row in homes if row["chunk"] == str(path)):
            name,preset,transform = (home[key] for key in ("name","prefab","transform"))
            go = bootstrap.names["__AarisPersistentCover_"+name]
            tr = bootstrap.component(go,4)
            floor = bootstrap.component(go,64)
            fallback = bootstrap.names["__AarisFarSilhouette_"+name]
            # The persistent parent transforms are identity; preserve the
            # existing cover GameObject, Collider IDs and chunk visibility link.
            parent = bootstrap.reference(bootstrap.docs[tr],"m_Father")
            assert bootstrap.vector(parent,"m_LocalScale")== (1,1,1)
            assert bootstrap.vector(parent,"m_LocalPosition")== (0,0,0)
            assert bootstrap.vector(parent,"m_LocalRotation")== (0,0,0,1)
            lods,collision = meshes[preset]
            bootstrap.transform(tr,home["position"],home["yaw"],home["scale"])
            bootstrap.replace_mesh(floor,collision)
            bootstrap.replace_mesh(bootstrap.component(fallback,33),lods[2][0]["guid"])
            renderer = bootstrap.component(fallback,23)
            bootstrap.replace_materials(renderer,lods[2][1])
            bootstrap.set_field(renderer,"m_CastShadows",0)
            scene.drop_tree(transform)
            scene.prefab(prefabs[preset],name,home["position"],home["yaw"],home["scale"],root=transform,detail=True)
        if any(row["chunk"]==str(path) for row in homes):
            scene.save()
    root_ids = []
    for home in new:
        root,physics = bootstrap.prefab(prefabs[home["prefab"]],home["name"],home["position"],home["yaw"],home["scale"])
        root_ids.append(root)
        collider_ids.append(physics)
    near, = [i for i,doc in bootstrap.docs.items() if bootstrap.kinds.get(i)==114 and NEARBY_GUID in doc]
    roots, = [i for i,kind in bootstrap.kinds.items() if kind==1660057539]
    bootstrap.add_references(near,"obstacles",collider_ids)
    bootstrap.add_references(roots,"m_Roots",root_ids)
    bootstrap.save()


def write_loot(homes, geometry):
    body = DOCUMENT.read_text().rstrip()
    data = json.loads(body)
    if "houseSockets" in data:
        raise ValueError("The authored socket manifest already exists")
    sockets = []
    for home in sorted(homes,key=lambda h:h["name"]):
        x,y,z = home["position"]
        sx,_,sz = home["scale"]
        for index,(a,b,c) in enumerate(geometry[home["prefab"]].sockets,1):
            u,v = rotate(a*sx,c*sz,home["yaw"])
            slot = crc32(f"{home['name']}:{index}".encode()) & 0x7fffffff
            sockets.append({"kind":"socket","origin":home["name"],"x":round(x+u,4),
                            "y":round(y+b,4),"z":round(z+v,4),"slot":slot})
    assert len({s["slot"] for s in sockets})==len(sockets)
    data["houseSockets"] = sockets
    # Preserve byte-for-byte formatting of the existing outdoor/legacy table.
    lines = ",\n".join("    "+json.dumps(s,separators=(",",":")) for s in sockets)
    DOCUMENT.write_text(body[:-1].rstrip()+',\n  "houseSockets": [\n'+lines+"\n  ]\n}\n")
    return len(sockets)


def bake():
    if AUDIT.exists():
        raise ValueError("The authored houses are present. Run --check to audit their current references.")
    terrain = Terrain(Path.cwd())
    layout = json.loads(Path("Docs/MAP_LAYOUT.json").read_text())
    expansion = json.loads(EXPANSION.read_text())
    bootstrap = UnityFile(BOOTSTRAP)
    geometry = {name:build(house) for name,house in HOUSES.items()}
    homes,landmarks,rotated = house_layout(layout,bootstrap,geometry,terrain)
    by_name = {b["name"]:b for b in layout["buildings"]}
    reserved = [tuple(h["bounds"]) for h in homes] + [by_name[name]["aabb_xz_m"] for name in landmarks]
    previous = expansion["sites"]
    towers = [(a["x"],a["z"]) for a in previous if a["prefabKey"]=="watchtower"]
    towers.extend((by_name[name]["position_m"][0],by_name[name]["position_m"][2])
                  for name in landmarks if "tower" in name or "lighthouse" in name)
    for name,go in bootstrap.names.items():
        if name.endswith(("_ClimbableTower","_StaticWreck")) or "RockCover" in name:
            x,_,z = bootstrap.vector(bootstrap.component(go,4),"m_LocalPosition")
            reserved.append((x-8,z-8,x+8,z+8))
            if name.endswith("_ClimbableTower"):
                towers.append((x,z))
    for x,z in towers:
        reserved.append((x-9,z-9,x+9,z+9))
    for v in layout["vehicle_spawns"]:
        x,_,z = v["position_m"]
        xx,zz = v["exit_world_xz_m"]
        reserved.append((min(x,xx)-5,min(z,zz)-5,max(x,xx)+5,max(z,zz)+5))
    planner = Placement(Path.cwd(),terrain,layout,reserved,towers)
    baked_expansion = planner.expansion(previous)
    scattered = planner.scatter(layout)
    prefabs,meshes,assets = make_assets(geometry)
    rewrite_scenes(bootstrap,homes,baked_expansion+scattered,prefabs,meshes)
    for site in previous:
        if site["prefabKey"]!="watchtower":
            site["baked"] = True
    EXPANSION.write_text(json.dumps(expansion,indent=2)+"\n")
    sockets = write_loot(homes+baked_expansion+scattered,geometry)
    for data in homes:
        data.pop("bounds")
        data.pop("transform")
    report = {"prefabs":assets,"native_before":len(homes),"previous_playtime_houses":len(baked_expansion),
              "landmarks_preserved":landmarks,"compounds_reoriented":sorted(rotated),
              "added_by_terrain":dict(Counter(h["category"] for h in scattered)),
              "total_houses":len(homes)+len(baked_expansion)+len(scattered),"total_sockets":sockets,
              "houses":homes+baked_expansion+scattered}
    AUDIT.write_text(json.dumps(report,indent=2)+"\n")


def check():
    report = json.loads(AUDIT.read_text())
    loot = json.loads(DOCUMENT.read_text())["houseSockets"]
    groups = Counter(row["origin"] for row in loot)
    main = BOOTSTRAP.read_text()
    scene_texts = {p:Path(p).read_text() for p in {h["chunk"] for h in report["houses"] if "chunk" in h}}
    for house in report["houses"]:
        name,preset = house["name"],house["prefab"]
        assert 2<=groups[name]<=6
        source = asset_guid(ASSETS/(preset+".prefab"))
        text = scene_texts[house["chunk"]] if "chunk" in house else main
        match, = [doc for doc in text.split("--- !u!1001 &")[1:] if f"\n      value: {name}\n" in doc]
        assert f"m_SourcePrefab: {{fileID: 100100000, guid: {source}, type: 3}}" in match
        if "chunk" in house:
            assert f"m_Name: __AarisPersistentCover_{name}\n" in main
            assert f"m_Name: __AarisFarSilhouette_{name}\n" in main
    assert len(loot)==report["total_sockets"]
    assert len(groups)==report["total_houses"]
    assert 18<=sum(report["added_by_terrain"].values())<=25
    assert 8 <= len(report["compounds_reoriented"]) <= 9
    assert 0 < len(report["landmarks_preserved"]) < 6
    print(f"Saved house prefab links: {len(groups)}. Loot sockets: {len(loot)}. Terrain additions: {report['added_by_terrain']}.")


if __name__=="__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check",action="store_true",help="Audit the already authored scenes and loot manifest")
    if not parser.parse_args().check:
        bake()
    check()
