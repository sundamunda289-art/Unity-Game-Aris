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


# These civic sources have verified LOD1 -> LOD2 triangle reductions of
# TownHall 17925->4431, Harbour 23527->2714, ParcelDepot 6632->2916.
# Only their LOD1 switch is raised (7% to 9.5% screen height).
# Preserve exact historical hashes by reversing this specific scalar in memory.
CIVIC_DISTANCE_LOD_SCENE_NAMES = frozenset((
    'ClockTowerTown_TownHall_01',
    'EasternPort_Harbour_01',
    'FreightDistrict_ParcelDepot_01',
))
CIVIC_DISTANCE_LOD_SOURCE_PREFABS = frozenset(
    f'Assets/AarisWorld/Models/{name}/{name}.prefab'
    for name in ('TownHall', 'Harbour', 'ParcelDepot')
)

# Strict scene LOD correction: reverse 79 residential/rock pairs, 3 compounds
# and 3 civic distance-LOD scalars.
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
    counts = {'home': 0, 'rock': 0, 'heavy': 0, 'civic': 0, 'untouched': 0}
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
        civic = name in CIVIC_DISTANCE_LOD_SCENE_NAMES
        if not (home or rock or heavy or civic):
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
        if civic:
            if thresholds != ['0.22', '0.095', '0.0025'] or chunk.count('  - screenRelativeHeight: 0.095\n') != 1:
                return None
            result.append(chunk.replace('  - screenRelativeHeight: 0.095\n',
                                        '  - screenRelativeHeight: 0.07\n', 1))
            counts['civic'] += 1
            continue
        if thresholds != ['0.28', '0.095', '0.0025']:
            return None
        if chunk.count('  - screenRelativeHeight: 0.28\n') != 1 or chunk.count('  - screenRelativeHeight: 0.095\n') != 1:
            return None
        original = chunk.replace('  - screenRelativeHeight: 0.28\n', '  - screenRelativeHeight: 0.22\n', 1)
        original = original.replace('  - screenRelativeHeight: 0.095\n', '  - screenRelativeHeight: 0.07\n', 1)
        counts['home' if home else 'rock'] += 1
        result.append(original)
    if counts != {'home': 47, 'rock': 32, 'heavy': 3, 'civic': 3, 'untouched': 16}:
        return None
    return ''.join(result).encode('utf-8')


def preserved_or_reviewed_lod_only(root,path,expected_sha):
    source=(Path(root)/path).read_bytes()
    if hashlib.sha256(source).hexdigest()==expected_sha:return True
    if path == SCENE_MOBILE_LOD_PATH:
        restored = restore_scene_mobile_lod(source)
        return restored is not None and hashlib.sha256(restored).hexdigest() == expected_sha
    if path in HEAVY_COMPOUND_SOURCE_PREFABS:
        marker = b'  - screenRelativeHeight: 0.11\n'
        if source.count(marker) != 1:return False
        original = source.replace(marker, b'  - screenRelativeHeight: 0.07\n', 1)
        return hashlib.sha256(original).hexdigest() == expected_sha
    if path in CIVIC_DISTANCE_LOD_SOURCE_PREFABS:
        marker = b'  - screenRelativeHeight: 0.095\n'
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
    for path,sha in contract['tree_source_files'].items():
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
