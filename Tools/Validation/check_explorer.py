"""Validate saved ground gameplay, forest clearances and references; not a playtest."""
import ast,copy,hashlib,json,math,re,sys
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

def validate_references(objects, path):
    ids={i for c,i,n,d in objects}
    assert len(ids)==len(objects),('Duplicate saved object ID',path)
    for c,i,n,d in objects:
        for reference in refs(d):
            if reference['fileID'] and not reference.get('guid'):
                assert reference['fileID'] in ids,(path,n,i,'Missing local reference',reference)

class ResolvedScene:
    """Read-only view of native YAML; synthesized IDs are scoped to this file."""
    def __init__(self, objects, path):
        self.objects=objects;self.path=path
        validate_references(objects,path)
        self.byid={i:(c,n,d)for c,i,n,d in objects}
        self.names={i:d['m_Name']for c,i,n,d in objects if c==1}
        self.transforms={i:d for c,i,n,d in objects if c==4}
        self.named={}
        for identity,transform in self.transforms.items():
            owner=transform['m_GameObject']['fileID']
            assert owner in self.names,(path,'Transform has no GameObject',identity)
            self.named.setdefault(self.names[owner],[]).append(identity)
            children=[r['fileID']for r in transform['m_Children']]
            assert len(children)==len(set(children)),(path,'Duplicate child',identity)
            for child in children:
                assert child in self.transforms and self.transforms[child]['m_Father']['fileID']==identity,\
                    (path,'Transform parent/child mismatch',identity,child)
            parent=transform['m_Father']['fileID']
            if parent:
                assert parent in self.transforms and identity in [r['fileID']for r in self.transforms[parent]['m_Children']],\
                    (path,'Missing reciprocal parent/child link',identity,parent)
        roots=[d for c,i,n,d in objects if n=='SceneRoots']
        if roots:
            assert len(roots)==1,(path,'Duplicate SceneRoots')
            recorded=[r['fileID']for r in roots[0]['m_Roots']]
            actual={i for i,t in self.transforms.items()if t['m_Father']['fileID']==0}
            assert len(recorded)==len(set(recorded)) and set(recorded)==actual,\
                (path,'SceneRoots differ from resolved root transforms')
        self._matrices={}

    def named_transform(self, name):
        matches=self.named.get(name,[])
        assert len(matches)==1,(self.path,'Missing/duplicate named transform',name,len(matches))
        return matches[0]

    def root_names(self):
        return [self.names[t['m_GameObject']['fileID']]for t in self.transforms.values()
                if t['m_Father']['fileID']==0]

    def matrix(self, identity, visiting=None):
        if identity in self._matrices:return self._matrices[identity]
        visiting=set()if visiting is None else visiting
        assert identity not in visiting,(self.path,'Transform cycle',identity)
        visiting.add(identity);t=self.transforms[identity]
        value=transform_matrix(t['m_LocalPosition'],t['m_LocalRotation'],t['m_LocalScale'])
        parent=t['m_Father']['fileID']
        if parent:value=self.matrix(parent,visiting)@value
        visiting.remove(identity);self._matrices[identity]=value
        return value

def transform_matrix(position, rotation, scale):
    x,y,z,w=(float(rotation[k])for k in ('x','y','z','w'))
    norm=x*x+y*y+z*z+w*w
    assert math.isfinite(norm) and abs(norm-1)<1e-5,('Invalid saved quaternion',rotation)
    norm=math.sqrt(norm);x,y,z,w=x/norm,y/norm,z/norm,w/norm
    value=np.eye(4)
    value[:3,:3]=np.array([[1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w)],
        [2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w)],
        [2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y)]])@np.diag([scale[k]for k in ('x','y','z')])
    value[:3,3]=[position[k]for k in ('x','y','z')]
    assert np.isfinite(value).all(),'Nonfinite saved transform'
    return value

class NativeSceneResolver:
    """Resolve the project's flat native prefabs without hiding unsupported edits.

    Unity saves root references as PrefabInstance IDs and may serialize only a
    stripped collider used by another component. The referenced prefab supplies
    its GameObjects, transforms and LODs. Source IDs repeat between instances;
    preserve stripped scene IDs and give every other copied object a fresh ID.
    """
    def __init__(self, root):
        self.root=Path(root);self.prefabs={};self.cached={}
        for meta in (self.root/'Assets').rglob('*.prefab.meta'):
            guid=yaml.safe_load(meta.read_text())['guid']
            assert guid not in self.prefabs,('Duplicate prefab GUID',guid)
            self.prefabs[guid]=meta.with_suffix('')

    def source(self, guid):
        assert guid in self.prefabs,('Unresolved source prefab GUID',guid)
        if guid not in self.cached:
            path=self.prefabs[guid];objects=documents(path)
            validate_references(objects,path)
            assert all(c!=1001 for c,i,n,d in objects),('Unsupported nested prefab',path)
            self.cached[guid]=objects
        return self.cached[guid]

    def resolve(self, path):
        path=Path(path);raw=documents(path);validate_references(raw,path)
        ids={i for c,i,n,d in raw};stripped={int(i)for i in re.findall(
            r'(?m)^--- !u!\d+ &(-?\d+) stripped$',path.read_text())}
        instances={i:d for c,i,n,d in raw if c==1001};aliases={}
        for c,i,n,d in raw:
            if i not in stripped:continue
            instance=d['m_PrefabInstance']['fileID'];source=d['m_CorrespondingSourceObject']
            assert instance in instances,(path,'Stripped object lacks instance',i)
            guid=instances[instance]['m_SourcePrefab']['guid']
            assert source.get('guid')==guid,(path,'Stripped source GUID mismatch',i)
            key=(instance,source['fileID'])
            assert key not in aliases,(path,'Duplicate stripped source',key)
            aliases[key]=(i,c,n)
        next_id=min(ids|{0})-1;expanded=[];instance_roots={};consumed=set()
        for instance,data in instances.items():
            guid=data['m_SourcePrefab']['guid'];source={i:(c,n,d)for c,i,n,d in self.source(guid)}
            modification=data['m_Modification']
            for key in ('m_AddedComponents','m_AddedGameObjects','m_RemovedGameObjects'):
                assert not modification.get(key),(path,'Unsupported prefab edit',instance,key)
            removed=set()
            for reference in modification.get('m_RemovedComponents',[]):
                identity=reference['fileID']
                assert reference.get('guid')==guid and identity in source,\
                    (path,'Unresolved removed component',instance,reference)
                assert source[identity][0]in (64,65,135,136,143,146,154),\
                    ('Visual prefab override may remove collision only',reference)
                assert identity not in removed,('Duplicate removed component',reference)
                removed.add(identity)
            copies={i:(c,n,copy.deepcopy(d))for i,(c,n,d)in source.items()if i not in removed}
            for c,n,d in copies.values():
                if c==1:d['m_Component']=[r for r in d['m_Component']if r['component']['fileID']not in removed]
            for change in modification['m_Modifications']:
                target=change['target'];identity=target['fileID'];property_path=change['propertyPath']
                assert target.get('guid')==guid and identity in copies,\
                    (path,'Unresolved override target',instance,target)
                assert not change['objectReference']['fileID']and not change['objectReference'].get('guid'),\
                    (path,'Unsupported object-reference override',instance,property_path)
                c,n,d=copies[identity]
                if c==1 and property_path in ('m_Name','m_StaticEditorFlags'):
                    assert property_path in d,(path,'Unknown GameObject property',property_path)
                    d[property_path]=str(change['value'])if property_path=='m_Name'else int(change['value'])
                elif c==4 and re.fullmatch(r'm_Local(?:Position|Scale|Rotation|EulerAnglesHint)\.[xyzw]',property_path):
                    field,axis=property_path.split('.')
                    assert field in d and axis in d[field],(path,'Unknown transform property',property_path)
                    value=float(change['value']);assert math.isfinite(value),'Nonfinite prefab override'
                    d[field][axis]=value
                else:raise AssertionError((path,'Unsupported prefab override',instance,property_path))
            mapping={}
            for identity,(c,n,d)in copies.items():
                alias=aliases.get((instance,identity))
                if alias:
                    assert alias[1:]==(c,n),(path,'Stripped type differs from source',alias)
                    mapping[identity]=alias[0];consumed.add(alias[0])
                else:mapping[identity]=next_id;next_id-=1
            roots=[i for i,(c,n,d)in copies.items()if c==4 and d['m_Father']['fileID']==0]
            assert len(roots)==1,(path,'Prefab needs exactly one transform root',instance)
            root=roots[0];instance_roots[instance]=mapping[root]
            def remap(value):
                if isinstance(value,list):return [remap(v)for v in value]
                if not isinstance(value,dict):return value
                if 'fileID'in value and value['fileID']and not value.get('guid'):
                    assert value['fileID']in mapping,(path,'Reference to missing/removed prefab object',instance,value)
                    return dict(value,fileID=mapping[value['fileID']])
                return {k:remap(v)for k,v in value.items()}
            for identity,(c,n,d)in copies.items():
                d=remap(d)
                if 'm_CorrespondingSourceObject'in d:
                    d['m_CorrespondingSourceObject']={'fileID':identity,'guid':guid,'type':3}
                    d['m_PrefabInstance']={'fileID':instance}
                if identity==root:d['m_Father']=copy.deepcopy(modification['m_TransformParent'])
                expanded.append((c,mapping[identity],n,d))
        assert consumed==stripped,(path,'Unresolved/removed stripped objects',stripped-consumed)
        objects=[(c,i,n,copy.deepcopy(d))for c,i,n,d in raw if i not in stripped]+expanded
        for c,i,n,d in objects:
            if c==4:
                parent=d['m_Father'];parent['fileID']=instance_roots.get(parent['fileID'],parent['fileID'])
                for child in d['m_Children']:child['fileID']=instance_roots.get(child['fileID'],child['fileID'])
            if n=='SceneRoots':
                for root in d['m_Roots']:root['fileID']=instance_roots.get(root['fileID'],root['fileID'])
        return ResolvedScene(objects,path)

def airy_house_contract(root):
    """Use the existing saved authoring audit, never invent replacement geometry."""
    report=json.loads((root/'Docs/AIRY_HOUSE_AUDIT.json').read_text())
    houses={row['name']:row for row in report['houses']}
    assert len(houses)==len(report['houses'])==report['total_houses']==128
    assert sum('chunk'in row for row in houses.values())==report['native_before']==91
    tree=ast.parse((root/'Tools/MapBuild/bake_airy_houses.py').read_text())
    mappings=[ast.literal_eval(node.value)for node in tree.body if isinstance(node,ast.Assign)
              and any(isinstance(target,ast.Name)and target.id=='MODEL_TYPE'for target in node.targets)]
    assert len(mappings)==1 and len(mappings[0])==14,'Missing authored replacement-model mapping'
    return report,houses,mappings[0]

def assert_lod_thresholds(lod, expected, context):
    actual=[float(level['screenRelativeHeight'])for level in lod['m_LODs']]
    assert len(actual)==len(expected)and all(math.isclose(a,b,rel_tol=0,abs_tol=1e-7)
        for a,b in zip(actual,expected)),(context,'Authored LOD thresholds changed',actual,expected)

# Exact MonoScript identities observed in Unity6000.3.26f1's resolved RP17.3.0
# packages. Clean source CI has no Library/PackageCache; this narrow snapshot
# verifies the declared metadata contract, not the availability of package bytes.
SCENE_PACKAGE_ASSETS={
    '172515602e62fb746b5d573b38a5fe58':('com.unity.render-pipelines.core','17.3.0',
        'Runtime/Volume/Volume.cs','8c0638aebb71f29a4d5f853c4c09529dec38f9a91178007a7f6dc8f7eae86d0a'),
    'a79441f348de89743a2939f4d699eac1':('com.unity.render-pipelines.universal','17.3.0',
        'Runtime/UniversalAdditionalCameraData.cs','073ed9d37f3742da3b73a364f7b721cc7f9ee74c4873c0385068be8da7090633'),
}

def resolve_package_scene_guids(root, required):
    """Resolve declared package assets without exempting unknown GUIDs."""
    if not required:return []
    declared=json.loads((root/'Packages/manifest.json').read_text())['dependencies']
    locked=json.loads((root/'Packages/packages-lock.json').read_text())['dependencies']
    for name,version in declared.items():
        # Native built-in modules can be absent from the saved lock (wind is
        # one in this project). Such entries cannot supply any scene GUID.
        if name not in locked:continue
        if re.fullmatch(r'\d+\.\d+\.\d+(?:-[\w.]+)?',version):
            assert locked[name]['version']==version,('Manifest/package lock version mismatch',name)
    reachable=set();pending=[name for name in declared if name in locked]
    while pending:
        name=pending.pop()
        if name in reachable:continue
        assert name in locked,('Dependency missing from package lock',name)
        reachable.add(name);pending.extend(locked[name].get('dependencies',{}))
    hydrated=set();matching=set();package_guids={}
    directories=list((root/'Packages').glob('*/'))+list((root/'Library/PackageCache').glob('*/'))
    for directory in directories:
        claimed=directory.name.split('@',1)[0]
        package_json=directory/'package.json'
        if not package_json.is_file():
            assert claimed not in reachable,('Hydrated package lacks package.json',claimed)
            continue
        info=json.loads(package_json.read_text());name=info['name'];version=info['version']
        if claimed in reachable:
            assert claimed==name,('Hydrated package name mismatch',directory,name)
        if name not in reachable:continue
        hydrated.add(name)
        if version!=locked[name]['version']:continue # An old cache is not the resolved package.
        matching.add(name)
        for meta in directory.rglob('*.meta'):
            raw=meta.read_bytes();found=re.search(rb'(?m)^guid: ([0-9a-f]{32})\r?$',raw)
            if found is None:continue
            guid=found.group(1).decode();asset=meta.with_suffix('')
            identity=(name,version,asset.relative_to(directory).as_posix())
            digest=hashlib.sha256(raw).hexdigest()
            if guid in package_guids:
                previous=package_guids[guid]
                assert previous[:3]==identity and previous[3]==digest,\
                    ('Conflicting package GUID identities',guid,previous[:3],identity)
            package_guids[guid]=identity+(digest,asset)
    evidence=[]
    for guid in sorted(required):
        observed=package_guids.get(guid);contract=SCENE_PACKAGE_ASSETS.get(guid)
        if observed is not None:
            name,version,asset,digest,path=observed
            assert path.exists(),('Package metadata has no asset',guid,path)
            if contract is not None:
                assert (name,asset)==(contract[0],contract[2]),\
                    ('Package GUID moved to another asset',guid,name,asset)
                if version==contract[1]:
                    assert observed[:4]==contract,('Hydrated package metadata differs from verified identity',guid)
            evidence.append({'guid':guid,'package':name,'version':version,'asset':asset,
                             'verification':'hydrated_package_metadata'})
            continue
        assert contract is not None,('Unresolved asset GUID',guid)
        name,version,asset,digest=contract
        assert name in reachable and locked[name]['version']==version and locked[name]['source']=='builtin',\
            ('Scene package snapshot does not match declared dependency',guid,name,version)
        assert name not in hydrated and name not in matching,\
            ('Hydrated package lacks matching verified metadata',guid,name)
        evidence.append({'guid':guid,'package':name,'version':version,'asset':asset,
                         'metadata_sha256':digest,'verification':'pinned_metadata_contract_only'})
    return evidence

def active_detail_scene_paths(root):
    """Verify the saved build/streamer manifest; archives are not live cells.

    This is a source packaging gate. Native AarisUltraSectorBake.VerifySaved
    separately verifies resolved prefab meshes, materials and world poses.
    """
    from collections import Counter
    from world_partition_readiness import WorldScene
    main='Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity'
    prefix='Assets/AarisWorld/Scenes/Chunks/'
    text=(root/'ProjectSettings/EditorBuildSettings.asset').read_text()
    entries=re.findall(r'(?m)^  - enabled: ([01])\n    path: ([^\r\n]+)\n    guid: ([0-9a-f]{32})$',text)
    enabled=[(path,guid)for state,path,guid in entries if state=='1']
    assert enabled and enabled[0][0]==main,'Missing first bootstrap scene'
    paths=[path for path,guid in enabled]
    assert len(paths)==len(set(paths)),'Duplicate enabled scene'
    for path,guid in enabled:
        assert (root/path).is_file(),('Missing enabled scene',path)
        meta=Path(str(root/path)+'.meta').read_text()
        assert re.search(r'(?m)^guid: '+guid+r'$',meta),('Build scene GUID mismatch',path)
    script_meta=(root/'Assets/AarisWorld/Scripts/AarisAdditiveChunkStreamer.cs.meta').read_text()
    script_guid=re.search(r'(?m)^guid: ([0-9a-f]{32})$',script_meta)
    assert script_guid,'Missing streamer script GUID'
    scene=WorldScene((root/main).read_text())
    streamers=[block.body for block in scene.blocks.values() if block.kind==114 and
               re.search(r'm_Script: \{fileID: \d+, guid: '+script_guid.group(1)+r',',block.body)]
    assert len(streamers)==1,'Expected one saved additive streamer'
    registered=re.findall(r'(?m)^  - scenePath: ([^\r\n]+)$',streamers[0])
    assert registered and len(registered)==len(set(registered)),'Empty/duplicate streamer manifest'
    assert set(paths[1:])==set(registered),'Enabled scenes differ from saved streamer'
    for path in registered:
        assert path.startswith(prefix) and path.endswith('.unity') and '..' not in Path(path).parts,\
            ('Unexpected detail scene path',path)
    grid=any(Path(path).name.startswith('Aaris_Cell_')for path in registered)
    if grid:
        assert all(Path(path).name.startswith('Aaris_Cell_')for path in registered),\
            'Mixed grid cells and archived districts'
        folder=root/prefix
        archives=sorted(folder.glob('Aaris_District_*_Detail.unity'))+[folder/'Aaris_ForestLookout_Detail.unity']
        assert len(archives)==13 and all(path.is_file()for path in archives),\
            'Original thirteen source archives must remain present'
        assert not(set(paths)&{path.relative_to(root).as_posix()for path in archives}),\
            'Source archive incorrectly ships as active detail'
        def signatures(cell_paths):
            result=Counter(); counts={}
            for path in cell_paths:
                cell=WorldScene(path.read_text())
                assert not any(block.kind in (54,64,65,135,136,143,146,154,218)for block in cell.blocks.values()),\
                    ('Gameplay collision/terrain entered visual cell',path)
                roots=[block.body for block in cell.blocks.values()if block.kind==1660057539]
                assert len(roots)==1,('Missing native SceneRoots',path)
                count=len(re.findall(r'(?m)^  - \{fileID: -?\d+\}$',roots[0]))
                counts[path.relative_to(root).as_posix()]=count
                represented=0
                for block in cell.blocks.values():
                    if block.kind!=1001:continue
                    prefab=re.search(r'm_SourcePrefab: \{fileID: \d+, guid: ([0-9a-f]{32}),',block.body)
                    names=tuple(re.findall(r'propertyPath: m_Name\n      value: ([^\r\n]+)',block.body))
                    assert prefab and names,('Unnamed/unresolved source prefab',path)
                    removed=re.search(r'm_RemovedComponents:(.*?)\n    m_RemovedGameObjects:',block.body,re.S)
                    assert removed and 'fileID:' in removed.group(1),('Persistent collider override lost',path)
                    removed_refs=tuple(sorted(re.findall(r'fileID: (-?\d+), guid: ([0-9a-f]{32})',removed.group(1))))
                    result[('prefab',prefab.group(1),names,removed_refs)]+=1;represented+=1
                for owner in cell.names:
                    if cell.parent_go[owner] is not None:continue
                    assert cell.component_types[owner]=={33,23,205},('Nonvisual explicit cell root',path,cell.names[owner])
                    members=cell.descendants(owner)
                    assert len(members)==3,('Changed landmark LOD hierarchy',path)
                    assets=[];lods=[]
                    for uid,block in cell.blocks.items():
                        go=cell.owner.get(uid)
                        if go not in members:continue
                        if block.kind in (23,33):
                            assets.append((cell.names[go],block.kind,tuple(re.findall(
                                r'fileID: (-?\d+), guid: ([0-9a-f]{32})',block.body))))
                        if block.kind==205:
                            lods.extend(re.findall(r'screenRelativeHeight: ([^\r\n]+)',block.body))
                    result[('explicit',cell.names[owner],tuple(sorted(assets)),tuple(lods))]+=1;represented+=1
                assert count==represented and count>0,('Unaccounted/empty native cell roots',path)
            return result,counts
        source,_=signatures(archives)
        active,counts=signatures([root/path for path in registered])
        assert sum(source.values())==94,'Authored source root count changed'
        assert active==source,'Active grid lost/duplicated a source root or changed its collider override/LOD assets'
        fallback_paths=re.findall(r'(?m)^  chunkScenePath: ([^\r\n]+)$',(root/main).read_text())
        assert Counter(fallback_paths)==Counter(counts),'Fallback ownership differs from active cell roots'
    return [root/path for path in paths[1:]]

def check(require_native=False):
    resolver=NativeSceneResolver(ROOT)
    main_view=resolver.resolve(ROOT/'Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity')
    objects=main_view.objects;byid=main_view.byid
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
    # URP camera configuration and a grading volume do not control movement.
    camera_render_scripts = {
        'a79441f348de89743a2939f4d699eac1',  # UniversalAdditionalCameraData
        '172515602e62fb746b5d573b38a5fe58',  # Volume
    }
    for c, i, n, d in objects:
        if c == 114 and d.get('m_GameObject', {}).get('fileID') == camera_go:
            assert d.get('m_Script', {}).get('guid') in camera_render_scripts, (
                'Unexpected camera behaviour', i)
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
    house_report,houses,replacement_models=airy_house_contract(ROOT)
    assert building_names-set(houses)==set(house_report['landmarks_preserved'])
    names={i:d['m_Name'] for c,i,n,d in objects if n=='GameObject'}
    lods=[d for c,i,n,d in objects if n=='LODGroup' and names[d['m_GameObject']['fileID']] in building_names]
    lod_names={id(d):names[d['m_GameObject']['fileID']]for d in lods}
    # All authored LOD building visuals now span native built additive
    # scenes. The persistent bootstrap must keep every physical collider and
    # a faithful far-LOD silhouette; counting only bootstrap LODGroups would
    # incorrectly declare 93 real buildings missing.
    detail_folder=ROOT/'Assets/AarisWorld/Scenes/Chunks'
    detail_path=detail_folder/'Aaris_ForestLookout_Detail.unity'
    scene_paths=active_detail_scene_paths(ROOT)
    forest_lods=0
    all_detail_names=set()
    for cell_path in scene_paths:
        detail_view=resolver.resolve(cell_path);detail=detail_view.objects
        cell_ids={i for c,i,n,d in detail}
        assert len(cell_ids)==len(detail),'Duplicate saved chunk object ID'
        for c,i,n,d in detail:
            for r in refs(d):
                if r['fileID'] and not r.get('guid'):
                    assert r['fileID'] in cell_ids,(cell_path,n,i,'Missing chunk local reference')
        detail_names={i:d['m_Name'] for c,i,n,d in detail if n=='GameObject'}
        root_names=detail_view.root_names()
        assert len(root_names)==len(set(root_names)),('Duplicate authored root',cell_path)
        names_here=set(root_names)
        assert all_detail_names.isdisjoint(names_here),'Duplicate full detail across chunk scenes'
        all_detail_names.update(names_here)
        detail_lods=[d for c,i,n,d in detail
                     if n=='LODGroup' and
                     detail_names[d['m_GameObject']['fileID']] in building_names]
        lod_names.update({id(d):detail_names[d['m_GameObject']['fileID']]for d in detail_lods})
        assert not any(c in (54,64,65,135,136,143,146,154,218)for c,i,n,d in detail),\
            'Additive detail must not duplicate persistent physics'
        found_forest=sum(detail_names[d['m_GameObject']['fileID']]=='ForestLookout_watchtower_01'for d in detail_lods)
        if cell_path==detail_path:assert len(detail_lods)==found_forest==1
        forest_lods+=found_forest
        lods+=detail_lods
    if detail_path.exists():assert forest_lods==1
    build=documents(ROOT/'ProjectSettings/EditorBuildSettings.asset')[0][3]
    enabled=[item['path'] for item in build['m_Scenes'] if item['enabled']]
    assert enabled[0]=='Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity'
    assert len(enabled)==1+len(scene_paths)
    assert len(set(enabled))==len(enabled)
    assert set(enabled[1:])=={p.relative_to(ROOT).as_posix() for p in scene_paths}
    assert len(lods)==59 and all(len(d['m_LODs'])==3 for d in lods)
    # The later Airy-house bake replaced 56 original buildings while retaining
    # three landmarks. Both exact authored LOD contracts remain enforced.
    for lod in lods:
        name=lod_names[id(lod)]
        assert_lod_thresholds(lod,[.085,.026,.005]if name in houses else [.22,.07,.0025],name)
    sky=next(d for c,i,n,d in documents(ROOT/'Assets/TerrainDemoScene_URP/Prefabs/Skybox/Materials/Sky.mat') if n=='Material')
    assert sky['m_Shader']['fileID']==106 and sky['m_ShaderKeywords']=='_SUNDISK_HIGH_QUALITY'
    sun=next(d for c,i,n,d in objects if n=='Light');assert sun['m_Enabled'] and 1<=sun['m_Intensity']<=1.5
    settings=documents(ROOT/'ProjectSettings/ProjectSettings.asset')[0][3]
    assert settings['activeInputHandler']==0 and settings['AndroidTargetArchitectures']==2 and settings['scriptingBackend']['Android']==1
    for target in ['Android','Standalone','WebGL']:assert 'URP_COMPATIBILITY_MODE' in settings['scriptingDefineSymbols'][target].split(';')
    quality_settings=documents(ROOT/'ProjectSettings/QualitySettings.asset')[0][3]
    quality=quality_settings['m_QualitySettings'][quality_settings['m_CurrentQuality']]
    assert quality['globalTextureMipmapLimit']==0 and quality['streamingMipmapsActive']==1 and quality['lodBias']==.75
    assert quality['customRenderPipeline']['guid']=='758559729899d1340bf85e82688a079f',\
        'Expected the measured mobile URP preset'
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
    package_references=[]
    if require_native or (ROOT/'TRANSFER-MANIFEST.json').exists():
        guids={yaml.safe_load(p.read_text())['guid'] for p in (ROOT/'Assets').rglob('*.meta')}
        external=set()
        for c,i,n,d in objects:
            for r in refs(d):
                g=r.get('guid')
                if g and not g.startswith('0000000000000000')and g not in guids:external.add(g)
                if g in SCENE_PACKAGE_ASSETS:
                    assert r['fileID']==11500000 and r.get('type')==3,(n,'Invalid package MonoScript reference',r)
        package_references=resolve_package_scene_guids(ROOT,external)
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
        'package_scene_asset_references':package_references,
        'unity_editor_compilation_tested':False,'unity_runtime_input_tested':False,'android_device_tested':False}
    (ROOT/'Docs/EXPLORER_SETUP_REPORT.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result))
    return result

if __name__=='__main__':check('--native'in sys.argv)
