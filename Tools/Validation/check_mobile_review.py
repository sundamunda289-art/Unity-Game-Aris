"""Check mobile bindings, preserved visuals and real authored prop geometry.
This verifies saved files, not Unity import, rendering, device FPS or crash recovery.
"""
import hashlib,json,math,re,sys
from pathlib import Path
import numpy as np
from tree_sitter import Language,Parser
import tree_sitter_c_sharp
ROOT=Path.cwd();sys.path.insert(0,str(ROOT/'Tools/MapBuild'))
from ground_review import documents

def mesh(path):
    d=documents(path)[0][3];v=np.frombuffer(bytes.fromhex(str(d['m_VertexData']['_typelessdata'])),dtype='<f4').reshape(-1,12)
    tri=np.frombuffer(bytes.fromhex(str(d['m_IndexBuffer'])),dtype='<u4').reshape(-1,3)
    assert np.isfinite(v).all() and tri.max()<len(v) and not d['m_IsReadable']
    assert np.all(np.linalg.norm(v[:,3:6],axis=1)>.9)
    return d,v,tri

def check(native=False):
    report=json.loads((ROOT/'Docs/MOBILE_REVIEW_REPORT.json').read_text());layout=json.loads((ROOT/'Docs/MAP_LAYOUT.json').read_text())
    parser=Parser(Language(tree_sitter_c_sharp.language()))
    for p in (ROOT/'Assets/AarisWorld').rglob('*.cs'):assert not parser.parse(p.read_bytes()).root_node.has_error,p
    objects=documents(ROOT/'Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity');ids={i for c,i,n,d in objects};assert len(ids)==len(objects)
    def references(x):
        if isinstance(x,list):
            for v in x:yield from references(v)
        if isinstance(x,dict):
            if 'fileID'in x:yield x
            for v in x.values():yield from references(v)
    for c,i,n,d in objects:
        for r in references(d):assert not r['fileID'] or r.get('guid') or r['fileID']in ids,(n,i,r)
    names={i:d['m_Name']for c,i,n,d in objects if n=='GameObject'};byid={i:(n,d)for c,i,n,d in objects}
    runtime=next(d for c,i,n,d in objects if n=='MonoBehaviour'and d.get('m_EditorClassIdentifier','').endswith('.AarisMobileRuntime'))
    nearby=next(d for c,i,n,d in objects if n=='MonoBehaviour'and d.get('m_EditorClassIdentifier','').endswith('.AarisNearbyColliders'))
    assert runtime['releaseTerrainPaintCpuCopies']==1 and runtime['targetFrameRate']==60
    assert len(runtime['terrains'])==4 and all(byid[r['fileID']][0]=='Terrain'for r in runtime['terrains'])
    gated={r['fileID']for r in nearby['obstacles']};actual={i for c,i,n,d in objects if n in ['MeshCollider','BoxCollider']}
    assert gated==actual and nearby['activationDistance']==55 and nearby['cellSize']==250
    # The runtime streamer skips colliders serialized disabled in Awake.
    # Previously 56 visible houses/landmarks were never registered and
    # remained permanently non-solid even after approaching them.
    phantom = [
        (names.get(d['m_GameObject']['fileID']), i)
        for c,i,n,d in objects if n=='MeshCollider' and i in gated
        and d['m_Enabled'] != 1
    ]
    assert not phantom, f'Saved map has permanently disabled house walls: {phantom}'
    generator=(ROOT/'Tools/MapBuild/mobile_review.py').read_text()
    assert "d['m_Enabled']=1" in generator
    assert "d['m_Enabled']=int(dx*dx+dz*dz<=55*55)" not in generator
    assert report['collision_spatial_cell_size_m']==250 and not report['full_terrain_asset_streaming_enabled']
    assert all(byid[i][0]not in ['TerrainCollider','CharacterController']for i in gated)
    assert sum(n=='TerrainCollider'and d['m_Enabled']==1 and d['m_EnableTreeColliders']==1 for c,i,n,d in objects)==4
    assert sum(n=='GameObject'and bool(re.fullmatch('VehicleSpawnPoint_[0-9]+',d['m_Name']))for c,i,n,d in objects)==45
    assert not any(n=='Rigidbody'for c,i,n,d in objects),'Wrecks must remain static props'
    placements=report['placements'];assert [sum(p['kind']==k for p in placements)for k in ['tower','static_wreck','rock_cover']]==[4,6,24]
    prop_names={p['name']for p in placements};assert prop_names<=set(names.values())
    for k,a in enumerate(placements):
        for b in placements[k+1:]:
            x=a['aabb_xz_m'];y=b['aabb_xz_m'];assert x[2]<=y[0]or x[0]>=y[2]or x[3]<=y[1]or x[1]>=y[3],(a['name'],b['name'])
        for s in layout['vehicle_spawns']:
            x,z=s['position_m'][::2];box=a['aabb_xz_m'];assert x<box[0]-8 or x>box[2]+8 or z<box[1]-8 or z>box[3]+8
    for name,entry in report['models'].items():
        counts=[]
        for level in range(3):
            _,v,tri=mesh(ROOT/f'Assets/AarisWorld/Props/{name}/Geometry{("_LOD"+str(level))if level else ""}.asset');counts.append(len(tri))
        assert counts==entry['triangles'] and counts[0]>counts[1]>counts[2],(name,counts)
        prefab=documents(ROOT/entry['reusable_prefab']);lod=next(d for c,i,n,d in prefab if n=='LODGroup');assert len(lod['m_LODs'])==3
        assert all(not(d['m_StaticEditorFlags']&4)for c,i,n,d in prefab if n=='GameObject')
    # The 1.78m controller has 0.30m step height and 46 degree slope limit.
    # Four continuous ramps reach 12m; landing edges have <0.10m step-up.
    tower=documents(ROOT/'Assets/AarisWorld/Props/ClimbableTower/ClimbableTower.prefab')
    t={i:d for c,i,n,d in tower if n=='Transform'};ramps=[]
    for c,i,n,d in tower:
        if n!='BoxCollider':continue
        tr=next(v for v in t.values()if v['m_GameObject']['fileID']==d['m_GameObject']['fileID'])
        angle=math.degrees(2*math.asin(tr['m_LocalRotation']['x']))
        if abs(angle)>1:ramps.append((tr,d,angle))
    assert len(ramps)==4
    for level,(tr,d,angle)in enumerate(ramps):
        assert abs(abs(angle)-math.degrees(math.atan(.5)))<.001 and abs(angle)<46
        assert d['m_Size']['x']>=1.7 and abs(tr['m_LocalPosition']['y']-(level*3+1.4))<.001
    assert all(p['standing_height_m']==12 and p['stair_width_m']>=1.7 and p['riser_m']<.30 for p in placements if p['kind']=='tower')
    source=(ROOT/'Assets/AarisWorld/Scripts/AarisMobileRuntime.cs').read_text()
    # The scene's legacy serialized release flag remains 1, but the runtime
    # deliberately ignores it. Do not upload/discard painted control maps:
    # GLES3 terrain splatmaps must be owned by Unity's Terrain renderer.
    assert 'texture.Apply(false, true)' not in source
    assert 'releaseTerrainPaintCpuCopies = false' in source
    assert 'AuditNativeTerrain();' in source
    # GPU timings, when supported, must guard against blurring a CPU-bound game.
    assert 'FrameTimingManager.IsFeatureEnabled()' in source
    assert 'FrameTimingManager.GetLatestTimings(1u, mobileFrameTimings)' in source
    assert 'timing.gpuFrameTime >= cpuWork * 1.1d' in source
    assert 'sample.cpuLimitedSamples >= sample.gpuLimitedSamples * 2' in source
    assert 'if (measuredCpuBound)' in source
    assert 's.meanGpuFrameMs' in source and 's.meanCpuMainMs' in source
    assert 'SampleMobileFrameTiming();' in source
    assert 'AARIS_TERRAIN_RENDER_AUDIT' in source
    assert 'terrainLitAvailable = lit != null && lit.isSupported;' in source
    assert 'tile.enabled && tile.drawHeightmap' in source
    assert 'GetPixels(' not in source
    android=(ROOT/'Assets/AarisWorld/Editor/AarisCodespaceAndroidBuild.cs').read_text()
    assert 'ANDROID_TERRAIN_LIT_MATERIAL_INVALID' in android
    assert 'ANDROID_TERRAIN_HEIGHTMAP_HIDDEN' in android
    assert 'ANDROID_TERRAIN_PAINT_LAYERS_MISSING' in android
    assert 'AARIS_TERRAIN_ANDROID_PREFLIGHT_FOUR_PAINTED_TILES_READY' in android
    terrain_mat=(ROOT/'Assets/TerrainDemoScene_URP/Terrain/Materials/TerrainLit.mat').read_text()
    assert 'm_Shader: {fileID: 4800000, guid: 69c1f799e772cb6438f56c23efccb782, type: 3}' in terrain_mat
    assert 'GetTotalMemory(false)'in source and 'Application.lowMemory +='in source
    assert 'GC.Collect'not in source and 'GC.Collect'not in (ROOT/'Assets/AarisWorld/Scripts/AarisNearbyColliders.cs').read_text()
    player=(ROOT/'Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs').read_text();assert 'nearbyColliders.RefreshNow()'in player and 'GUIUtility.systemCopyBuffer'in player
    assert 'horizontalVelocity.sqrMagnitude > 0.000001f' in player
    assert 'SafeFooting(candidate)' in player and 'CanAdvanceProne(candidate, horizontalVelocity)' in player
    settings=documents(ROOT/'ProjectSettings/ProjectSettings.asset')[0][3]
    assert settings['bundleVersion']=='0.3.1' and settings['AndroidBundleVersionCode']==7
    assert settings['productName']=='Aaris Game'
    assert settings['applicationIdentifier']['Android']=='com.aaris.game'
    assert settings['AndroidMinSdkVersion']==26 and settings['AndroidTargetSdkVersion']==33
    assert settings['AndroidTargetArchitectures']==2
    assert settings['AndroidPreferredInstallLocation']==0
    # Unity stores this as a byte string; generic YAML interprets its 0b prefix as binary zero.
    apis=settings['m_BuildTargetGraphicsAPIs'];assert apis==[{'m_BuildTarget':'AndroidPlayer','m_APIs':0,'m_Automatic':0}]
    assert 'm_APIs: 0b000000' in (ROOT/'ProjectSettings/ProjectSettings.asset').read_text()
    assert json.loads((ROOT/'Packages/manifest.json').read_text())['dependencies']['com.unity.modules.androidjni']=='1.0.0'
    q=documents(ROOT/'ProjectSettings/QualitySettings.asset')[0][3]['m_QualitySettings'][0]
    assert q['globalTextureMipmapLimit']==0 and q['resolutionScalingFixedDPIFactor']==1 and q['lodBias']==1
    light=next(d for c,i,n,d in objects if n=='Light');assert light['m_Intensity']==1.35 and light['m_Shadows']['m_Strength']==.55
    if native:
        from mobile_review import height_grid,sample
        h=height_grid(ROOT)
        for a,b,axis in [('1_1','2_1','x'),('1_2','2_2','x'),('1_1','1_2','z'),('2_1','2_2','z')]:
            assert np.array_equal(h[a][:,-1],h[b][:,0])if axis=='x'else np.array_equal(h[a][-1,:],h[b][0,:])
        for p in placements:
            if p['kind']=='tower':
                x,y,z=p['position_m'];vals=[sample(h,x+dx,z+dz)for dx in np.linspace(-3.4,3.4,16)for dz in np.linspace(-4.5,4.5,20)]
                assert max(vals)-min(vals)<.03 and abs(y-np.mean(vals))<.03,p['name']
    assert not report['android_device_tested'] and not report['crash_root_cause_confirmed']
    result={'status':'saved_mobile_checks_passed','native_terrain_bytes_checked':native,'preserved_terrain_tiles':4,'saved_prop_instances':len(placements),'gated_colliders':len(gated),'unity_editor_compiled':False,'android_device_tested':False}
    print(json.dumps(result));return result

if __name__=='__main__':check('--native'in sys.argv)
