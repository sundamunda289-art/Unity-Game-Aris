"""Save mobile review settings and bounded, reusable battle-royale props.

Authoring only. Existing terrain, close meshes, textures, sky and lighting are
not regenerated. Input terrain bytes must match MAP_BUILD_REPORT first.
"""
from __future__ import annotations
import base64, copy, hashlib, json, math, re, shutil, sys, urllib.request
from pathlib import Path
import numpy as np
from PIL import Image
import trimesh
from native_assets import Flow, guid, material_asset, mesh_asset, meta, ref, vec, write_yaml
from native_scene import Scene
from ground_review import documents
from ground_native import simplify

SCENE = 'Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity'
PROPS = 'Assets/AarisWorld/Props'
SOURCES = Path('/tmp/aaris-prop-sources')

def download(url, path, expected=None):
    path=Path(path);path.parent.mkdir(parents=True,exist_ok=True)
    if not path.exists():
        req=urllib.request.Request(url,headers={'User-Agent':'AarisMapAssetImporter/1.0'})
        path.write_bytes(urllib.request.urlopen(req,timeout=45).read())
    raw=path.read_bytes()
    if expected:assert hashlib.md5(raw).hexdigest()==expected
    return raw

def source_textures(root,asset):
    data=json.loads(download('https://api.polyhaven.com/files/'+asset,SOURCES/(asset+'-files.json')))
    if asset=='rock_09':
        include=data['gltf']['1k']['gltf']['include']
        chosen={kind:next(v for k,v in include.items()if '_'+term+'_1k.'in k)for kind,term in [('Base','diff'),('Normal','nor_gl'),('ARM','arm')]}
    else:
        chosen={kind:data[key]['1k']['jpg']for kind,key in [('Base','Diffuse'),('Normal','nor_gl'),('ARM','arm')]}
    folder=root/PROPS/'Shared';folder.mkdir(parents=True,exist_ok=True);out={};source=[]
    for kind,d in chosen.items():
        b=download(d['url'],SOURCES/(asset+'_'+kind+'.jpg'),d.get('md5'))
        path=folder/(asset+'_'+kind+('.png'if kind=='ARM'else'.jpg'))
        if kind=='ARM':
            a=np.asarray(Image.open(SOURCES/(asset+'_'+kind+'.jpg')).convert('RGB'))
            # One shared URP texture: metal=R, AO=G, smoothness=A.
            rgba=np.zeros((*a.shape[:2],4),dtype='u1');rgba[:,:,0]=a[:,:,2];rgba[:,:,1]=a[:,:,0];rgba[:,:,3]=255-a[:,:,1]
            Image.fromarray(rgba).save(path)
        else:path.write_bytes(b)
        meta(path,texture={'normal':kind=='Normal','linear':kind!='Base'})
        # These NEW mobile props use their downloaded 1K source unchanged.
        m=Path(str(path)+'.meta');text=m.read_text().replace('maxTextureSize: 2048','maxTextureSize: 1024');m.write_text(text)
        out[kind]=guid(path);source.append({'url':d['url'],'download_sha256':hashlib.sha256(b).hexdigest(),'asset':path.relative_to(root).as_posix()})
    return out,source

def mesh_data(mesh):
    # Flat face normals suit fabricated beams; UVs are metre-scaled, not palette colors.
    t=mesh.triangles.reshape(-1,3);norm=np.repeat(mesh.face_normals,3,axis=0)
    axis=np.argmax(np.abs(norm),axis=1);uv=np.zeros((len(t),2))
    for i,axes in enumerate([(2,1),(0,2),(0,1)]):uv[axis==i]=t[axis==i][:,axes]
    return t,norm,uv,np.arange(len(t),dtype='u4')

def box_mesh(center,size,rotation=0):
    m=trimesh.creation.box(extents=size)
    if rotation:m.apply_transform(trimesh.transformations.rotation_matrix(math.radians(rotation),[1,0,0]))
    m.apply_translation(center);return m

def beam(a,b,width=.09):
    a=np.asarray(a);b=np.asarray(b);m=trimesh.creation.box(extents=[width,width,np.linalg.norm(b-a)])
    transform=trimesh.geometry.align_vectors([0,0,1],b-a);transform[:3,3]=(a+b)/2;m.apply_transform(transform);return m

def tower_geometry():
    parts=[];boxes=[]
    def box(center,size,angle=0,collision=True):
        parts.append(box_mesh(center,size,angle))
        if collision:boxes.append({'center':list(center),'size':list(size),'angle_x':angle})
    for x in [-2.7,2.7]:
        for z in [-4.1,4.1]:
            box((x,6,z),(.19,12,.19),collision=True)
            box((x,-.08,z),(.65,.28,.65),collision=True)
    # 4 switchback flights: 3 m rise / 6 m run; 0.167 m visible risers.
    # Physical ramps under the treads prevent per-step CharacterController jitter.
    run=6.;rise=3.;angle=math.degrees(math.atan2(rise,run))
    for flight in range(4):
        direction=1 if flight%2==0 else -1;x=-1.15 if flight%2==0 else 1.15;base=flight*rise
        for step in range(18):
            z=direction*(-3+(step+.5)*run/18);y=base+(step+1)*rise/18-.055
            box((x,y,z),(1.7,.11,run/18+.01),collision=False)
        boxes.append({'center':[x,base+rise/2-.10,0],'size':[1.7,.10,math.hypot(run,rise)],'angle_x':-direction*angle})
        for dx in [-.86,.86]:
            parts.append(beam((x+dx,base+1.05,-direction*3),(x+dx,base+rise+1.05,direction*3),.07))
        # End landings and side walkways leave the ascending stairwell open.
        dest=direction*3.65;y=base+rise
        box((0,y-.08,dest),(5.6,.16,1.3))
        for side in [-1,1]:box((side*2.35,y-.08,0),(.65,.16,6.0))
        box((0,y+1.05,direction*4.25),(5.6,.09,.09))
        for side in [-1,1]:box((side*2.8,y+1.05,0),(.09,.09,8.5))
        # Guard the opposite end too; the top keeps an open exit from its flight.
        box((0,y+1.05,-direction*4.25),(5.6,.09,.09))
    # A useful upper deck occupies the free lane, leaving a real stair opening.
    box((-1.1,11.92,0),(2.1,.16,6))
    for y in [0,3,6,9]:
        for z in [-4.1,4.1]:parts.append(beam((-2.7,y,z),(2.7,y+3,z),.09))
    return trimesh.util.concatenate(parts),boxes,{'standing_height_m':12,'overall_height_m':13.1,'stair_width_m':1.7,'riser_m':rise/18,'tread_m':run/18,'ramp_slope_degrees':angle,'stairwell_open':True}

def wreck_geometry():
    # Original non-drivable wreck, no car physics, sound, interior or large texture set.
    # Rounded body sections, dented front, collapsed roof and exposed/rusted metal.
    sections=[(-2.15,.68,.50),(-1.85,.88,.70),(-1.05,.92,.80),(.85,.90,.81),(1.65,.84,.64),(2.0,.62,.48)]
    vertices=[]
    for z,w,top in sections:
        for x,y in [(-w*.86,.32),(-w,.48),(-w*.93,top),(-w*.55,top+.05),(w*.55,top+.05),(w*.93,top),(w,.48),(w*.86,.32)]:
            front=max(0,(z-.8)/1.2);vertices.append((x+front*.07*math.sin(x*5),y-front*.13,z-front*.25))
    faces=[]
    for k in range(len(sections)-1):
        for i in range(8):a=k*8+i;b=k*8+(i+1)%8;c=b+8;d=a+8;faces.extend([(a,b,c),(a,c,d)])
    faces.extend([(0,i+1,i)for i in range(1,7)]);last=(len(sections)-1)*8;faces.extend([(last,last+i,last+i+1)for i in range(1,7)])
    parts=[trimesh.Trimesh(vertices=vertices,faces=faces,process=False)]
    # Cabin shell, heavily squashed compared with a functioning sedan.
    v=[[-.76,.79,-.96],[.76,.79,-.96],[-.58,1.30,-.51],[.58,1.28,-.51],[-.58,1.27,.48],[.58,1.22,.48],[-.76,.79,.96],[.76,.79,.96]]
    f=[[0,1,3],[0,3,2],[2,3,5],[2,5,4],[4,5,7],[4,7,6],[0,2,4],[0,4,6],[1,7,5],[1,5,3]]
    parts.append(trimesh.Trimesh(vertices=v,faces=f,process=False))
    for x in [-.87,.87]:
        for z in [-1.33,1.28]:
            tire=trimesh.creation.cylinder(radius=.31,height=.19,sections=20)
            tire.apply_transform(trimesh.transformations.rotation_matrix(math.pi/2,[0,1,0]));tire.apply_translation((x,.31,z));parts.append(tire)
    return trimesh.util.concatenate(parts),[{'center':[0,.62,0],'size':[1.8,.70,4.2],'angle_x':0},{'center':[0,1.04,0],'size':[1.45,.55,1.9],'angle_x':0}]

def prefab(root,name,template,mesh,material,boxes):
    folder=root/PROPS/name;folder.mkdir(parents=True,exist_ok=True)
    if hasattr(mesh.visual,'uv') and mesh.visual.uv is not None:
        p,n,u,ind=mesh.vertices,mesh.vertex_normals,mesh.visual.uv,mesh.faces.reshape(-1)
    else:
        mesh.fix_normals(multibody=True)
        p,n,u,ind=mesh_data(mesh)
    rec=mesh_asset(folder/'Geometry.asset',p,n,u,ind,template,name=name)
    levels=[rec]
    for level in [1,2]:
        data=simplify((folder/'Geometry.asset').read_text(),level)
        pp,nn,uu,ii,tt,groups=data
        levels.append(mesh_asset(folder/f'Geometry_LOD{level}.asset',pp,nn,uu,ii,template,name=name+f'_LOD{level}',tangent=tt,groups=groups))
    scene=Scene();go,tr=scene.model(name,rec['guid'],[material],bounds=rec['bounds'],collider=False)
    lod=next(d for c,i,n,d in scene.objects if n=='LODGroup');r=scene.renderers[0]
    lod['m_LODs']=[{'screenRelativeHeight':.22,'fadeTransitionWidth':.12,'renderers':[{'renderer':ref(r)}]}]
    for level,threshold in [(1,.07),(2,.0025)]:
        child,_=scene.model(name+f'_LOD{level}',levels[level]['guid'],[material],parent=tr,collider=False,lod=False)
        lod['m_LODs'].append({'screenRelativeHeight':threshold,'fadeTransitionWidth':.12,'renderers':[{'renderer':ref(scene.renderers[-1])}]})
    for k,b in enumerate(boxes):
        cgo,ctr,gd=scene.object('Solid_'+str(k),position=b['center'],parent=tr)
        a=math.radians(b['angle_x'])/2;scene.transforms[ctr]['m_LocalRotation']=Flow(x=math.sin(a),y=0,z=0,w=math.cos(a))
        scene.component(cgo,gd,65,'BoxCollider',{'m_Material':ref(),'m_IsTrigger':0,'m_Enabled':1,'serializedVersion':3,'m_Size':vec(*b['size']),'m_Center':vec()})
    for c,i,n,d in scene.objects:
        if n=='GameObject':d['m_StaticEditorFlags']=d.get('m_StaticEditorFlags',0)&~4
    scene.save(folder/(name+'.prefab'),prefab=True);meta(folder,kind='folder')
    return {'name':name,'folder':folder,'root_transform':tr,'objects':scene.objects,'triangles':[r['triangles']for r in levels],'bounds':rec['bounds']}

def height_grid(root):
    import UnityPy,gc
    heights={};report=json.loads((root/'Docs/MAP_BUILD_REPORT.json').read_text())
    for name,e in report['terrain_files'].items():
        p=root/name;raw=p.read_bytes();assert hashlib.sha256(raw).hexdigest()==e['sha256']
        env=UnityPy.load(raw);d=next(o for o in env.objects if o.type.name=='TerrainData').read_typetree()
        tag=re.search(r'Terrain_(\d_\d)_',p.name)[1]
        heights[tag]=np.asarray(d['m_Heightmap']['m_Heights'],dtype=float).reshape(2049,2049)/32766*800-120
        del env,d,raw;gc.collect()
    return heights

def sample(h,x,z):
    i=1 if x<0 else 2;j=1 if z<0 else 2;ix=np.clip((x-(i-2)*1000)*2048/1000,0,2048);iz=np.clip((z-(j-2)*1000)*2048/1000,0,2048)
    a=min(int(ix),2047);b=min(int(iz),2047);fx=ix-a;fz=iz-b;v=h[f'{i}_{j}']
    return float((v[b,a]*(1-fx)+v[b,a+1]*fx)*(1-fz)+(v[b+1,a]*(1-fx)+v[b+1,a+1]*fx)*fz)

def clear(layout,x,z,hx,hz,used):
    rect=[x-hx,z-hz,x+hx,z+hz]
    for a in [b['aabb_xz_m']for b in layout['buildings']]+used:
        if not(rect[2]+2<a[0]or rect[0]-2>a[2]or rect[3]+2<a[1]or rect[1]-2>a[3]):return False
    for s in layout['vehicle_spawns']:
        px,_,pz=s['position_m']
        # Conservative full exit corridor bounding radius, independent of yaw.
        if abs(px-x)<hx+9 and abs(pz-z)<hz+9:return False
    for r in layout['roads']:
        for a,b in zip(r['points'],r['points'][1:]):
            a=np.array(a)[[0,2]];b=np.array(b)[[0,2]];p=np.array([x,z]);v=b-a;t=np.clip(np.dot(p-a,v)/max(np.dot(v,v),1e-8),0,1)
            if np.linalg.norm(p-a-t*v)<math.hypot(hx,hz)+r['width']/2+1:return False
    return True

def append_prefab(scene,asset,name,position,yaw,parent):
    remap={i:scene.next_id+k for k,(c,i,n,d)in enumerate(asset['objects'])};scene.next_id+=len(remap)+10
    def convert(v):
        if isinstance(v,list):return [convert(q)for q in v]
        if isinstance(v,dict):
            out=type(v)((k,convert(q))for k,q in v.items())
            if out.get('fileID')in remap and 'guid'not in out:out['fileID']=remap[out['fileID']]
            return out
        return v
    for c,i,n,d in asset['objects']:
        d=convert(copy.deepcopy(d));ni=remap[i]
        if n=='GameObject'and d['m_Name']==asset['name']:d['m_Name']=name
        if n=='Transform':
            if i==asset['root_transform']:
                d['m_LocalPosition']=vec(*position);d['m_LocalRotation']=Flow(x=0,y=math.sin(math.radians(yaw)/2),z=0,w=math.cos(math.radians(yaw)/2));d['m_Father']=ref(parent)
                scene.transforms[parent]['m_Children'].append(ref(ni))
            scene.transforms[ni]=d
        scene.objects.append((c,ni,n,d))

def upgrade(root):
    root=Path(root);layout=json.loads((root/'Docs/MAP_LAYOUT.json').read_text());h=height_grid(root)
    template=base64.b64decode((root/'Tools/MapBuild/mesh_template.b64').read_text()).decode()
    sources=[];assets={}
    for asset in ['rock_09','rusty_metal_02']:
        tex,src=source_textures(root,asset);sources+=src
        matpath=root/PROPS/'Shared'/(asset+'.mat')
        mat=material_asset(matpath,asset,metallic=1,smoothness=1,textures={'_BaseMap':tex['Base'],'_BumpMap':tex['Normal'],'_MetallicGlossMap':tex['ARM'],'_OcclusionMap':tex['ARM']},extra={'_Cull':2,'_BumpScale':1})
        assets[asset]=mat
    mesh,boxes,tower_spec=tower_geometry();tower=prefab(root,'ClimbableTower',template,mesh,assets['rusty_metal_02'],boxes)
    mesh,boxes=wreck_geometry();wreck=prefab(root,'StaticWreck',template,mesh,assets['rusty_metal_02'],boxes)
    info=json.loads(download('https://api.polyhaven.com/files/rock_09',SOURCES/'rock_09-files.json'))['gltf']['1k']['gltf']
    gltf_path=SOURCES/'rock_09_1k.gltf';download(info['url'],gltf_path,info['md5'])
    # trimesh only needs the source mesh buffer; material textures are converted separately.
    for name,item in info['include'].items():download(item['url'],SOURCES/name,item['md5'])
    rock_scene=trimesh.load(gltf_path,force='scene');rock=rock_scene.to_geometry();rock.apply_scale(24)
    rock.apply_translation([-(rock.bounds[0,0]+rock.bounds[1,0])/2,-rock.bounds[0,1],-(rock.bounds[0,2]+rock.bounds[1,2])/2])
    bounds=rock.bounds;size=bounds[1]-bounds[0];rock_boxes=[{'center':((bounds[1]+bounds[0])/2).tolist(),'size':(size*.85).tolist(),'angle_x':0}]
    stone=prefab(root,'RockCover',template,rock,assets['rock_09'],rock_boxes)
    original=documents(root/SCENE);scene=Scene();scene.objects=[o for o in original if o[2]!='SceneRoots'and o[1]<400000]
    scene.transforms={i:d for c,i,n,d in scene.objects if n=='Transform'}
    for t in scene.transforms.values():t['m_Children']=[r for r in t['m_Children']if r['fileID']<400000]
    scene.next_id=400000;_,props,_=scene.object('BattleRoyaleProps');used=[];placements=[]
    # Reserve sparse, full-resolution-flat district margins. Existing roads and bays stay clear.
    for district_name in ['FreightDistrict','SouthernAirfield','NorthernRidgeTown','ValleySettlement']:
        d=next(d for d in layout['districts']if d['name']==district_name);candidates=[]
        for x in np.arange(d['x']-d['width']/2+8,d['x']+d['width']/2-8,5):
            for z in np.arange(d['z']-d['depth']/2+8,d['z']+d['depth']/2-8,5):
                if not clear(layout,x,z,3.4,4.5,used):continue
                samples=[sample(h,x+dx,z+dz)for dx in [-3.4,0,3.4]for dz in [-4.5,0,4.5]]
                if max(samples)-min(samples)>.03:continue
                candidates.append((abs(x-d['x'])+abs(z-d['z']),x,z,samples[4]))
        assert candidates,('No flat, unobstructed tower margin',district_name)
        _,x,z,y=max(candidates);name=district_name+'_ClimbableTower'
        append_prefab(scene,tower,name,[x,y,z],0,props);used.append([x-3.4,z-4.5,x+3.4,z+4.5]);placements.append({'name':name,'kind':'tower','position_m':[x,y,z],'yaw':0,'aabb_xz_m':used[-1],**tower_spec})
    # A few static wrecks, not usable vehicles, outside traffic and future vehicle exits.
    for district_name in ['FreightDistrict','EasternPort','SouthernAirfield','ClockTowerTown','OldVillage','CoastalHamlet']:
        d=next(d for d in layout['districts']if d['name']==district_name);found=False
        for x in np.arange(d['x']-d['width']/2+8,d['x']+d['width']/2-8,6):
            if found:break
            for z in np.arange(d['z']-d['depth']/2+8,d['z']+d['depth']/2-8,6):
                if not clear(layout,x,z,2.5,2.5,used):continue
                samples=[sample(h,x+dx,z+dz)for dx in [-2,2]for dz in [-2,2]]
                if max(samples)-min(samples)>.08:continue
                name=district_name+'_StaticWreck';y=sample(h,x,z)
                append_prefab(scene,wreck,name,[x,y,z],17,props);used.append([x-2.5,z-2.5,x+2.5,z+2.5]);placements.append({'name':name,'kind':'static_wreck','position_m':[x,y,z],'yaw':17,'aabb_xz_m':used[-1]});found=True;break
        assert found,('No safe wreck location',district_name)
    rng=np.random.default_rng(20261103);attempts=0
    while sum(p['kind']=='rock_cover'for p in placements)<24 and attempts<6000:
        attempts+=1;x,z=rng.uniform(-860,860,2);y=sample(h,x,z)
        if math.hypot(x+496,z+212)<16:continue
        if y < -33 or not clear(layout,x,z,2,2,used):continue
        # Avoid native trees/rocks by using their full-resolution instance positions below.
        tag=f'{1 if x<0 else 2}_{1 if z<0 else 2}'
        if 'native_points'not in locals():
            import UnityPy
            native_points=[]
            for path in json.loads((root/'Docs/MAP_BUILD_REPORT.json').read_text())['terrain_files']:
                env=UnityPy.load(str(root/path));td=next(o for o in env.objects if o.type.name=='TerrainData').read_typetree();i,j=map(int,re.search(r'Terrain_(\d)_(\d)_',path).groups())
                native_points.extend([[(i-2)*1000+t['position']['x']*1000,(j-2)*1000+t['position']['z']*1000]for t in td['m_DetailDatabase']['m_TreeInstances']])
            native_points=np.array(native_points)
        if np.linalg.norm(native_points-[x,z],axis=1).min()<6:continue
        samples=[sample(h,x+dx,z+dz)for dx in [-1,1]for dz in [-1,1]]
        if max(samples)-min(samples)>1.0:continue
        yaw=float(rng.uniform(0,360));name='RockCover_'+str(len(placements));append_prefab(scene,stone,name,[x,min(samples)-.12,z],yaw,props)
        used.append([x-2,z-2,x+2,z+2]);placements.append({'name':name,'kind':'rock_cover','position_m':[x,min(samples)-.12,z],'yaw':yaw,'aabb_xz_m':used[-1]})
    assert sum(p['kind']=='rock_cover'for p in placements)==24
    # Preserve mesh sharing instead of baking another geometry copy for every repeated house.
    names={i:d['m_Name']for c,i,n,d in scene.objects if n=='GameObject'}
    changed_static=0
    for c,i,n,d in scene.objects:
        if n=='GameObject'and any(b['name']in d['m_Name']for b in layout['buildings']if b['model']in ['a_frame_cabin','bungalow_house','detached_two_storey_house','garage_apartment','pitched_roof_cottage','row_three_townhouses','two_bedroom_garage_house']):
            changed_static+=1;d['m_StaticEditorFlags']&=~4
    for name in ['a_frame_cabin','bungalow_house','detached_two_storey_house','garage_apartment','pitched_roof_cottage','row_three_townhouses','two_bedroom_garage_house']:
        p=root/f'Assets/AarisWorld/Models/{name}/{name}.prefab';objects=documents(p)
        for c,i,n,d in objects:
            if n=='GameObject':d['m_StaticEditorFlags']&=~4
        write_yaml(p,objects)
    player_tr=next(i for i,d in scene.transforms.items()if names.get(d['m_GameObject']['fileID'])=='AarisPlayer')
    building_names={b['name']for b in layout['buildings']}
    for c,i,n,d in scene.objects:
        if n=='MeshCollider'and names.get(d['m_GameObject']['fileID'])in building_names:
            # IMPORTANT: Do not serialize distant houses with m_Enabled:0.
            # AarisNearbyColliders.Awake() skips already-disabled colliders
            # to protect intentionally disabled content. The old build step
            # made ~56 visible houses permanent ghosts: none could stream in.
            # Serialize solid authored meshes enabled. The nearby runtime
            # component disables distant collisions and activates local ones.
            # This changes NO renderer, mesh, terrain or mobile frame budget.
            d['m_Enabled']=1
    initially_disabled=0
    # Saved service bindings; nothing reconstructs terrain, buildings or props at scene load.
    go,tr,gd=scene.object('MobileRuntime',static=False)
    terrains=[i for c,i,n,d in scene.objects if n=='Terrain'];colliders=[i for c,i,n,d in scene.objects if n in ('MeshCollider','BoxCollider')]
    nearby=scene.component(go,gd,114,'MonoBehaviour',{'m_Enabled':1,'m_EditorHideFlags':0,'m_Script':ref(11500000,guid('Assets/AarisWorld/Scripts/AarisNearbyColliders.cs'),3),'m_Name':'','m_EditorClassIdentifier':'Assembly-CSharp::Aaris.MapReview.AarisNearbyColliders','target':ref(player_tr),'obstacles':[ref(i)for i in colliders],'activationDistance':55,'cellSize':250})
    scene.component(go,gd,114,'MonoBehaviour',{'m_Enabled':1,'m_EditorHideFlags':0,'m_Script':ref(11500000,guid('Assets/AarisWorld/Scripts/AarisMobileRuntime.cs'),3),'m_Name':'','m_EditorClassIdentifier':'Assembly-CSharp::Aaris.MapReview.AarisMobileRuntime','terrains':[ref(i)for i in terrains],'releaseTerrainPaintCpuCopies':1,'targetFrameRate':60})
    player=next(d for c,i,n,d in scene.objects if n=='MonoBehaviour'and d.get('m_EditorClassIdentifier','').endswith('.AarisThirdPersonPlayer'));player['nearbyColliders']=ref(nearby)
    scene_meta=Path(str(root/SCENE)+'.meta').read_text();scene.save(root/SCENE);Path(str(root/SCENE)+'.meta').write_text(scene_meta)
    for name in ['AarisMobileRuntime','AarisNearbyColliders']:
        p=root/f'Assets/AarisWorld/Scripts/{name}.cs';Path(str(p)+'.meta').write_text('fileFormatVersion: 2\nguid: '+guid(p)+'\nMonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {fileID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n')
    settings=root/'ProjectSettings/ProjectSettings.asset';s=settings.read_text().replace('bundleVersion: 0.2.3','bundleVersion: 0.3.0').replace('AndroidBundleVersionCode: 5','AndroidBundleVersionCode: 6')
    if 'm_BuildTargetGraphicsAPIs:'not in s:s+='  m_BuildTargetGraphicsAPIs:\n  - m_BuildTarget: AndroidPlayer\n    m_APIs: 0b000000\n    m_Automatic: 0\n'
    settings.write_text(s)
    meta(root/PROPS,kind='folder');meta(root/PROPS/'Shared',kind='folder')
    output={'source_commit':'ae68166e65473f8b5c8181dcc1fd4b79b9a9d679','terrain_files_unchanged':True,'lighting_and_existing_visual_assets_unchanged':True,'existing_world_size_m':[2000,2000],
        'terrain_cpu_paint_bytes_eligible_for_release':8*22369620,'cpu_paint_release':'Android-only, read-only APK, one original mip chain per frame; Editor files unchanged. Requires device verification.',
        'residential_static_batch_flags_cleared':changed_static,'distant_building_colliders_disabled_at_start':initially_disabled,'nearby_static_collider_count':len(colliders),'nearby_activation_distance_m':55,'native_terrain_and_tree_colliders_preserved':True,
        'collision_spatial_cell_size_m':250,'full_terrain_asset_streaming_enabled':False,'target_fps':60,'fps_target_is_not_measured_fps':True,'graphics_api':'OpenGLES3','texture_compression':'ETC2','placements':placements,
        'models':{a['name']:{'triangles':a['triangles'],'reusable_prefab':f"{PROPS}/{a['name']}/{a['name']}.prefab"}for a in [tower,wreck,stone]},
        'new_asset_sources':sources,'licenses':{'rock_09':'CC0-1.0, Poly Haven / Jenelle van Heerden','rusty_metal_02':'CC0-1.0, Poly Haven / Rob Tuytel','ClimbableTower':'Original project geometry','StaticWreck':'Original project geometry; decorative only'},
        'unity_editor_compiled':False,'unity_render_tested':False,'android_device_tested':False,'crash_root_cause_confirmed':False}
    (root/'Docs/MOBILE_REVIEW_REPORT.json').write_text(json.dumps(output,indent=2)+'\n');print(json.dumps({k:v for k,v in output.items()if k not in ['placements','new_asset_sources']},indent=2))
    return output

if __name__=='__main__':upgrade(Path.cwd())
