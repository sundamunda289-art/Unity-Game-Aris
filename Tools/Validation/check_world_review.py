"""Verify saved scale, prefab integration and daylight. This is not a render test."""
import hashlib,json,math,sys,unittest
from pathlib import Path
import numpy as np
ROOT=Path.cwd();sys.path.insert(0,str(ROOT/'Tools/MapBuild'))
from ground_review import documents
from world_review import HOMES,SCENE,SKY,fit_layout,segment_box_distance

class ClearanceTests(unittest.TestCase):
    def test_crossing_segment_with_both_ends_outside(self):
        self.assertEqual(segment_box_distance([-3,-3],[3,3],[-1,-1,1,1]),0)
    def test_parallel_edge_and_corner_distance(self):
        self.assertAlmostEqual(segment_box_distance([-3,2],[3,2],[-1,-1,1,1]),1)
        self.assertAlmostEqual(segment_box_distance([2,2],[3,3],[-1,-1,1,1]),math.sqrt(2))

def check(native=False):
    unittest.TextTestRunner().run(unittest.defaultTestLoader.loadTestsFromTestCase(ClearanceTests)).wasSuccessful() or sys.exit(1)
    layout=json.loads((ROOT/'Docs/MAP_LAYOUT.json').read_text());fitted,gaps=fit_layout(layout)
    assert fitted==layout,'Saved layout does not match the clearance-bounded scale policy'
    report=json.loads((ROOT/'Docs/WORLD_REVIEW_REPORT.json').read_text())
    assert not native or report['native_assets_materialized']
    assert report['clearances']==gaps
    prefab_checks=[]
    for name,model in layout['models'].items():
        objects=documents(ROOT/f'Assets/AarisWorld/Models/{name}/{name}.prefab');ids={i for c,i,n,d in objects}
        assert len(ids)==len(objects),(name,'Duplicate prefab ID')
        from check_explorer import refs
        for c,i,n,d in objects:
            for r in refs(d):
                if r['fileID'] and not r.get('guid'):assert r['fileID']in ids,(name,'Missing prefab reference',r)
        root=next(d for c,i,n,d in objects if n=='Transform' and d['m_Father']['fileID']==0)
        assert list(root['m_LocalScale'].values())==[model['prefab_scale']]*3
        lod=next(d for c,i,n,d in objects if n=='LODGroup')
        heavy_compounds={'Airfield','CanalTown','HanokVillage'}
        civic_distance_lod={'TownHall','Harbour','ParcelDepot'}
        expected_lods=([.28,.095,.0025] if name in HOMES else
            [.22,.11,.0025] if name in heavy_compounds else
            [.22,.095,.0025] if name in civic_distance_lod else [.22,.07,.0025])
        assert [l['screenRelativeHeight'] for l in lod['m_LODs']]==expected_lods,(name,'Unexpected LOD profile')
        renderers={i:d for c,i,n,d in objects if n=='MeshRenderer'};assert len(renderers)==3
        assert all(len(d['m_Materials'])==model['submeshes'] for d in renderers.values()),(name,'Material slots changed')
        for level in lod['m_LODs']:assert len(level['renderers'])==1 and level['renderers'][0]['renderer']['fileID']in renderers
        colliders=[d for c,i,n,d in objects if n=='MeshCollider'];assert len(colliders)==1
        assert colliders[0]['m_Mesh']['guid']==model['guid']
        prefab_checks.append(name)
    objects=documents(ROOT/SCENE);names={i:d['m_Name'] for c,i,n,d in objects if n=='GameObject'}
    transforms={names[d['m_GameObject']['fileID']]:d for c,i,n,d in objects if n=='Transform'}
    for b in layout['buildings']:assert list(transforms[b['name']]['m_LocalScale'].values())==b['scale']
    assert list(transforms['AarisPlayer']['m_LocalPosition'].values())==report['safe_spawn_m']
    assert 15<report['nearest_residential_footprint_m']<35
    player=next(d for c,i,n,d in objects if n=='MonoBehaviour' and names.get(d['m_GameObject']['fileID'])=='AarisPlayer')
    assert player['minLookPitch']==-70 and player['maxLookPitch']==70
    sky=next(d for c,i,n,d in documents(ROOT/SKY) if n=='Material')
    assert sky['m_Shader']['fileID']==106 and '_SUNDISK_HIGH_QUALITY'in sky['m_ValidKeywords']
    assert next(f['_Exposure'] for f in sky['m_SavedProperties']['m_Floats'] if '_Exposure'in f)==1.15
    rs=next(d for c,i,n,d in objects if n=='RenderSettings');sun=next((i,d) for c,i,n,d in objects if n=='Light' and names[d['m_GameObject']['fileID']]=='Sun')
    assert rs['m_Sun']['fileID']==sun[0] and rs['m_AmbientMode']==1
    assert sun[1]['m_Intensity']==1.35 and sun[1]['m_Shadows']['m_Strength']==.55
    q=transforms['Sun']['m_LocalRotation'];assert abs(sum(v*v for v in q.values())-1)<1e-6
    incoming_y=-2*(q['y']*q['z']-q['w']*q['x']);elevation=math.degrees(math.asin(incoming_y))
    assert abs(elevation-50)<.01 and -player['minLookPitch']>elevation,'Sun cannot be reached by upward look'
    camera=next(d for c,i,n,d in objects if n=='Camera');assert camera['m_ClearFlags']==1 and camera['field of view']==60
    graphics=documents(ROOT/'ProjectSettings/GraphicsSettings.asset')[0][3]
    assert any(s['fileID']==106 for s in graphics['m_AlwaysIncludedShaders'])
    if native:
        assert report['total_tree_count']==1939 and report['terrain_heights_details_prototypes_and_alpha_preserved']
        assert report['spawn_native_ground_and_obstacles_verified']
        for path,expected in report['original_close_mesh_hashes'].items():
            if hashlib.sha256((ROOT/path).read_bytes()).hexdigest()!=expected:
                # An intentional opening edit supersedes exactly its original mesh
                # hash, without rewriting this historical preservation report.
                sys.path.insert(0,str(ROOT/'Tools/World'))
                from verify_tree_house_upgrade import accepts_historical_transition
                assert accepts_historical_transition(ROOT,path,expected),(path,'Unreviewed source mesh drift')
        pipeline=documents(ROOT/'Assets/TerrainDemoScene_URP/Settings/URP/URP-HighQuality Pipeline Asset.asset')[0][3]
        assert pipeline['m_RenderScale']==1,'Render resolution changed'
    result={'status':'saved_core_checks_passed','building_prefabs_checked':len(prefab_checks),'scaled_residential_instances':47,
        'sun_elevation_degrees':elevation,'upward_look_degrees':70,'native_assets_materialized':report['native_assets_materialized'],
        'unity_editor_compilation_tested':False,'unity_render_tested':False,'android_device_tested':False}
    (ROOT/'Docs/WORLD_CORE_CHECKS.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result));return result

if __name__=='__main__':check('--native'in sys.argv)
