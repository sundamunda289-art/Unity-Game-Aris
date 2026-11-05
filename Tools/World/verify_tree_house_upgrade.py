"""Verify the authored source migration. Sampled doorway rays are not Unity physics."""
from __future__ import annotations
import hashlib
import json
from pathlib import Path
import numpy as np
from mesh_faces import arrays,summary
from unity_data import World,digest,documents

CONTRACT='Tools/World/tree-house-20261103.postconditions.json'


def mesh_data(path):
    return next(d['data']for d in documents(path)[1]if d['kind']=='Mesh')


def ray_hits(positions,groups,start,end):
    triangles=positions[np.concatenate(groups)].astype(float)
    direction=np.asarray(end)-start;origin=np.asarray(start)
    e1=triangles[:,1]-triangles[:,0];e2=triangles[:,2]-triangles[:,0]
    q=np.cross(direction,e2);det=np.sum(e1*q,axis=1);valid=np.abs(det)>1e-10
    inv=np.divide(1,det,out=np.zeros_like(det),where=valid)
    s=origin-triangles[:,0];u=np.sum(s*q,axis=1)*inv;r=np.cross(s,e1)
    v=np.sum(direction*r,axis=1)*inv;t=np.sum(e2*r,axis=1)*inv
    return int(np.count_nonzero(valid&(u>=-1e-8)&(v>=-1e-8)&(u+v<=1+1e-8)&(t>1e-5)&(t<1-1e-5)))


def accepts_historical_transition(root,path,original_sha):
    """Recognize only this explicitly authored hash transition, preserving the old report."""
    root=Path(root);contract=root/CONTRACT
    if not contract.exists():return False
    row=json.loads(contract.read_text())['meshes'].get(str(path))
    if not row or row['before_sha256']!=original_sha or digest(root/path)!=row['after_sha256']:return False
    return summary(mesh_data(root/path))['vertex_buffer_sha256']==row['vertex_buffer_sha256']


# PR #18's historical protection contract records the exact saved house prefab
# bytes before PR #19's two reviewed LOD-threshold-only changes. Preserve that
# stronger byte-for-byte guarantee by reversing exactly these two lines in memory
# and matching the original protected SHA256. Any other change fails closed.
RESIDENTIAL_MOBILE_LOD_PREFABS=frozenset(
    f'Assets/AarisWorld/Models/{name}/{name}.prefab' for name in (
        'a_frame_cabin','bungalow_house','detached_two_storey_house',
        'garage_apartment','pitched_roof_cottage','row_three_townhouses',
        'two_bedroom_garage_house',
    )
)



# Heavy city-scale compounds use earlier LOD2 for screen heights 7%-11%.
# Exact named scene groups and reusable source prefabs are the only exceptions.
# The historical source hash is still checked after reversing this scalar.
HEAVY_COMPOUND_SCENE_NAMES = frozenset((
    'SouthernAirfield_Airfield_01',
    'CanalMarket_CanalTown_01',
    'OldVillage_HanokVillage_01',
))
HEAVY_COMPOUND_SOURCE_PREFABS = frozenset(
    f'Assets/AarisWorld/Models/{name}/{name}.prefab'
    for name in ('Airfield', 'CanalTown', 'HanokVillage')
)


# Strict scene LOD correction: reverse 79 residential/rock pairs and 3 compound scalars.
SCENE_MOBILE_LOD_PATH = 'Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity'
MOBILE_LOD_HOME_TYPES = frozenset((
    'a_frame_cabin', 'bungalow_house', 'detached_two_storey_house',
    'garage_apartment', 'pitched_roof_cottage', 'row_three_townhouses',
    'two_bedroom_garage_house',
))

def restore_scene_mobile_lod(source):
    import re
    try:
        chunks = re.split(r'(?=^--- !u!)', source.decode('utf-8'), flags=re.M)
    except UnicodeDecodeError:
        return None
    names = {}
    for chunk in chunks:
        match = re.match(r'--- !u!1 &(\d+)', chunk)
        if match:
            name = re.search(r'^  m_Name: ([^\r\n]+)', chunk, re.M)
            if name:
                names[match.group(1)] = name.group(1)
    counts = {'home': 0, 'rock': 0, 'heavy': 0, 'untouched': 0}
    result = []
    for chunk in chunks:
        if not re.match(r'--- !u!205 &', chunk):
            result.append(chunk)
            continue
        owner = re.search(r'm_GameObject: \{fileID: (\d+)', chunk)
        name = names.get(owner.group(1), '') if owner else ''
        home = any(re.search(r'_' + re.escape(t) + r'_\d+$', name) for t in MOBILE_LOD_HOME_TYPES)
        rock = re.fullmatch(r'RockCover_\d+', name) is not None
        heavy = name in HEAVY_COMPOUND_SCENE_NAMES
        if not (home or rock or heavy):
            counts['untouched'] += 1
            result.append(chunk)
            continue
        thresholds = re.findall(r'^  - screenRelativeHeight: (\S+)', chunk, flags=re.M)
        if heavy:
            if thresholds != ['0.22', '0.11', '0.0025'] or chunk.count('  - screenRelativeHeight: 0.11\n') != 1:
                return None
            result.append(chunk.replace('  - screenRelativeHeight: 0.11\n',
                                        '  - screenRelativeHeight: 0.07\n', 1))
            counts['heavy'] += 1
            continue
        if thresholds != ['0.28', '0.095', '0.0025']:
            return None
        if chunk.count('  - screenRelativeHeight: 0.28\n') != 1 or chunk.count('  - screenRelativeHeight: 0.095\n') != 1:
            return None
        original = chunk.replace('  - screenRelativeHeight: 0.28\n', '  - screenRelativeHeight: 0.22\n', 1)
        original = original.replace('  - screenRelativeHeight: 0.095\n', '  - screenRelativeHeight: 0.07\n', 1)
        counts['home' if home else 'rock'] += 1
        result.append(original)
    if counts != {'home': 47, 'rock': 32, 'heavy': 3, 'untouched': 19}:
        return None
    return ''.join(result).encode('utf-8')


# Exact, reviewed 2026-11-05 mobile LOD2-only shadow omission.
# Revert this ONE property before the historical protected byte hash check.
REVIEWED_FAR_SHADOW_PREFABS=frozenset(
    f'Assets/AarisWorld/Models/{n}/{n}.prefab' for n in (
        'Airfield','CanalTown','HanokVillage','Harbour','ParcelDepot',
        'TownHall','a_frame_cabin','bungalow_house',
        'detached_two_storey_house','garage_apartment','lighthouse',
        'observatory_dome','pitched_roof_cottage',
        'row_three_townhouses','two_bedroom_garage_house',
        'warehouse_loading_dock','watchtower')
) | frozenset(
    f'Assets/AarisWorld/Props/{n}/{n}.prefab'
    for n in ('ClimbableTower','RockCover','StaticWreck')
)


def restore_reviewed_far_shadow(source, path):
    """Return original source bytes or None on ANY unreviewed renderer state."""
    import re
    if path not in REVIEWED_FAR_SHADOW_PREFABS:
        return source
    blocks=re.split(rb'(?=^--- !u!)',source,flags=re.M)
    groups=[x for x in blocks if b'\nLODGroup:\n' in x]
    if len(groups)!=1:return None
    ids=re.findall(rb'- renderer: \{fileID: (\d+)\}',groups[0])
    if len(ids)!=3 or len(set(ids))!=3:return None
    for index,ident in enumerate(ids):
        header=b'--- !u!23 &'+ident+b'\nMeshRenderer:\n'
        locations=[j for j,b in enumerate(blocks) if b.startswith(header)]
        if len(locations)!=1:return None
        j=locations[0]
        matches=re.findall(rb'^  m_CastShadows: (\d+)\n',blocks[j],flags=re.M)
        if matches!=([b'1'] if index<2 else [b'0']):return None
        if index==2:
            blocks[j]=blocks[j].replace(b'  m_CastShadows: 0\n',b'  m_CastShadows: 1\n',1)
    return b''.join(blocks)


# Exact, owner-verified PR #64 tree-only native TerrainData updates.
# Preserve the old v1 house contract and source mesh checks. Approve ONLY
# these four LFS blobs, never arbitrary terrain data or altered .meta files.
APPROVED_20261105_NATIVE_TERRAINS={
    'Terrain_1_1_4bf22c8f-eb55-436d-84e3-dedfa85665b1.asset':
      ('4798dfe0edf6cec43be1417e519271fbc4b98b99de553a981195e13c321714ae',
       '896b2d29b2d5b71afffe1f64a17863c8fa99cd06104c833b679ca117dd4d4609',53668572),
    'Terrain_1_2_1694bb0f-cffe-402c-b6f9-cf47692fbb78.asset':
      ('9053935547655ccf39744e0b5ab475514598734bdffcf19f4f1760a452b559e4',
       'b92e9099868cabd8040db632d0cfac3c38075b2e292519b0edd8629723edecf8',54074780),
    'Terrain_2_1_82fc4922-5564-4a18-858e-787f6d901556.asset':
      ('9aa00e637163f52f559683eb837e12799f39f499bcd16535d60838d49f8d5f65',
       'd26aa11e1e6bb8edc65ca52c8c3210d477711b091e86e7ead42e75407dcc29a3',53509804),
    'Terrain_2_2_b6e03cf0-de91-4cae-b6af-0128d856ae43.asset':
      ('113c4b11ab5f7e6cba20f60a6b8bbc061d0f72d335827d48c98fff855a7faf07',
       'cfd96176947a1e75da285458ad6198ebf56a80195aa2138a5f202f5dc7f814e6',53568876),
}


def reviewed_native_terrain_pr64(source, path, expected_sha):
    import re
    prefix='Assets/TerrainDemoScene_URP/Terrain/Data/'
    if not path.startswith(prefix):return False
    entry=APPROVED_20261105_NATIVE_TERRAINS.get(path[len(prefix):])
    if entry is None or expected_sha!=entry[0]:return False
    new,size=entry[1:]
    if source.startswith(b'version https://git-lfs.github.com/spec/v1\n'):
        matched=re.fullmatch(
            rb'version https://git-lfs.github.com/spec/v1\n'
            rb'oid sha256:([a-f0-9]{64})\nsize ([0-9]+)\n?',source)
        return (matched is not None and matched[1].decode()==new and
                int(matched[2])==size)
    return len(source)==size and hashlib.sha256(source).hexdigest()==new


def restore_reviewed_scene_far_shadow(source):
    """Reverse exactly 101 LOD2-only scene shadow edits; preserve all other bytes.

    Scene entities are expanded GameObjects, not prefab instances. Do not
    assume modifying the source prefab changes the saved scene MeshRenderers.
    The historical protected SHA-256 must match after this reversal AND the
    previous reviewed screen-height LOD normalization.
    """
    import re
    blocks=re.split(rb'(?=^--- !u!)',source,flags=re.M)
    mesh_by_id={}
    for index,block in enumerate(blocks):
        match=re.match(rb'--- !u!(\d+) &(\d+)',block)
        if match and match[1]==b'23':
            if match[2] in mesh_by_id:return None
            mesh_by_id[match[2]]=index
    lod_groups=0
    reverted=0
    seen=set()
    for group in blocks:
        if not re.match(rb'--- !u!205 &',group):
            continue
        lod_groups+=1
        ids=re.findall(rb'- renderer: \{fileID: (\d+)\}',group)
        if len(ids)!=3 or len(set(ids))!=3:return None
        for level,ident in enumerate(ids):
            index=mesh_by_id.get(ident)
            if index is None:return None
            block=blocks[index]
            head=b'MeshRenderer:\n'
            if head not in block:return None
            bits=re.findall(rb'^  m_CastShadows: (\d+)\n',block,flags=re.M)
            if bits!=([b'1'] if level<2 else [b'0']):return None
            if level==2:
                if ident in seen:return None
                seen.add(ident)
                marker=b'  m_CastShadows: 0\n'
                if block.count(marker)!=1:return None
                blocks[index]=block.replace(marker,b'  m_CastShadows: 1\n',1)
                reverted+=1
    if lod_groups!=101 or reverted!=101:return None
    return b''.join(blocks)


# House enlargement review: EXACTLY 47 existing residential root Transform
# scale triples are the ONLY additional scene bytes accepted. Reverse them
# before the historical protected scene hash check. Unknown/partial changes fail.
# This does not certify meshes, interiors or a Unity/Android render.
REVIEWED_RESIDENTIAL_SCALES = {
    "a_frame_cabin": ("1.12", "1.344", 6),
    "bungalow_house": ("1.12", "1.456", 5),
    "pitched_roof_cottage": ("1.12", "1.3216", 6),
    "detached_two_storey_house": ("1.12", "1.2544", 6),
    "garage_apartment": ("1.12", "1.2768", 11),
    "two_bedroom_garage_house": ("1.105", "1.31495", 7),
    "row_three_townhouses": ("1.12", "1.232", 6),
}


def restore_reviewed_residential_scales(source):
    """Undo only the reviewed full-set 47 root XYZ scales, or accept old baseline.

    Every owner identity, field spelling, component count and unrelated scene
    byte remains subject to the protected historical source SHA check.
    """
    import re
    try:
        source.decode("utf-8")
    except UnicodeDecodeError:
        return None
    blocks = re.split(rb"(?=^--- !u!)", source, flags=re.M)
    owners = {}
    for block in blocks:
        m = re.match(rb"--- !u!1 &(\d+)\nGameObject:\n", block)
        if m:
            name = re.search(rb"^  m_Name: ([^\r\n]+)", block, flags=re.M)
            if name:
                if m[1] in owners:
                    return None
                owners[m[1]] = name[1].decode("utf-8")
    counts = {name: [0, 0] for name in REVIEWED_RESIDENTIAL_SCALES}
    output = []
    for block in blocks:
        if not re.match(rb"--- !u!4 &\d+\nTransform:\n", block):
            output.append(block)
            continue
        match = re.search(rb"^  m_GameObject: \{fileID: (\d+)\}$", block, flags=re.M)
        name = owners.get(match[1], "") if match else ""
        model = next(
            (kind for kind in REVIEWED_RESIDENTIAL_SCALES
             if re.search(r"_" + re.escape(kind) + r"_\d+$", name)),
            None,
        )
        if model is None:
            output.append(block)
            continue
        old, new, _ = REVIEWED_RESIDENTIAL_SCALES[model]
        def scalar_line(value):
            s = value.encode("ascii")
            return b"  m_LocalScale: {x: " + s + b", y: " + s + b", z: " + s + b"}"
        old_line, new_line = scalar_line(old), scalar_line(new)
        if block.count(new_line) == 1 and block.count(old_line) == 0:
            block = block.replace(new_line, old_line, 1)
            counts[model][1] += 1
        elif block.count(old_line) == 1 and block.count(new_line) == 0:
            counts[model][0] += 1
        else:
            return None
        output.append(block)
    for model, (old, new, expected_count) in REVIEWED_RESIDENTIAL_SCALES.items():
        if sum(counts[model]) != expected_count:
            return None
    # Do not permit mixed old/new state, even across different model types.
    total_old = sum(v[0] for v in counts.values())
    total_new = sum(v[1] for v in counts.values())
    if (total_old, total_new) not in ((47, 0), (0, 47)):
        return None
    return b"".join(output)


# A Foundation mesh stores absolute world-space rectangle vertices. Its
# GameObject stays centered on the old foundation with a compensated scale
# and translation; no native LFS geometry, terrain or .meta is rewritten.
RESIDENTIAL_FOUNDATION_PLAN = "Tools/World/reviewed-residential-foundations-20261105.json"


def restore_reviewed_residential_foundations(root, source):
    """Reverse exactly the 47 named foundation transforms or reject drift.

    The manifest's authored numbers are also checked against the preserved
    world layout and the approved per-model house multipliers. The
    historical exact-byte scene SHA remains the final immutable guard.
    """
    import re
    root = Path(root)
    try:
        plan = json.loads((root / RESIDENTIAL_FOUNDATION_PLAN).read_text())
        layout = json.loads((root / "Docs/MAP_LAYOUT.json").read_text())
        source.decode("utf-8")
    except (OSError, ValueError, UnicodeError):
        return None
    if plan.get("schema") != 1 or len(plan.get("changes", [])) != 47:
        return None
    houses = {b["name"]: b for b in layout["buildings"]
              if b["model"] in REVIEWED_RESIDENTIAL_SCALES}
    names = set(houses)
    changes = {row.get("name"): row for row in plan["changes"]
               if isinstance(row, dict)}
    if len(houses) != 47 or set(changes) != names:
        return None
    # Prevent a changed manifest from approving unreviewed house geometry.
    for name, b in houses.items():
        row = changes[name]
        if row.get("model") != b["model"]:
            return None
        old, new, _ = REVIEWED_RESIDENTIAL_SCALES[b["model"]]
        factor = float(new) / float(old)
        x, _, z = b["position_m"]
        xmin, zmin, xmax, zmax = b["aabb_xz_m"]
        width, depth = xmax-xmin, zmax-zmin
        sx = (width*factor+2)/(width+2)
        sz = (depth*factor+2)/(depth+2)
        expected = (sx, sz, x*(1-sx), z*(1-sz))
        keys = ("scaleX", "scaleZ", "positionX", "positionZ")
        try:
            actual = tuple(float(row[key]) for key in keys)
            before = row["footprintBefore"]
            after = row["footprintAfter"]
        except (TypeError, ValueError, KeyError):
            return None
        if (any(not isinstance(row[key], str) for key in keys)
            or any(abs(a-e)>1e-7 for a,e in zip(actual,expected))
            or any(abs(a-e)>1e-5 for a,e in
                   zip(before,[width,depth]))
            or any(abs(a-e)>1e-5 for a,e in
                   zip(after,[width*factor,depth*factor]))):
            return None
    blocks = re.split(rb"(?=^--- !u!)", source, flags=re.M)
    owners = {}
    for block in blocks:
        match = re.match(rb"--- !u!1 &(\d+)\nGameObject:\n", block)
        if match:
            name = re.search(rb"^  m_Name: ([^\r\n]+)",block,flags=re.M)
            if name:
                if match[1] in owners:
                    return None
                owners[match[1]] = name[1].decode("utf-8")
    states = {}
    roots = {}
    out = []
    oldS = b"  m_LocalScale: {x: 1.0, y: 1.0, z: 1.0}"
    oldP = b"  m_LocalPosition: {x: 0.0, y: 0.0, z: 0.0}"
    for block in blocks:
        if not re.match(rb"--- !u!4 &\d+\nTransform:\n", block):
            out.append(block)
            continue
        match = re.search(rb"^  m_GameObject: \{fileID: (\d+)\}$",block,flags=re.M)
        name = owners.get(match[1],"") if match else ""
        if name in names:
            old, new, _ = REVIEWED_RESIDENTIAL_SCALES[houses[name]["model"]]
            o = ("  m_LocalScale: {x: "+old+", y: "+old+", z: "+old+"}").encode()
            n = ("  m_LocalScale: {x: "+new+", y: "+new+", z: "+new+"}").encode()
            if name in roots:
                return None
            if block.count(n)==1 and block.count(o)==0:
                roots[name]=True
            elif block.count(o)==1 and block.count(n)==0:
                roots[name]=False
            else:
                return None
        if not name.endswith("_Foundation") or name[:-11] not in names:
            out.append(block)
            continue
        home = name[:-11]
        if home in states:
            return None
        row = changes[home]
        sx, sz, px, pz = (row[k] for k in
                           ("scaleX","scaleZ","positionX","positionZ"))
        newS = ("  m_LocalScale: {x: "+sx+", y: 1.0, z: "+sz+"}").encode()
        newP = ("  m_LocalPosition: {x: "+px+", y: 0.0, z: "+pz+"}").encode()
        if (block.count(newS)==1 and block.count(newP)==1
            and block.count(oldS)==0 and block.count(oldP)==0):
            states[home] = True
            block = block.replace(newS,oldS,1).replace(newP,oldP,1)
        elif (block.count(oldS)==1 and block.count(oldP)==1
              and block.count(newS)==0 and block.count(newP)==0):
            states[home] = False
        else:
            return None
        out.append(block)
    if set(states) != names or set(roots) != names:
        return None
    # An enlarged house may not silently retain a smaller foundation.
    if any(states[name] != roots[name] for name in names):
        return None
    if sum(states.values()) not in (0, 47):
        return None
    return b"".join(out)


# Keep the historical scene SHA immutable while allowing exactly 17 paired
# changes to vehicle spawn marker + absolute-coordinate asphalt apron. New road
# exits, genuine vehicle physics or arbitrary terrain edits are NOT approved.
REVIEWED_VEHICLE_SPAWN_MANIFEST = "Tools/World/reviewed-residential-vehicle-spawns-20261105.json"


def restore_reviewed_residential_vehicle_spawns(root, source):
    """Return exact old scene bytes after reversing 17 markers + 17 aprons.

    Reject unreviewed marker positions, changed pavement translations, partial
    pairs, mutated manifest coordinates, wrong owners and duplicate names.
    The historical SHA verifies every other byte after all reviewed reversals.
    """
    import re
    from smart_residential_clearance import simulation, ClearanceError
    root=Path(root)
    try:
        plan=json.loads((root/REVIEWED_VEHICLE_SPAWN_MANIFEST).read_text())
        layout=json.loads((root/"Docs/MAP_LAYOUT.json").read_text())
        expected=simulation(layout)
        source.decode("utf-8")
    except (OSError,ValueError,UnicodeError,KeyError,TypeError,ClearanceError):
        return None
    rows=plan.get("rows",[])
    if (plan.get("schema")!=1 or plan.get("initial_marker_count")!=45
        or plan.get("unchanged_marker_count")!=28
        or plan.get("changed_marker_count")!=17
        or plan.get("changed_apron_count")!=17
        or not isinstance(rows,list) or len(rows)!=17):
        return None
    data={row.get("spawn_name"):row for row in rows if isinstance(row,dict)}
    derived={row["spawn_name"]:row for row in expected["relocated"]}
    if (len(data)!=17 or set(data)!=set(derived)
        or expected["total_spawns"]!=45 or expected["total_buildings"]!=59):
        return None
    def parsed_position(line):
        if not isinstance(line,str):
            return None
        m=re.fullmatch(
            r"  m_LocalPosition: \{x: (-?[\d.eE+]+), y: (-?[\d.eE+]+), z: (-?[\d.eE+]+)\}",
            line)
        return tuple(map(float,m.groups())) if m else None
    for name,item in derived.items():
        entry=data[name]
        if (entry.get("house_name")!=item["house_name"]
            or entry.get("model")!=item["model"]
            or entry.get("road_exit_xz_m")!=item["road_exit_xz_m"]):
            return None
        for k,expected_values in (
            ("old_spawn_position_m",item["original_position_m"]),
            ("new_spawn_position_m",item["new_position_m"])
        ):
            actual=entry.get(k)
            if (not isinstance(actual,list) or len(actual)!=3
                or any(type(x) not in (int,float) for x in actual)
                or any(abs(a-b)>1e-7 for a,b in zip(actual,expected_values))):
                return None
        if (abs(entry.get("shift_z_m",0)-item["delta_xz_m"][1])>1e-7
            or abs(entry.get("before_clearance_m",0)-item["before_clearance_m"])>1e-6
            or abs(entry.get("after_clearance_m",0)-item["after_clearance_m"])>1e-6):
            return None
        for typ in ("marker","apron"):
            part=entry.get(typ)
            if not isinstance(part,dict) or set(part)!={"original_line","updated_line"}:
                return None
            old=parsed_position(part["original_line"])
            new=parsed_position(part["updated_line"])
            if old is None or new is None or old==new:
                return None
            if typ=="marker":
                expected_old=item["original_position_m"]
                expected_new=item["new_position_m"]
            else:
                expected_old=(0,0,0)
                expected_new=(0,0,item["delta_xz_m"][1])
            if any(abs(a-b)>1e-7 for a,b in zip(old,expected_old)):
                return None
            if any(abs(a-b)>1e-7 for a,b in zip(new,expected_new)):
                return None
    blocks=re.split(rb"(?=^--- !u!)",source,flags=re.M)
    owners={}
    all_related=set()
    for block in blocks:
        m=re.match(rb"--- !u!1 &(\d+)\nGameObject:\n",block)
        if m:
            n=re.search(rb"^  m_Name: ([^\r\n]+)",block,flags=re.M)
            if not n:continue
            name=n[1].decode("utf-8")
            if name in data or (name.endswith("_Apron") and name[:-6] in data):
                if name in all_related:
                    return None
                all_related.add(name)
                owners[m[1]]=name
    if len(all_related)!=34:
        return None
    seen=set()
    restored=[]
    for block in blocks:
        if not re.match(rb"--- !u!4 &\d+\nTransform:\n",block):
            restored.append(block)
            continue
        m=re.search(rb"^  m_GameObject: \{fileID: (\d+)\}$",block,flags=re.M)
        name=owners.get(m[1],"") if m else ""
        if not name:
            restored.append(block)
            continue
        if name in seen:
            return None
        seen.add(name)
        typ="apron" if name.endswith("_Apron") else "marker"
        row=data[name[:-6] if typ=="apron" else name][typ]
        old=row["original_line"].encode()
        new=row["updated_line"].encode()
        if block.count(new)!=1 or block.count(old)!=0:
            return None
        restored.append(block.replace(new,old,1))
    if seen!=all_related:
        return None
    return b"".join(restored)


def preserved_or_reviewed_lod_only(root,path,expected_sha):
    source=(Path(root)/path).read_bytes()
    if hashlib.sha256(source).hexdigest()==expected_sha:return True
    if reviewed_native_terrain_pr64(source,path,expected_sha):return True
    if path in REVIEWED_FAR_SHADOW_PREFABS:
        source=restore_reviewed_far_shadow(source,path)
        if source is None:return False
        if hashlib.sha256(source).hexdigest()==expected_sha:return True
    if path == SCENE_MOBILE_LOD_PATH:
        vehicle_source=restore_reviewed_residential_vehicle_spawns(root,source)
        if vehicle_source is None:return False
        foundation_source=restore_reviewed_residential_foundations(root,vehicle_source)
        if foundation_source is None:return False
        reviewed_scales=restore_reviewed_residential_scales(foundation_source)
        if reviewed_scales is None:return False
        original_shadows=restore_reviewed_scene_far_shadow(reviewed_scales)
        if original_shadows is None:return False
        restored=restore_scene_mobile_lod(original_shadows)
        return restored is not None and hashlib.sha256(restored).hexdigest() == expected_sha
    if path in HEAVY_COMPOUND_SOURCE_PREFABS:
        marker = b'  - screenRelativeHeight: 0.11\n'
        if source.count(marker) != 1:return False
        original = source.replace(marker, b'  - screenRelativeHeight: 0.07\n', 1)
        return hashlib.sha256(original).hexdigest() == expected_sha
    if path not in RESIDENTIAL_MOBILE_LOD_PREFABS:return False
    for new,old in (
        (b'  - screenRelativeHeight: 0.28\n',b'  - screenRelativeHeight: 0.22\n'),
        (b'  - screenRelativeHeight: 0.095\n',b'  - screenRelativeHeight: 0.07\n'),
    ):
        if source.count(new)!=1:return False
        source=source.replace(new,old,1)
    return hashlib.sha256(source).hexdigest()==expected_sha


def verify(root):
    root=Path(root).resolve();contract=json.loads((root/CONTRACT).read_text());rays=0
    for path,row in contract['meshes'].items():
        assert digest(root/path)==row['after_sha256'],path+' differs from inspected migration'
        assert digest(root/(path+'.meta'))==row['meta_sha256'],path+' identity changed'
        facts=summary(mesh_data(root/path))
        assert facts['vertex_buffer_sha256']==row['vertex_buffer_sha256'],path+' vertex attributes changed'
        assert facts['triangles_by_submesh']==row['triangles_by_submesh'],path+' material slots changed'
    from lod_profiles import preserved_tree_lod_only
    reviewed_v2 = set()
    v2 = root / 'Tools/World/tree-realism-v2-reviewed.json'
    if v2.exists():
        from verify_tree_realism_v2 import verify as verify_v2
        validated = verify_v2(root)
        assert validated['approved_files'] == 12
        from verify_tree_realism_v2 import authorized_tree_blobs
        reviewed_v2 = set(authorized_tree_blobs(root))
    for path,sha in contract['tree_source_files'].items():
        if path not in reviewed_v2:
            assert preserved_tree_lod_only(root,path,sha),path+' tree source changed outside the reviewed LOD scalars'
    for path,sha in contract['preserved_files'].items():
        assert preserved_or_reviewed_lod_only(root,path,sha),path+' protected source changed outside reviewed LOD fields'
    for probe in contract['doorway_samples']:
        folder=root/'Assets/AarisWorld/Models'/probe['model']
        for path in sorted(folder.glob('Geometry*.asset')):
            positions,groups,_=arrays(mesh_data(path))
            center=np.array(probe['center']);normal=probe['normal_axis'];side=2 if normal==0 else 0
            for offset in (-.28,0,.28):
                for dy in (.04,.28,.89,1.5,1.74):
                    start=center.copy();start[side]+=offset;start[1]=probe['foot_y']+dy;end=start.copy()
                    start[normal]-=.55;end[normal]+=.55
                    assert ray_hits(positions,groups,start,end)==0,(probe['model'],path.name,probe['center'],offset,dy,'sampled doorway blocked')
                    rays+=1
    world=World(root);models={Path(p).parent.name for p in contract['meshes']};bindings=0
    for model in sorted(models):
        path='Assets/AarisWorld/Models/'+model+'/Geometry.asset'
        guid=next(g for g,p in world.guid_paths.items()if p==path)
        users=[]
        for go in world.gos:
            components=world.components(go)
            if any(c['kind']=='MeshFilter'and c['data'].get('m_Mesh',{}).get('guid')==guid for c in components):
                users.append(go)
                assert any(c['kind']=='MeshCollider'and c['data'].get('m_Mesh',{}).get('guid')==guid and not c['data'].get('m_Convex')for c in components)
        assert users,model+' has no saved scene users';bindings+=len(users)
    return {'source_meshes_checked':len(contract['meshes']),'residential_instances_using_opened_meshes':bindings,
            'tree_source_files_checked':len(contract['tree_source_files']),'doorway_samples':len(contract['doorway_samples']),
            'clear_sampled_rays_across_all_lods':rays,'vertex_attributes_and_material_slots_preserved':True,
            'unity_import_render_or_physics_tested':False,
            'limits':'Discrete source-triangle rays, not continuous capsule sweeps, complete interior navigation, Unity cooking, rendering or device tests.'}


if __name__=='__main__':print(json.dumps(verify(Path(__file__).resolve().parents[2])))
