"""Validate saved ground gameplay, forest clearances and references; not a playtest."""
import ast,hashlib,json,re,sys
from pathlib import Path
import numpy as np
import yaml
from tree_sitter import Language,Parser
import tree_sitter_c_sharp
ROOT=Path.cwd()
sys.path.insert(0,str(ROOT/'Tools/MapBuild'))
from ground_review import documents,segment_distance

def refs(value):
    if isinstance(value,dict):
        if 'fileID'in value:yield value
        for v in value.values():yield from refs(v)
    elif isinstance(value,list):
        for v in value:yield from refs(v)

def check(require_native=False):
    objects=documents(ROOT/'Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity');byid={i:(c,n,d) for c,i,n,d in objects}
    assert len(byid)==len(objects),'Duplicate saved object ID'
    for c,i,n,d in objects:
        for r in refs(d):
            if r['fileID'] and not r.get('guid'):assert r['fileID'] in byid,(n,i,'Missing local reference',r)
    players=[(i,d) for c,i,n,d in objects if n=='GameObject' and d['m_Name']=='AarisPlayer'];assert len(players)==1
    player_go,player=players[0];components=[byid[r['component']['fileID']] for r in player['m_Component']]
    tags=documents(ROOT/'ProjectSettings/TagManager.asset')[0][3]
    assert tags['layers'][8]=='Player'
    assert all(d['m_Layer']==8 for c,i,n,d in objects if n=='GameObject' and (d['m_Name']=='AarisPlayer' or d['m_Name'].startswith('AarisPlayer_')))
    prefab=documents(ROOT/'Assets/AarisWorld/Player/AarisPlayer.prefab')
    assert all(d['m_Layer']==8 for c,i,n,d in prefab if n=='GameObject')
    controller=next(d for c,n,d in components if c==143);assert controller['m_Height']==1.78 and controller['m_Radius']>.2
    script=ROOT/'Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs';script_guid=yaml.safe_load(Path(str(script)+'.meta').read_text())['guid']
    logic=next(d for c,n,d in components if c==114);assert logic['m_Script']['guid']==script_guid
    camera_id=logic['followCamera']['fileID'];assert byid[camera_id][0]==20
    assert sum(c==20 for c,i,n,d in objects)==sum(c==81 for c,i,n,d in objects)==1
    camera_go=byid[camera_id][2]['m_GameObject']['fileID']
    assert not any(c==114 and d.get('m_GameObject',{}).get('fileID')==camera_go for c,i,n,d in objects),'Flying camera controller remains'
    assert 3<logic['followDistance']<6 and 3<logic['walkSpeed']<logic['runSpeed']<8
    for limb in ['leftArm','rightArm','leftLeg','rightLeg']:assert byid[logic[limb]['fileID']][0]==4
    assert all(d['m_StaticEditorFlags']==0 for c,i,n,d in objects if n=='GameObject' and d['m_Name'].startswith('AarisPlayer'))
    transforms={i:d for c,i,n,d in objects if n=='Transform'}
    for i,t in transforms.items():
        for child in t['m_Children']:assert transforms[child['fileID']]['m_Father']['fileID']==i,'Transform parent/child mismatch'
    scene_roots=next(d['m_Roots'] for c,i,n,d in objects if n=='SceneRoots')
    assert {r['fileID'] for r in scene_roots}=={i for i,t in transforms.items() if t['m_Father']['fileID']==0}
    assert sum(c==218 for c,i,n,d in objects)==4
    assert sum(c==1 and bool(re.fullmatch('VehicleSpawnPoint_[0-9]+',d['m_Name'])) for c,i,n,d in objects)==45
    layout=json.loads((ROOT/'Docs/MAP_LAYOUT.json').read_text())
    building_names={b['name'] for b in layout['buildings']}
    names={i:d['m_Name'] for c,i,n,d in objects if n=='GameObject'}
    lods=[d for c,i,n,d in objects if n=='LODGroup' and names[d['m_GameObject']['fileID']] in building_names]
    assert len(lods)==59 and all(len(d['m_LODs'])==3 for d in lods)
    # 47 houses and 32 rocks use 0.28 / 0.095; 3 very heavy district compounds
    # switch to saved LOD2 at 0.11. Three high-savings civic landmarks use
    # 0.22 / 0.095 / 0.0025; other civic assets retain 0.22 / 0.07.
    # Guard the entire scene against unrelated edits via exact protected SHA256.
    sys.path.insert(0, str(ROOT/'Tools/World'))
    from verify_tree_house_upgrade import SCENE_MOBILE_LOD_PATH, preserved_or_reviewed_lod_only
    contract=json.loads((ROOT/'Tools/World/tree-house-20261103.postconditions.json').read_text())
    assert preserved_or_reviewed_lod_only(ROOT, SCENE_MOBILE_LOD_PATH,
        contract['preserved_files'][SCENE_MOBILE_LOD_PATH]), 'Unexpected scene change outside reviewed LOD scalar values'
    revised={d['m_GameObject']['fileID'] for c,i,n,d in objects if n=='LODGroup'
        and [l['screenRelativeHeight'] for l in d['m_LODs']]==[.28,.095,.0025]}
    assert len(revised)==79, 'Expected exactly 47 homes and 32 rock covers with revised LOD'
    heavy_names={'SouthernAirfield_Airfield_01','CanalMarket_CanalTown_01',
        'OldVillage_HanokVillage_01'}
    actual_heavy={names[d['m_GameObject']['fileID']] for d in lods
        if [l['screenRelativeHeight'] for l in d['m_LODs']]==[.22,.11,.0025]}
    assert actual_heavy==heavy_names, 'Heavy compound scene LOD owner mismatch'
    from verify_tree_house_upgrade import CIVIC_DISTANCE_LOD_SCENE_NAMES
    actual_civic={names[d['m_GameObject']['fileID']] for d in lods
        if [l['screenRelativeHeight'] for l in d['m_LODs']]==[.22,.095,.0025]}
    assert actual_civic==CIVIC_DISTANCE_LOD_SCENE_NAMES, 'Civic distance LOD owner mismatch'
    assert all([l['screenRelativeHeight'] for l in d['m_LODs']] in
        ([.28,.095,.0025], [.22,.07,.0025], [.22,.11,.0025], [.22,.095,.0025])
        for d in lods), 'Unreviewed building LOD settings'
    sky=next(d for c,i,n,d in documents(ROOT/'Assets/TerrainDemoScene_URP/Prefabs/Skybox/Materials/Sky.mat') if n=='Material')
    assert sky['m_Shader']['fileID']==106 and sky['m_ShaderKeywords']=='_SUNDISK_HIGH_QUALITY'
    sun=next(d for c,i,n,d in objects if n=='Light');assert sun['m_Enabled'] and 1<=sun['m_Intensity']<=1.5
    settings=documents(ROOT/'ProjectSettings/ProjectSettings.asset')[0][3]
    assert settings['activeInputHandler']==0 and settings['AndroidTargetArchitectures']==2 and settings['scriptingBackend']['Android']==1
    for target in ['Android','Standalone','WebGL']:assert 'URP_COMPATIBILITY_MODE' in settings['scriptingDefineSymbols'][target].split(';')
    quality=documents(ROOT/'ProjectSettings/QualitySettings.asset')[0][3]['m_QualitySettings'][0]
    assert quality['globalTextureMipmapLimit']==0 and quality['streamingMipmapsActive']==1 and quality['lodBias']==1
    for name in ['Asphalt','Concrete','Sand','RoadMark']:
        assert 'TextureImporter'in yaml.safe_load((ROOT/f'Assets/AarisWorld/Surfaces/{name}.png.meta').read_text())
    layout=json.loads((ROOT/'Docs/MAP_LAYOUT.json').read_text())
    plan=json.loads((ROOT/'Docs/FOREST_PLANTING_PLAN.json').read_text())['placements'];assert len(plan)==1100
    assert {p['tile'] for p in plan}=={'1_1','1_2','2_1','2_2'}
    pts=np.array([p['position_xz_m'] for p in plan]);segments=np.array([[p[::2],q[::2]] for r in layout['roads'] for p,q in zip(r['points'],r['points'][1:])])
    for chunk in np.array_split(pts,12):assert segment_distance(chunk,segments).min()>16.99
    for s in layout['districts']:
        assert not np.any((np.abs(pts[:,0]-s['x'])<s['width']/2+17.99)&(np.abs(pts[:,1]-s['z'])<s['depth']/2+17.99)),s['name']
    parser=Parser(Language(tree_sitter_c_sharp.language()));syntax=[]
    for path in (ROOT/'Assets/AarisWorld').rglob('*.cs'):
        assert not parser.parse(path.read_bytes()).root_node.has_error,'C# syntax error '+str(path)
        syntax.append(path.relative_to(ROOT).as_posix())
    for path in [ROOT/'Tools/MapBuild/ground_review.py',ROOT/'Tools/MapBuild/ground_native.py']:ast.parse(path.read_text())
    report=json.loads((ROOT/'Docs/GROUND_REVIEW_REPORT.json').read_text())
    if require_native:assert report['native_assets_materialized'],'Native assets are still pending'
    surface_report=ROOT/'Docs/ROAD_SURFACE_REPORT.json'
    if surface_report.exists():
        roads=json.loads(surface_report.read_text())
        assert roads['road_meshes_unchanged'] and roads['paint_meshes_saved']==len(layout['roads'])
        for r in roads['roads']:
            assert .0198<r['saved_min_gap_m']<=r['saved_max_gap_m']<.0202
    map_report=json.loads((ROOT/'Docs/MAP_BUILD_REPORT.json').read_text())
    for path,expected in map_report['terrain_files'].items():
        if not (ROOT/path).exists():assert not require_native,'Terrain file missing';continue
        raw=(ROOT/path).read_bytes()
        if raw.startswith(b'version https://git-lfs'):assert ('oid sha256:'+expected['sha256']).encode() in raw
        else:assert hashlib.sha256(raw).hexdigest()==expected['sha256']
    if (ROOT/'TRANSFER-MANIFEST.json').exists():
        guids={yaml.safe_load(p.read_text())['guid'] for p in (ROOT/'Assets').rglob('*.meta')}
        for c,i,n,d in objects:
            for r in refs(d):
                g=r.get('guid')
                if g and not g.startswith('0000000000000000'):assert g in guids,(n,'Unresolved asset GUID',g)
    if report['native_assets_materialized']:
        index=json.loads((ROOT/'Docs/TERRAIN_INDEX.json').read_text())
        total=sum(t['terrain_instances'].get('tree',0) for t in index['tiles'])
        assert total==report['total_tree_count']==856+report['accepted_new_trees']
        assert report['accepted_new_trees']>=900
        for name,counts in report['building_lod_triangles'].items():
            assert counts['lod2_triangles']<=counts['lod0_triangles']
            for level in [1,2]:assert (ROOT/f'Assets/AarisWorld/Models/{name}/Geometry_LOD{level}.asset.meta').exists()
    result={'status':'passed','saved_ground_player_bound':True,'free_flight_camera_removed':True,'terrain_count':4,
        'vehicle_marker_count':45,'building_lod_levels':3,'planned_new_trees':1100,
        'native_assets_materialized':report['native_assets_materialized'],'csharp_syntax_checked':syntax,
        'unity_editor_compilation_tested':False,'unity_runtime_input_tested':False,'android_device_tested':False}
    (ROOT/'Docs/EXPLORER_SETUP_REPORT.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))
    return result

if __name__=='__main__':check('--native'in sys.argv)
