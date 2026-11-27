"""Reopen saved TerrainData and compare preserved vendor data with original bytes."""
import argparse,hashlib,json
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'MapBuild'))
import numpy as np
import UnityPy
from sculpt import SOURCE_HASHES,BASE

def verify_detail_prototypes(original, current, recalibrated):
    assert len(original) == len(current)
    size_fields = {"minWidth", "maxWidth", "minHeight", "maxHeight", "useInstancing"}
    for index, (before, after) in enumerate(zip(original, current)):
        if index in recalibrated:
            assert {k: v for k, v in before.items() if k not in size_fields} == {
                k: v for k, v in after.items() if k not in size_fields}, index
            assert after["useInstancing"] == 1, index
        else:
            assert before == after, index

def verify(source_dir=None):
    data=Path('Assets/TerrainDemoScene_URP/Terrain/Data');geometry=np.load('Tools/MapBuild/.build_geometry.npz')['height'];reports=[]
    layout=json.loads(Path('Docs/MAP_LAYOUT.json').read_text())
    report_path = Path('Docs/NATIVE_TERRAIN_VERIFICATION.json')
    previous = json.loads(report_path.read_text()) if report_path.exists() else {}
    updated = {entry['tile']: entry.get('recalibrated_bush_prototype_slots', []) for entry in previous.get('tiles', [])}
    for tag,sha in SOURCE_HASHES.items():
        out=next(p for p in data.glob(f'Terrain_{tag}_*.asset') if not p.stem.endswith(' 1'))
        src=next(Path(source_dir).glob(f'Terrain_{tag}_*.asset')) if source_dir else Path('.git/lfs/objects')/sha[:2]/sha[2:4]/sha
        assert hashlib.sha256(src.read_bytes()).hexdigest()==sha
        se=UnityPy.load(str(src));fe=UnityPy.load(str(out));so=next(x for x in se.objects if x.type.name=='TerrainData');fo=next(x for x in fe.objects if x.type.name=='TerrainData')
        s=so.parse_as_dict();f=fo.parse_as_dict();sf,ff=so.assets_file,fo.assets_file
        external=lambda file:[(e.guid.hex(),e.path,e.type) for e in file.externals]
        assert external(sf)==external(ff),(tag,'external references changed')
        assert sf.unity_version==ff.unity_version
        assert s['m_Name']==f['m_Name'] and s['m_SplatDatabase']==f['m_SplatDatabase']
        recalibrated = updated.get(tag, [])
        verify_detail_prototypes(s['m_DetailDatabase']['m_DetailPrototypes'],
                                 f['m_DetailDatabase']['m_DetailPrototypes'], recalibrated)
        for field in ('m_TreePrototypes','m_PatchCount','m_PatchSamples'):
            assert s['m_DetailDatabase'][field]==f['m_DetailDatabase'][field],(tag,field)
        textures=0
        for ident,obj in sf.objects.items():
            if obj.type.name=='Texture2D':assert obj.get_raw_data()==ff.objects[ident].get_raw_data(),(tag,'texture changed');textures+=1
        raw=np.asarray(f['m_Heightmap']['m_Heights']).reshape(2049,2049);height=raw/32766*800+BASE;i,j=map(int,tag.split('_'))
        planned=geometry[(j-1)*2048:(j-1)*2048+2049,(i-1)*2048:(i-1)*2048+2049]
        err=float(np.max(np.abs(height-planned)));assert err<.0001,(tag,err)
        assert abs(min(f['m_Heightmap']['m_MinMaxPatchHeights'])-float(raw.min()/32766))<1e-6
        assert abs(max(f['m_Heightmap']['m_MinMaxPatchHeights'])-float(raw.max()/32766))<1e-6
        lines=sum(1 for _ in Path('Docs/TerrainInstances',f'Terrain_{tag}.jsonl').open());assert lines==len(f['m_DetailDatabase']['m_TreeInstances'])
        reports.append({'tile':tag,'embedded_alpha_textures_preserved':textures,'terrain_layers_preserved':True,
                        'other_detail_prototypes_preserved':True,'recalibrated_bush_prototype_slots':recalibrated,'external_references_preserved':True,
                        'native_height_roundtrip_max_error_m':err,'indexed_instance_count':lines,'original_unity_serialization_version':ff.unity_version})
    report={'status':'passed','unity_editor_tested':False,'tiles':reports,'terrain_files':json.loads(Path('Docs/MAP_BUILD_REPORT.json').read_text())['terrain_files']}
    report_path.write_text(json.dumps(report,indent=2)+'\n');print(json.dumps(report),flush=True)
if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--source-dir');args=parser.parse_args();verify(args.source_dir)
