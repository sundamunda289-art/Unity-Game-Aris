"""Save human-scale buildings, complete prefabs and clear daylight in core assets.

This is offline authoring. Checked-in outputs open/build directly in Unity.
"""
from __future__ import annotations
import copy,hashlib,json,math,re
from collections import Counter
from pathlib import Path
from types import SimpleNamespace
import numpy as np
import yaml
from ground_review import documents,segment_distance
from native_assets import color,guid,ref,vec,write_yaml,Flow,quat_y
from native_scene import Scene

HOMES={'a_frame_cabin','bungalow_house','detached_two_storey_house','garage_apartment',
       'pitched_roof_cottage','row_three_townhouses','two_bedroom_garage_house'}
HOME_SCALE=1.12
CYPRESS_FACTOR=1.12
SCENE='Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity'
SKY='Assets/TerrainDemoScene_URP/Prefabs/Skybox/Materials/Sky.mat'

def save_json(path,data):path.write_text(json.dumps(data,indent=2)+'\n')

def box_distance(a,b):
    return math.hypot(max(a[0]-b[2],b[0]-a[2],0),max(a[1]-b[3],b[1]-a[3],0))

def segment_box_distance(a,b,box):
    a=np.asarray(a,float);b=np.asarray(b,float);delta=b-a;lo=0.;hi=1.
    for axis in (0,1):
        if abs(delta[axis])<1e-10:
            if not box[axis]<=a[axis]<=box[axis+2]:break
        else:
            t=sorted([(box[axis]-a[axis])/delta[axis],(box[axis+2]-a[axis])/delta[axis]])
            lo=max(lo,t[0]);hi=min(hi,t[1])
    else:
        if lo<=hi:return 0.
    corners=np.array([[box[0],box[1]],[box[0],box[3]],[box[2],box[1]],[box[2],box[3]]])
    t=np.clip((corners-a)@delta/max(float(delta@delta),1e-15),0,1)
    point_distance=np.linalg.norm(corners-a-t[:,None]*delta,axis=1).min()
    return min(float(point_distance),box_distance([*a,*a],box),box_distance([*b,*b],box))

def scaled_layout(layout,factors=None):
    layout=copy.deepcopy(layout)
    factors=factors or {name:HOME_SCALE for name in HOMES}
    for b in layout['buildings']:
        factor=factors.get(b['model'],1.)
        dims=np.asarray(layout['models'][b['model']]['dimensions_m'])*factor
        angle=math.radians(b['yaw_degrees']);c=abs(math.cos(angle));s=abs(math.sin(angle))
        hx=(dims[0]*c+dims[2]*s)/2;hz=(dims[0]*s+dims[2]*c)/2;x,y,z=b['position_m']
        b['scale']=[factor]*3;b['world_dimensions_m']=dims.tolist();b['aabb_xz_m']=[x-hx,z-hz,x+hx,z+hz]
    for name,model in layout['models'].items():model['prefab_scale']=factors.get(name,1.)
    return layout

def fit_layout(layout):
    """Keep one scale per house type, bounded by every saved road/bay clearance."""
    factors={name:HOME_SCALE for name in HOMES};by_name={b['name']:b['model'] for b in layout['buildings']}
    for _ in range(200):
        candidate=scaled_layout(layout,factors)
        try:return candidate,clearances(candidate)
        except AssertionError as error:
            detail=error.args[0]
            if not isinstance(detail,tuple) or detail[0] not in ('Building overlap','Foundation enters road','Building enters vehicle corridor'):raise
            models={by_name[name] for name in detail[1:] if isinstance(name,str) and name in by_name and by_name[name]in HOMES}
            if not models:raise
            for model in models:
                if factors[model]<=1.00001:raise
                factors[model]=round(max(1.,factors[model]-.005),3)
    raise AssertionError('Could not find a safe house scale')

def clearances(layout):
    buildings=layout['buildings'];road_gap=float('inf');bay_gap=float('inf');building_gap=float('inf')
    for i,b in enumerate(buildings):
        q=b['aabb_xz_m']
        for other in buildings[i+1:]:
            gap=box_distance(q,other['aabb_xz_m']);assert gap>0,('Building overlap',b['name'],other['name'])
            building_gap=min(building_gap,gap)
        for road in layout['roads']:
            for a,z in zip(road['points'],road['points'][1:]):
                # Include the extra metre of visible foundation on every side.
                foundation=[q[0]-1,q[1]-1,q[2]+1,q[3]+1] if b['model']!='CanalTown' else q
                gap=segment_box_distance(a[::2],z[::2],foundation)-road['width']/2
                assert gap>.15,('Foundation enters road',b['name'],road['name'],gap)
                road_gap=min(road_gap,gap)
        for sp in layout['vehicle_spawns']:
            x,y,z=sp['position_m'];ex,ez=sp['exit_world_xz_m']
            bay=[min(x-3,ex-3),min(z-6,ez)-1,max(x+3,ex+3),max(z+6,ez)+1]
            gap=box_distance(q,bay);assert gap>.25,('Building enters vehicle corridor',b['name'],sp['name'])
            bay_gap=min(bay_gap,gap)
    return {'minimum_building_gap_m':building_gap,'minimum_foundation_to_road_edge_m':road_gap,'minimum_building_to_vehicle_corridor_m':bay_gap}

def complete_prefab(root,name,factor):
    path=root/f'Assets/AarisWorld/Models/{name}/{name}.prefab'
    old=documents(path);root_go=next(d['m_GameObject']['fileID'] for c,i,n,d in old if n=='Transform' and d['m_Father']['fileID']==0)
    mesh=next(d['m_Mesh'] for c,i,n,d in old if n=='MeshFilter' and d['m_GameObject']['fileID']==root_go)
    mats=[m['guid'] for m in next(d for c,i,n,d in old if n=='MeshRenderer' and d['m_GameObject']['fileID']==root_go)['m_Materials']]
    lod=copy.deepcopy(next(d for c,i,n,d in old if n=='LODGroup'))
    scene=Scene();go,tr=scene.model(name,mesh['guid'],mats,lod=False)
    scene.transforms[tr]['m_LocalScale']=vec(factor,factor,factor)
    old_id=next(i for c,i,n,d in old if n=='LODGroup')
    scene.next_id=max(scene.next_id,old_id+1)
    # Match the saved low-memory residential authoring policy; other types retain original LODs.
    near_height,mid_height=(.28,.095) if name in HOMES else (.22,.07)
    levels=[{'screenRelativeHeight':near_height,'fadeTransitionWidth':.12,'renderers':[{'renderer':ref(scene.renderers[-1])}]}]
    for level,threshold in ((1,mid_height),(2,.0025)):
        scene.model(f'{name}_LOD{level}',guid(f'Assets/AarisWorld/Models/{name}/Geometry_LOD{level}.asset'),mats,parent=tr,collider=False,lod=False)
        levels.append({'screenRelativeHeight':threshold,'fadeTransitionWidth':.12,'renderers':[{'renderer':ref(scene.renderers[-1])}]})
    fields={k:v for k,v in lod.items() if k not in ('m_GameObject','m_ObjectHideFlags','m_CorrespondingSourceObject','m_PrefabInstance','m_PrefabAsset')}
    fields.update(m_LODs=levels,m_FadeMode=1,m_AnimateCrossFading=1)
    gd=next(d for c,i,n,d in scene.objects if n=='GameObject' and i==go)
    scene.component(go,gd,205,'LODGroup',fields)
    # Keep pre-existing root/component file IDs so prefab references remain stable.
    lod_object=scene.objects.pop();new_id=lod_object[1]
    assert not any(i==old_id for c,i,n,d in scene.objects),'LOD ID collides with another component'
    for item in gd['m_Component']:
        if item['component']['fileID']==new_id:item['component']=ref(old_id)
    scene.objects.append((205,old_id,'LODGroup',lod_object[3]))
    write_yaml(path,scene.objects)

def save_world(root):
    root=Path(root);layout_path=root/'Docs/MAP_LAYOUT.json'
    layout,gaps=fit_layout(json.loads(layout_path.read_text()))
    objects=documents(root/SCENE);buildings={b['name']:b for b in layout['buildings']}
    go_names={i:d['m_Name'] for c,i,n,d in objects if n=='GameObject'};sun_go=next(i for i,n in go_names.items() if n=='Sun')
    village=next(d for d in layout['districts'] if d['name']=='ValleySettlement')
    spawn=np.array([-496.,village['height']+.085,-212.]);spawn_yaw=40.
    nearest_home=min(box_distance([spawn[0],spawn[2],spawn[0],spawn[2]],b['aabb_xz_m']) for b in layout['buildings'] if b['model']in HOMES)
    assert 15<nearest_home<35,'Spawn must give an immediate, safe residential scale reference'
    for b in layout['buildings']:assert box_distance([spawn[0],spawn[2],spawn[0],spawn[2]],b['aabb_xz_m'])>3
    for c,i,n,d in objects:
        if n=='Transform' and go_names.get(d['m_GameObject']['fileID']) in buildings:
            b=buildings[go_names[d['m_GameObject']['fileID']]];d['m_LocalScale']=vec(*b['scale'])
        if n=='Transform' and go_names.get(d['m_GameObject']['fileID'])=='AarisPlayer':
            d['m_LocalPosition']=vec(*spawn);d['m_LocalRotation']=quat_y(spawn_yaw);d['m_LocalEulerAnglesHint']=vec(0,spawn_yaw,0)
        if n=='Transform' and go_names.get(d['m_GameObject']['fileID'])=='Main Camera':
            p=math.radians(12);y=math.radians(spawn_yaw)
            forward=np.array([math.sin(y)*math.cos(p),-math.sin(p),math.cos(y)*math.cos(p)])
            right=np.array([math.cos(y),0,-math.sin(y)])
            d['m_LocalPosition']=vec(*(spawn+[0,1.45,0]-forward*4.2+right*.3))
            d['m_LocalRotation']=Flow(x=math.cos(y/2)*math.sin(p/2),y=math.sin(y/2)*math.cos(p/2),z=-math.sin(y/2)*math.sin(p/2),w=math.cos(y/2)*math.cos(p/2))
            d['m_LocalEulerAnglesHint']=vec(12,spawn_yaw,0)
        if n=='RenderSettings':
            d.update(m_AmbientMode=1,m_AmbientIntensity=1,m_AmbientSkyColor=color(.62,.72,.86),
                m_AmbientEquatorColor=color(.54,.58,.56),m_AmbientGroundColor=color(.32,.34,.31),m_ReflectionIntensity=.75)
        if n=='Light' and d['m_GameObject']['fileID']==sun_go:
            d['m_Intensity']=1.35;d['m_Color']=color(1,.973,.925);d['m_Shadows']['m_Strength']=.55
        if n=='Transform' and d['m_GameObject']['fileID']==sun_go:
            x=math.radians(50)/2;y=math.radians(35)/2
            d['m_LocalRotation']=Flow(x=math.cos(y)*math.sin(x),y=math.sin(y)*math.cos(x),z=-math.sin(y)*math.sin(x),w=math.cos(y)*math.cos(x))
            d['m_LocalEulerAnglesHint']=vec(50,35,0)
        if n=='MonoBehaviour' and go_names.get(d['m_GameObject']['fileID'])=='AarisPlayer':d.update(minLookPitch=-70,maxLookPitch=70)
    write_yaml(root/SCENE,objects)
    for name,model in layout['models'].items():complete_prefab(root,name,model['prefab_scale'])
    player_path=root/'Assets/AarisWorld/Player/AarisPlayer.prefab';player=documents(player_path)
    next(d for c,i,n,d in player if n=='MonoBehaviour').update(minLookPitch=-70,maxLookPitch=70)
    write_yaml(player_path,player)
    sky=documents(root/SKY);material=next(d for c,i,n,d in sky if n=='Material')
    material.update(serializedVersion=8,m_ShaderKeywords='_SUNDISK_HIGH_QUALITY',m_ValidKeywords=['_SUNDISK_HIGH_QUALITY'],m_InvalidKeywords=[])
    material.update(m_Parent=ref(),m_ModifiedSerializedProperties=0,m_LockedProperties='')
    material['m_SavedProperties'].setdefault('m_Ints',[])
    for f in material['m_SavedProperties']['m_Floats']:
        if '_Exposure'in f:f['_Exposure']=1.15
    for f in material['m_SavedProperties']['m_Colors']:
        if '_SkyTint'in f:f['_SkyTint']=color(.5,.5,.5)
        if '_GroundColor'in f:f['_GroundColor']=color(.45,.47,.45)
    write_yaml(root/SKY,sky);save_json(layout_path,layout)
    report={'schema_version':1,'native_assets_materialized':False,'human_height_m':1.78,'residential_scale_target':HOME_SCALE,
        'residential_scales':{name:layout['models'][name]['prefab_scale'] for name in sorted(HOMES)},
        'residential_instances_scaled':sum(b['model']in HOMES for b in layout['buildings']),
        'complete_three_lod_prefabs':len(layout['models']),'clearances':gaps,
        'safe_spawn_m':spawn.tolist(),'spawn_yaw_degrees':spawn_yaw,'nearest_residential_footprint_m':nearest_home,
        'daylight':{'sun_elevation_degrees':50,'sun_intensity':1.35,'shadow_strength':.55,'sky_exposure':1.15,'maximum_upward_look_degrees':70},
        'graphics_resolution_reduced':False,'unity_editor_compilation_tested':False,'unity_render_tested':False,'android_device_tested':False}
    save_json(root/'Docs/WORLD_REVIEW_REPORT.json',report);return report

def save_native(root):
    import UnityPy
    from export_index import export
    from surfaces import rect
    root=Path(root);layout=json.loads((root/'Docs/MAP_LAYOUT.json').read_text())
    close_hashes={str(p.relative_to(root)):hashlib.sha256(p.read_bytes()).hexdigest() for p in (root/'Assets/AarisWorld/Models').glob('*/Geometry.asset')}
    assert len(close_hashes)==17
    report=json.loads((root/'Docs/WORLD_REVIEW_REPORT.json').read_text())
    target_path=root/'Docs/CYPRESS_SCALE_TARGETS.json'
    targets=json.loads(target_path.read_text()) if target_path.exists() else {'schema_version':1,'factor':CYPRESS_FACTOR,'instances':{}}
    assert targets['factor']==CYPRESS_FACTOR
    index=json.loads((root/'Docs/TERRAIN_INDEX.json').read_text());terrain=SimpleNamespace(data={},env={});hashes={};changed=Counter();height_grids={}
    map_report=json.loads((root/'Docs/MAP_BUILD_REPORT.json').read_text())
    for relative in map_report['terrain_files']:
        path=root/relative;raw=path.read_bytes();env=UnityPy.load(raw)
        obj=next(o for o in env.objects if o.type.name=='TerrainData');d=obj.read_typetree();db=d['m_DetailDatabase']
        tag=re.search(r'Terrain_(\d_\d)_',path.name).group(1);ix,iz=map(int,tag.split('_'));ox=(ix-2)*1000;oz=(iz-2)*1000
        protos=next(t for t in index['tiles'] if t['tile']==tag)['tree_and_rock_prototypes']
        before=copy.deepcopy(d);before_alpha={o.path_id:hashlib.sha256(o.get_raw_data()).hexdigest() for o in env.objects if o.type.name=='Texture2D'}
        for inst in db['m_TreeInstances']:
            proto=protos[inst['index']]
            if '/Cypress/' not in proto['path']:continue
            p=inst['position'];key=f"{tag}:{proto['guid']}:{ox+p['x']*1000:.3f}:{oz+p['z']*1000:.3f}"
            if key not in targets['instances']:
                targets['instances'][key]={'original_width':inst['widthScale'],'original_height':inst['heightScale'],
                    'width':float(np.float32(inst['widthScale']*CYPRESS_FACTOR)),'height':float(np.float32(inst['heightScale']*CYPRESS_FACTOR))}
            target=targets['instances'][key];inst['widthScale']=target['width'];inst['heightScale']=target['height'];changed[tag]+=1
        # The only permitted changes in the full TerrainData tree are selected instance scales.
        normalized=copy.deepcopy(d)
        for old,new in zip(before['m_DetailDatabase']['m_TreeInstances'],normalized['m_DetailDatabase']['m_TreeInstances']):
            new['widthScale']=old['widthScale'];new['heightScale']=old['heightScale']
        assert normalized==before,'Unexpected TerrainData change'
        obj.save_typetree(d);path.write_bytes(env.file.save())
        saved=UnityPy.load(path.read_bytes());saved_obj=next(o for o in saved.objects if o.type.name=='TerrainData');check=saved_obj.read_typetree()
        assert check==d,'Saved native terrain differs from the authored data'
        assert before_alpha=={o.path_id:hashlib.sha256(o.get_raw_data()).hexdigest() for o in saved.objects if o.type.name=='Texture2D'}
        height_grids[tag]=np.asarray(check['m_Heightmap']['m_Heights']).reshape(2049,2049).astype(float)/32766*800-120
        terrain.data[tag]=check;terrain.env[tag]=(saved,saved_obj)
        hashes[relative]={'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'size':path.stat().st_size}
    for k,b in enumerate(layout['buildings']):
        if b['model'] not in HOMES:continue
        q=b['aabb_xz_m'];xx,zz=np.meshgrid(np.linspace(q[0]-1,q[2]+1,9),np.linspace(q[1]-1,q[3]+1,9));yy=[]
        for x,z in zip(xx.ravel(),zz.ravel()):
            i=min(2,max(1,int((x+1000)//1000)+1));j=min(2,max(1,int((z+1000)//1000)+1));h=height_grids[f'{i}_{j}']
            fx=np.clip((x-(i-2)*1000)*2048/1000,0,2048);fz=np.clip((z-(j-2)*1000)*2048/1000,0,2048)
            a=min(int(fx),2047);c=min(int(fz),2047);dx=fx-a;dz=fz-c
            yy.append((h[c,a]*(1-dx)+h[c,a+1]*dx)*(1-dz)+(h[c+1,a]*(1-dx)+h[c+1,a+1]*dx)*dz)
        assert np.max(np.abs(np.asarray(yy)-b['ground_y_m']))<.03,('Scaled foundation leaves flat ground',b['name'])
        path=root/f'Assets/AarisWorld/Surfaces/Foundation_{k:03d}.asset';template=path.read_text();meta=Path(str(path)+'.meta');original_meta=meta.read_bytes()
        rect(path,(q[0]+q[2])/2,(q[1]+q[3])/2,q[2]-q[0]+2,q[3]-q[1]+2,b['ground_y_m']+.014,template);meta.write_bytes(original_meta)
    export(terrain,root/'Docs',root/'Tools/MapBuild')
    for name in ['MAP_BUILD_REPORT.json','NATIVE_TERRAIN_VERIFICATION.json']:
        path=root/'Docs'/name;data=json.loads(path.read_text());data['terrain_files']=hashes;save_json(path,data)
    plan_path=root/'Docs/FOREST_PLANTING_PLAN.json';plan=json.loads(plan_path.read_text())
    for p in plan['placements']:
        if '/Cypress/' not in p['path']:continue
        # Position/prototype keys use native precision rounded to a millimetre.
        proto=next(x for t in index['tiles'] for x in t['tree_and_rock_prototypes'] if x['path']==p['path'])
        x,z=p['position_xz_m'];key=f"{p['tile']}:{proto['guid']}:{x:.3f}:{z:.3f}"
        if key in targets['instances']:p['width_scale']=targets['instances'][key]['width'];p['height_scale']=targets['instances'][key]['height']
    save_json(plan_path,plan);save_json(target_path,targets)
    ground_path=root/'Docs/GROUND_REVIEW_REPORT.json';ground=json.loads(ground_path.read_text());ground['world_scale_integrated']=True
    ground['spawn_m']=report['safe_spawn_m'];save_json(ground_path,ground)
    x,spawn_y,z=report['safe_spawn_m'];i=min(2,max(1,int((x+1000)//1000)+1));j=min(2,max(1,int((z+1000)//1000)+1))
    h=height_grids[f'{i}_{j}'];fx=(x-(i-2)*1000)*2048/1000;fz=(z-(j-2)*1000)*2048/1000;a=int(fx);c=int(fz);dx=fx-a;dz=fz-c
    ground_y=(h[c,a]*(1-dx)+h[c,a+1]*dx)*(1-dz)+(h[c+1,a]*(1-dx)+h[c+1,a+1]*dx)*dz
    assert abs(spawn_y-ground_y-.085)<.001,'Village spawn is not on flat native ground'
    for tag,data in terrain.data.items():
        ti,tj=map(int,tag.split('_'))
        for tree in data['m_DetailDatabase']['m_TreeInstances']:
            p=tree['position'];assert math.hypot((ti-2)*1000+p['x']*1000-x,(tj-2)*1000+p['z']*1000-z)>3,'Spawn is blocked by a native tree/rock'
    assert all(hashlib.sha256((root/path).read_bytes()).hexdigest()==sha for path,sha in close_hashes.items())
    report.update(native_assets_materialized=True,cypress_instances_enlarged=sum(changed.values()),cypress_factor=CYPRESS_FACTOR,
        cypress_instances_by_tile=dict(changed),terrain_heights_details_prototypes_and_alpha_preserved=True,
        total_tree_count=sum(t['terrain_instances'].get('tree',0) for t in json.loads((root/'Docs/TERRAIN_INDEX.json').read_text())['tiles']),
        scaled_foundations_flat_ground_verified=True,spawn_native_ground_and_obstacles_verified=True,
        all_original_close_meshes_preserved=True,original_close_mesh_hashes=close_hashes)
    save_json(root/'Docs/WORLD_REVIEW_REPORT.json',report);print(json.dumps(report));return report

if __name__=='__main__':
    save_world(Path.cwd());save_native(Path.cwd())
