"""Verify saved scale, prefab integration and daylight. This is not a render test."""
import hashlib,json,math,sys,unittest
from pathlib import Path
import numpy as np
ROOT=Path.cwd();sys.path.insert(0,str(ROOT/'Tools/MapBuild'))
from ground_review import documents
from world_review import HOMES,SCENE,SKY,fit_layout,segment_box_distance
from check_explorer import (NativeSceneResolver,ResolvedScene,active_detail_scene_paths,
                            airy_house_contract,assert_lod_thresholds,transform_matrix)

def lod_geometry(view, lod, root_identity=None):
    """Resolve every LOD renderer to its mesh, ordered materials and local pose."""
    if root_identity is None:
        roots=[i for i,t in view.transforms.items()if t['m_Father']['fileID']==0]
        assert len(roots)==1,(view.path,'Expected one prefab/building root')
        root_identity=roots[0]
    root_inverse=np.linalg.inv(view.matrix(root_identity));result=[]
    for level in lod['m_LODs']:
        assert len(level['renderers'])==1,(view.path,'LOD renderer count changed')
        identity=level['renderers'][0]['renderer']['fileID']
        assert identity in view.byid and view.byid[identity][0]==23,(view.path,'Invalid LOD renderer',identity)
        renderer=view.byid[identity][2];owner=renderer['m_GameObject']['fileID']
        meshes=[d['m_Mesh']for c,i,n,d in view.objects if c==33 and d['m_GameObject']['fileID']==owner]
        poses=[i for i,t in view.transforms.items()if t['m_GameObject']['fileID']==owner]
        assert len(meshes)==len(poses)==1,(view.path,'LOD mesh/transform missing',identity)
        result.append((meshes[0],renderer['m_Materials'],root_inverse@view.matrix(poses[0])))
    return result

def same_geometry(actual, expected, context):
    assert len(actual)==len(expected),(context,'LOD geometry count changed')
    for level,(observed,source)in enumerate(zip(actual,expected)):
        assert observed[:2]==source[:2],(context,'LOD mesh/material references changed',level)
        assert np.allclose(observed[2],source[2],rtol=0,atol=1e-5),\
            (context,'LOD relative transform changed',level)

def same_collider(actual, expected, context):
    assert len(actual)==len(expected)==1,(context,'Collider missing/duplicated')
    for field in ('m_Mesh','m_Enabled','m_IsTrigger','m_Convex','m_Material'):
        assert actual[0][field]==expected[0][field],(context,'Collider field changed',field)

def persistent_proxy_name(name):
    # The first native partition pilot predates the later district naming rule.
    return '__AarisForestLookout_PersistentCover'if name=='ForestLookout_watchtower_01'else '__AarisPersistentCover_'+name

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
    resolver=NativeSceneResolver(ROOT)
    house_report,houses,replacement_models=airy_house_contract(ROOT)
    canonical={}
    for preset in house_report['prefabs']:
        path=ROOT/f'Assets/AarisWorld/Models/AiryHouses/{preset}.prefab'
        view=resolver.resolve(path)
        guid=next(g for g,p in resolver.prefabs.items()if p==path)
        canonical[preset]=(view,guid)
    prefab_checks=[]
    for name,model in layout['models'].items():
        path=ROOT/f'Assets/AarisWorld/Models/{name}/{name}.prefab'
        view=resolver.resolve(path);objects=view.objects;ids={i for c,i,n,d in objects}
        assert len(ids)==len(objects),(name,'Duplicate prefab ID')
        from check_explorer import refs
        for c,i,n,d in objects:
            for r in refs(d):
                if r['fileID'] and not r.get('guid'):assert r['fileID']in ids,(name,'Missing prefab reference',r)
        root=next(d for c,i,n,d in objects if n=='Transform' and d['m_Father']['fileID']==0)
        replacement=replacement_models.get(name)
        expected_scale=1.0 if replacement else model['prefab_scale']
        assert list(root['m_LocalScale'].values())==[expected_scale]*3,(name,'Authored prefab scale changed')
        lod=next(d for c,i,n,d in objects if n=='LODGroup')
        assert_lod_thresholds(lod,[.085,.026,.005]if replacement else [.22,.07,.0025],name)
        renderers={i:d for c,i,n,d in objects if n=='MeshRenderer'};assert len(renderers)==3
        for level in lod['m_LODs']:assert len(level['renderers'])==1 and level['renderers'][0]['renderer']['fileID']in renderers
        colliders=[d for c,i,n,d in objects if n=='MeshCollider'];assert len(colliders)==1
        if replacement:
            source,_=canonical[replacement]
            source_lod=next(d for c,i,n,d in source.objects if c==205)
            assert_lod_thresholds(source_lod,[.085,.026,.005],replacement)
            same_geometry(lod_geometry(view,lod),lod_geometry(source,source_lod),name)
            source_colliders=[d for c,i,n,d in source.objects if c==64]
            same_collider(colliders,source_colliders,(name,'Authored walkable collider'))
        else:
            assert all(len(d['m_Materials'])==model['submeshes']for d in renderers.values()),(name,'Material slots changed')
            assert colliders[0]['m_Mesh']['guid']==model['guid']
        prefab_checks.append(name)
    main_view=resolver.resolve(ROOT/SCENE);objects=main_view.objects;names=main_view.names
    wanted={b['name']for b in layout['buildings']}|set(houses)|{'AarisPlayer','Sun'}
    wanted|={persistent_proxy_name(name)for name in wanted}
    records={}
    def collect(view):
        for name in wanted&view.named.keys():
            identity=view.named_transform(name)
            assert name not in records,('Duplicate authored object across scenes',name,view.path)
            records[name]=(view,identity)
    collect(main_view)
    chunk_folder=ROOT/'Assets/AarisWorld/Scenes/Chunks'
    detail_scene=chunk_folder/'Aaris_ForestLookout_Detail.unity'
    all_cells=active_detail_scene_paths(ROOT)
    streamed_names=set()
    for cell_path in all_cells:
        detail_view=resolver.resolve(cell_path);detail=detail_view.objects
        root_names=detail_view.root_names()
        assert len(root_names)==len(set(root_names)),('Duplicate authored cell root',cell_path)
        assert streamed_names.isdisjoint(root_names),('Duplicate full-detail building across cells',cell_path)
        assert root_names,('Empty district chunk',cell_path)
        assert not any(c in (54,64,65,135,136,143,146,154,218)for c,i,n,d in detail),\
            ('Inherited or explicit physics entered visual cell',cell_path)
        if cell_path==detail_scene:
            assert root_names==['ForestLookout_watchtower_01'],\
                'Expected full-detail tower missing'
            assert len(detail_view.transforms)==3,'Additive landmark contents changed unexpectedly'
        streamed_names.update(root_names);collect(detail_view)
    transforms={name:view.transforms[identity]for name,(view,identity)in records.items()}
    for name,house in houses.items():
        assert name in records,('Missing authored Airy house',name)
        view,identity=records[name];transform=view.transforms[identity]
        source,guid=canonical[house['prefab']]
        assert transform.get('m_CorrespondingSourceObject',{}).get('guid')==guid,\
            (name,'Authored prefab source changed')
        angle=math.radians(house['yaw'])*.5
        expected=transform_matrix(dict(zip(('x','y','z'),house['position'])),
            {'x':0,'y':math.sin(angle),'z':0,'w':math.cos(angle)},dict(zip(('x','y','z'),house['scale'])))
        assert np.allclose(view.matrix(identity),expected,rtol=0,atol=.001),\
            (name,'World pose differs from saved Airy authoring audit')
        owner=transform['m_GameObject']['fileID']
        actual_lods=[d for c,i,n,d in view.objects if c==205 and d['m_GameObject']['fileID']==owner]
        source_lod=next(d for c,i,n,d in source.objects if c==205)
        assert len(actual_lods)==1,(name,'Missing authored LOD group')
        assert_lod_thresholds(actual_lods[0],[.085,.026,.005],name)
        same_geometry(lod_geometry(view,actual_lods[0],identity),lod_geometry(source,source_lod),name)
        if 'chunk'in house:
            assert view.path in all_cells and name in streamed_names,(name,'Detail no longer streams')
            proxy=persistent_proxy_name(name)
            assert proxy in records and records[proxy][0]is main_view,(name,'Persistent collision proxy missing')
            proxy_id=records[proxy][1]
            assert np.allclose(view.matrix(identity),main_view.matrix(proxy_id),rtol=0,atol=.001),\
                (name,'Streamed visual/persistent collider world-pose mismatch')
            owner=main_view.transforms[proxy_id]['m_GameObject']['fileID']
            actual=[d for c,i,n,d in main_view.objects if c==64 and d['m_GameObject']['fileID']==owner]
            expected=[d for c,i,n,d in source.objects if c==64]
            same_collider(actual,expected,(name,'Persistent walkable collider'))
        else:
            assert view is main_view,(name,'Persistent house moved out of bootstrap')
            actual=[d for c,i,n,d in main_view.objects if c==64 and d['m_GameObject']['fileID']==owner]
            expected=[d for c,i,n,d in source.objects if c==64]
            same_collider(actual,expected,(name,'Persistent prefab collider'))
    for b in layout['buildings']:
        name=b['name']
        assert name in transforms, ('Missing authored building across scenes',name)
        if name in houses:continue # Exact current audit and proxy world-pose checks above.
        observed=list(transforms[name]['m_LocalScale'].values())
        if observed != b['scale']:
            # Unity reparents migrated building roots into native additive
            # scenes while preserving the actual world transform. The new
            # root's local scale is its former *world* scale, not its
            # district-relative authoring scale. Demand the original exact
            # local scale on the persistent collision/far-LOD proxy instead.
            proxy=persistent_proxy_name(name)
            assert proxy in transforms, ('World-space reparent lost authored proxy',name)
            assert list(transforms[proxy]['m_LocalScale'].values())==b['scale'],\
                ('Original authored district-relative scale changed',name)
            assert name in streamed_names,\
                ('Reparented full detail missing from native cell',name)
        proxy=persistent_proxy_name(name)
        if name in streamed_names:
            assert proxy in records and records[proxy][0]is main_view,(name,'Landmark collision proxy missing')
            view,identity=records[name]
            assert np.allclose(view.matrix(identity),main_view.matrix(records[proxy][1]),rtol=0,atol=.001),\
                (name,'Landmark visual/collider world-pose mismatch')
            source=resolver.resolve(ROOT/f"Assets/AarisWorld/Models/{b['model']}/{b['model']}.prefab")
            owner=view.transforms[identity]['m_GameObject']['fileID']
            actual_lods=[d for c,i,n,d in view.objects if c==205 and d['m_GameObject']['fileID']==owner]
            source_lod=next(d for c,i,n,d in source.objects if c==205)
            assert len(actual_lods)==1,(name,'Landmark LOD group missing')
            assert_lod_thresholds(actual_lods[0],[.22,.07,.0025],name)
            same_geometry(lod_geometry(view,actual_lods[0],identity),lod_geometry(source,source_lod),name)
            proxy_owner=main_view.transforms[records[proxy][1]]['m_GameObject']['fileID']
            actual=[d for c,i,n,d in main_view.objects if c==64 and d['m_GameObject']['fileID']==proxy_owner]
            expected=[d for c,i,n,d in source.objects if c==64]
            same_collider(actual,expected,(name,'Landmark collider'))
    scene_spawn = transforms['AarisPlayer']['m_LocalPosition']
    reported_spawn = report['safe_spawn_m']
    assert len(reported_spawn) == 3 and all(
        math.isclose(float(scene_spawn[axis]), float(reported_spawn[i]), rel_tol=0.0, abs_tol=1e-4)
        for i, axis in enumerate(('x', 'y', 'z'))
    ), 'Saved player spawn moved from verified position'
    assert 15<report['nearest_residential_footprint_m']<35
    player=next(d for c,i,n,d in objects if n=='MonoBehaviour' and names.get(d['m_GameObject']['fileID'])=='AarisPlayer')
    assert player['minLookPitch']==-70 and player['maxLookPitch']==70
    sky=next(d for c,i,n,d in documents(ROOT/SKY) if n=='Material')
    assert sky['m_Shader']['fileID']==106 and '_SUNDISK_HIGH_QUALITY'in sky['m_ValidKeywords']
    assert next(f['_Exposure'] for f in sky['m_SavedProperties']['m_Floats'] if '_Exposure'in f)==1.15
    rs=next(d for c,i,n,d in objects if n=='RenderSettings');sun=next((i,d) for c,i,n,d in objects if n=='Light' and names[d['m_GameObject']['fileID']]=='Sun')
    assert rs['m_Sun']['fileID']==sun[0] and rs['m_AmbientMode']==3
    assert rs['m_AmbientIntensity']==1.2
    assert rs['m_AmbientSkyColor']=={'r':.69,'g':.70,'b':.65,'a':1.0}
    # Preserve the current saved native lighting (also present before sectoring).
    assert sun[1]['m_Intensity']==1.35 and sun[1]['m_Shadows']['m_Strength']==.62
    q=transforms['Sun']['m_LocalRotation'];assert abs(sum(v*v for v in q.values())-1)<1e-6
    incoming_y=-2*(q['y']*q['z']-q['w']*q['x']);elevation=math.degrees(math.asin(incoming_y))
    assert abs(elevation-50)<.01 and -player['minLookPitch']>elevation,'Sun cannot be reached by upward look'
    camera=next(d for c,i,n,d in objects if n=='Camera');assert camera['m_ClearFlags']==1 and camera['field of view']==60
    graphics=documents(ROOT/'ProjectSettings/GraphicsSettings.asset')[0][3]
    assert any(s['fileID']==106 for s in graphics['m_AlwaysIncludedShaders'])
    if native:
        assert report['total_tree_count']==1939 and report['terrain_heights_details_prototypes_and_alpha_preserved']
        assert report['spawn_native_ground_and_obstacles_verified']
        for path,expected in report['original_close_mesh_hashes'].items():assert hashlib.sha256((ROOT/path).read_bytes()).hexdigest()==expected
        mobile_guid='758559729899d1340bf85e82688a079f'
        quality_settings=documents(ROOT/'ProjectSettings/QualitySettings.asset')[0][3]
        quality=quality_settings['m_QualitySettings'][quality_settings['m_CurrentQuality']]
        assert quality['customRenderPipeline']['guid']==mobile_guid and quality['lodBias']==.75,\
            'Active quality differs from the measured mobile preset'
        assert graphics['m_CustomRenderPipeline']['guid']==mobile_guid,\
            'Default graphics pipeline differs from the measured mobile preset'
        pipeline=documents(ROOT/'Assets/TerrainDemoScene_URP/Settings/URP/URP-LowQuality Pipeline Asset.asset')[0][3]
        assert pipeline['m_RenderScale']==1,'Render resolution changed'
    result={'status':'saved_core_checks_passed','building_prefabs_checked':len(prefab_checks),'scaled_residential_instances':47,
        'verified_airy_houses':len(houses),'verified_streamed_airy_houses':sum('chunk'in house for house in houses.values()),
        'verified_persistent_airy_houses':sum('chunk'not in house for house in houses.values()),
        'verified_preserved_landmarks':len(house_report['landmarks_preserved']),
        'sun_elevation_degrees':elevation,'upward_look_degrees':70,'native_assets_materialized':report['native_assets_materialized'],
        'unity_editor_compilation_tested':False,'unity_render_tested':False,'android_device_tested':False}
    (ROOT/'Docs/WORLD_CORE_CHECKS.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result));return result

if __name__=='__main__':check('--native'in sys.argv)
