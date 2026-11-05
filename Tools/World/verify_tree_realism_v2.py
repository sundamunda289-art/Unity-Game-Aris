"""Strict source verification of v2 SpeedTree proportions, GUIDs, colliders and LODs.

Only six explicitly reviewed saved prefab/importer-metadata blobs are allowed.
No .st binaries, TerrainData instances, Scene files or prefab .meta identities
are modified by the upgrade. Actual Unity imported mesh/render/physics must be
verified separately in the real Editor before claiming visual quality.
"""
from __future__ import annotations
import hashlib
import json
import math
from pathlib import Path
import re
import sys

PARENT=Path(__file__).resolve().parents[2]
MANIFEST="Tools/World/tree-realism-v2-reviewed.json"
HISTORIC="Tools/World/tree-proportions-20261103.json"
TREE_ROOT="Assets/TerrainDemoScene_URP/Prefabs/Trees/"


class TreeRealismError(ValueError):
    pass


def git_blob_sha1(data:bytes)->str:
    return hashlib.sha1(b"blob "+str(len(data)).encode()+b"\0"+data).hexdigest()


def authorized_tree_blobs(root=PARENT):
    root=Path(root)
    manifest=root/MANIFEST
    if not manifest.is_file():
        return {}
    data=json.loads(manifest.read_text())
    if data.get("schema_version")!=2 or data.get("transaction_id")!="aaris-natural-trunks-20261105-v2":
        raise TreeRealismError("Unexpected v2 upgrade manifest")
    profiles=data["profiles"]
    if len(profiles)!=6:
        raise TreeRealismError("Expected exactly six reviewed SpeedTree species")
    blobs={}
    names=set()
    for profile in profiles:
        key=profile["key"]
        if not isinstance(key,str) or key in names:
            raise TreeRealismError("Duplicate tree species")
        names.add(key)
        for path_key,hash_key in (
            ("prefab","reviewed_prefab_git_blob_sha1"),
            ("source_meta","reviewed_source_meta_git_blob_sha1")
        ):
            path=profile[path_key]
            approved=profile[hash_key]
            if (not path.startswith(TREE_ROOT) or ".." in Path(path).parts
                or not re.fullmatch(r"[a-f0-9]{40}",approved)):
                raise TreeRealismError("Unsafe tree source or blob hash")
            b=(root/path).read_bytes()
            if git_blob_sha1(b)!=approved:
                raise TreeRealismError("Unreviewed Unity tree source change: "+path)
            blobs[path]=approved
    return blobs


def verify(root=PARENT):
    root=Path(root).resolve()
    reviewed=authorized_tree_blobs(root)
    if not reviewed:
        raise TreeRealismError("Approved tree realism manifest missing")
    manifest=json.loads((root/MANIFEST).read_text())
    historic=json.loads((root/HISTORIC).read_text())
    prior={row["prefab"]:row for row in historic["profiles"]}
    result=[]
    for row in manifest["profiles"]:
        prefab=row["prefab"];source=row["source_meta"]
        if prefab!=TREE_ROOT+row["key"]+".prefab" or source!=TREE_ROOT+row["key"]+".st.meta":
            raise TreeRealismError("Profile paths and name do not agree")
        if prefab not in prior or prior[prefab]["source_meta"]!=source:
            raise TreeRealismError("SpeedTree was not in the original approved source list")
        h=float(row["height_factor"])
        base=float(row["base_width_factor"])
        crown=float(row["crown_width_factor"])
        old_b=float(row["previous_base_width_factor"])
        old_c=float(row["previous_crown_width_factor"])
        if not (0.5<=h<=1 and 1.0<=old_c<=crown<=base<=1.8
                and old_b<base<=old_b*1.15
                and crown<=old_c*1.05
                and math.isfinite(base) and math.isfinite(crown)):
            raise TreeRealismError("Unsafe/non-natural trunk and canopy proportions")
        if abs(h-prior[prefab]["height_factor"])>1e-8 or abs(old_b-prior[prefab]["base_width_factor"])>1e-8:
            raise TreeRealismError("Unexpected historical height/trunk source")
        # The corresponding .prefab.meta, NOT the intentional .st.meta importer
        # profile, must remain byte-identical to the old approved GUID.
        from unity_data import digest
        if digest(root/(prefab+".meta"))!=historic["expected_files"][prefab+".meta"]:
            raise TreeRealismError("Tree prefab GUID changed: "+prefab)
        metadata=(root/source).read_text()
        match=re.search(r"(?m)^  userData: aaris-tree-shape-v2\|([\d.]+)\|([\d.]+)$",metadata)
        if not match or abs(float(match[1])*h-base)>1e-5 or abs(float(match[2])*h-crown)>1e-5:
            raise TreeRealismError("Importer taper and approved profile differ")
        from unity_data import documents
        _,docs=documents(root/prefab)
        collider=[d for d in docs if d["kind"]=="BoxCollider"]
        lod=[d for d in docs if d["kind"]=="LODGroup"]
        if len(collider)!=1 or len(lod)!=1:
            raise TreeRealismError("Tree collider or LODGroup identity changed")
        box=collider[0]["data"]
        if box.get("m_IsTrigger") or not box.get("m_Enabled"):
            raise TreeRealismError("Tree collision has been disabled")
        size=box["m_Size"]
        if size["x"]<=0 or size["y"]<=0 or size["z"]<=0:
            raise TreeRealismError("Invalid reviewed collider dimensions")
        thresholds=[t["screenRelativeHeight"] for t in lod[0]["data"]["m_LODs"]]
        if len(thresholds)!=4 or thresholds!=sorted(thresholds,reverse=True):
            raise TreeRealismError("Changed/broken SpeedTree LOD ordering")
        result.append({"tree":row["key"],"base_multiplier":round(base/old_b,4),
                       "crown_multiplier":round(crown/old_c,4),
                       "height_preserved":True,"collider_active":True,
                       "lod_count":len(thresholds)})
    if manifest.get("importer_version")!=3:
        raise TreeRealismError("Expected reviewed mid-trunk taper importer v3")
    importer=(root/"Assets/AarisWorld/Editor/AarisTreeShapeImporter.cs").read_text()
    if ("public override uint GetVersion() { return 3; }" not in importer
            or '"aaris-tree-shape-v2"' not in importer
            or "RadialProfile(p.y, height, lower, upper" not in importer):
        raise TreeRealismError("Unity v2 SpeedTree importer activation missing")
    return {"verified_source":True,"species":result,"approved_files":len(reviewed),
            "speedtree_binary_modified":False,
            "prefab_guids_modified":False,
            "actual_unity_import_and_render_verified":False,"importer_version":3}


if __name__=="__main__":
    print(json.dumps(verify(),indent=2))
