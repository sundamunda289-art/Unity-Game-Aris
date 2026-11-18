#!/usr/bin/env python3
"""Offline only: exact CC0 closed-body GT GLB -> 5 separated Unity-ready OBJ meshes.

The source's 12 2048px textures are flat RGB colors. Encode their verified
color factors in the .mtl instead of wasting 192 MiB uncompressed GPU memory.
Never execute on a mobile device; no dependency beyond the Python stdlib.
"""
import argparse
import hashlib
import json
import math
import re
import struct
from collections import defaultdict
from pathlib import Path

SOURCE_SHA256 = "ad0fd94845edb9af474407fe08fb379b624c56ec4d4b4ad6b47822b65c6f8903"
SOURCE_PAGE = "https://www.innerscene.com/tools/library/3d-parts/generic-mid-engine-supercar-73f252c5"
GROUPS = ("body", "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr")
# Original GLB forward is negative X, height is Y, lateral is Z.
# Convert to Unity/vehicle chassis: left -X, up +Y, nose +Z.
CENTRES = {
    "wheel_fl": (-.90095, .344, 1.27),
    "wheel_fr": ( .90095, .344, 1.27),
    "wheel_rl": (-.95615, .362, -1.43),
    "wheel_rr": ( .95615, .362, -1.43),
}
# Preserve the whole sculpted shell, glass, lighting, functional tyres,
# recognizable wheel face and basic cockpit. Remove *invisible* deep barrels,
# expensive circumferential micro-ribs, door shut-line ribbons and tiny props.
OMIT = re.compile(
    r"^(Continuous circumferential tread rib|Deep wheel barrel inner lip|"
    r"Door shut seam|Steering wheel rim|Steering spoke|Steering boss|"
    r"Cross-drilled brake disc|Barrel shadow disc|Wheelhouse arched liner|"
    r"Mid-mounted engine block|Interior carpet|Integrated seat head restraint)", re.I)
WHEEL = re.compile(
    r"(tyre|tire|wheel|hub|rim|barrel|tread|spoke|centre-lock|disc)",
    re.I)
MATS = (
    ("AarisRoadGT_Black",    (24, 29, 34), .05, .52),
    ("AarisPaint_RoadGT",   (170,29, 36), .65, .76),
    ("AarisRoadGT_Accent",   (196,39, 25), .30, .70),
    ("AarisRoadGT_Glass",    (188,203,209), .0, .935),
    ("AarisRoadGT_Chrome",   (206,216,223), .96, .83),
    ("AarisRoadGT_Tyre",     (30, 34, 38), .0, .22),
    ("AarisRoadGT_Steel",    (124,137,144), .78, .68),
    ("AarisRoadGT_RedLens",  (170,16, 30), .10, .86),
    ("AarisRoadGT_Interior", (55, 61, 66), .0, .25),
    ("AarisRoadGT_Seat",     (97, 93, 89), .0, .14),
    ("AarisRoadGT_White",    (226,230,231), .12, .70),
    ("AarisRoadGT_ClearLens",(243,248,252), .0, .94),
)

def parse_glb(path):
    raw = path.read_bytes()
    if hashlib.sha256(raw).hexdigest() != SOURCE_SHA256:
        raise ValueError("Refusing unpinned or modified GLB input")
    if len(raw) < 24 or raw[:4] != b"glTF" or struct.unpack_from("<I", raw, 4)[0] != 2:
        raise ValueError("Not a glTF 2.0 binary")
    if struct.unpack_from("<I", raw, 8)[0] != len(raw):
        raise ValueError("Truncated GLB")
    jl, typ = struct.unpack_from("<I4s", raw, 12)
    if typ != b"JSON": raise ValueError("Missing GLB JSON chunk")
    doc = json.loads(raw[20:20 + jl])
    bl, typ = struct.unpack_from("<I4s", raw, 20 + jl)
    if typ != b"BIN\x00": raise ValueError("Missing GLB binary chunk")
    binary = raw[28 + jl:28 + jl + bl]
    license_text = doc.get("asset", {}).get("copyright", "")
    if "CC0 1.0 Universal" not in license_text:
        raise ValueError("GLB must explicitly contain CC0 dedication")
    if doc.get("extensionsRequired"):
        raise ValueError("Unsupported glTF extensions")
    return doc, binary

def elements(doc, buf, index):
    spec = doc["accessors"][index]
    if "bufferView" not in spec or "sparse" in spec:
        raise ValueError("Sparse/implicit accessor not supported")
    view = doc["bufferViews"][spec["bufferView"]]
    kind = {5121:"B", 5123:"H", 5125:"I", 5126:"f"}.get(spec["componentType"])
    width = {"SCALAR":1, "VEC2":2, "VEC3":3, "VEC4":4}.get(spec["type"])
    if not kind or not width: raise ValueError("Unsupported glTF accessor")
    fmt = "<" + kind * width
    size = struct.calcsize(fmt)
    stride = view.get("byteStride", size)
    if stride < size: raise ValueError("Invalid stride")
    start = view.get("byteOffset", 0) + spec.get("byteOffset", 0)
    if start + max(0, spec["count"] - 1) * stride + size > len(buf):
        raise ValueError("Buffer overflow in accessor")
    return [struct.unpack_from(fmt, buf, start + k*stride)
            for k in range(spec["count"])]

def mul(a,b):
    return [[sum(a[i][k]*b[k][j] for k in range(4))
             for j in range(4)] for i in range(4)]

def local_matrix(node):
    if "matrix" in node:
        v=node["matrix"]
        return [[v[j*4+i] for j in range(4)] for i in range(4)]
    x,y,z,w=node.get("rotation",(0,0,0,1))
    sx,sy,sz=node.get("scale",(1,1,1))
    a,b,c=node.get("translation",(0,0,0))
    return [[(1-2*(y*y+z*z))*sx,2*(x*y-z*w)*sy,2*(x*z+y*w)*sz,a],
            [2*(x*y+z*w)*sx,(1-2*(x*x+z*z))*sy,2*(y*z-x*w)*sz,b],
            [2*(x*z-y*w)*sx,2*(y*z+x*w)*sy,(1-2*(x*x+y*y))*sz,c],
            [0,0,0,1]]

def world_matrices(doc):
    nodes=doc["nodes"]
    parents={}
    for i,n in enumerate(nodes):
        for ch in n.get("children",[]):
            if ch in parents:raise ValueError("glTF multiple node parents")
            parents[ch]=i
    cache={}
    def world(i):
        if i not in cache:
            own=local_matrix(nodes[i])
            cache[i]=mul(world(parents[i]),own) if i in parents else own
        return cache[i]
    return [world(i) for i in range(len(nodes))]

def change_frame(m, xyz, normal=False):
    x,y,z=xyz[:3]
    if normal:
        a=m[0][0]*x+m[0][1]*y+m[0][2]*z
        b=m[1][0]*x+m[1][1]*y+m[1][2]*z
        c=m[2][0]*x+m[2][1]*y+m[2][2]*z
        length=max(1e-6, math.sqrt(a*a+b*b+c*c))
        return (-c/length,b/length,-a/length)
    a=m[0][0]*x+m[0][1]*y+m[0][2]*z+m[0][3]
    b=m[1][0]*x+m[1][1]*y+m[1][2]*z+m[1][3]
    c=m[2][0]*x+m[2][1]*y+m[2][2]*z+m[2][3]
    return (-c,b,-a)

def group_of(name, vertices):
    if not WHEEL.search(name): return "body"
    # Never rotate calipers/arches with the wheel: wheel visual roots are the
    # *rotating* tyre, alloy and central hub pieces only.
    if "caliper" in name.lower() or "wheelhouse" in name.lower():
        return "body"
    xx=sum(v[0] for v in vertices)/len(vertices)
    zz=sum(v[2] for v in vertices)/len(vertices)
    choice=min(CENTRES, key=lambda g: (CENTRES[g][0]-xx)**2+(CENTRES[g][2]-zz)**2)
    d=(CENTRES[choice][0]-xx)**2+(CENTRES[choice][2]-zz)**2
    return choice if d < .75*.75 else "body"

def save_materials(output):
    lines=["# Exact CC0 flat texture colours collapsed into GPU-cheap URP palette."]
    for name,rgb,metal,smooth in MATS:
        vals=[v/255.0 for v in rgb]
        lines.extend((f"newmtl {name}",
                      "Kd "+ " ".join(f"{v:.6f}" for v in vals),
                      "Ks .15 .15 .15", f"Ns {max(2,1000*smooth*smooth):.1f}",
                      "illum 2", ""))
    (output/"AarisRoadGT.mtl").write_text("\n".join(lines)+"\n")

def convert(input_glb, output, budget=70000):
    doc,bin_chunk=parse_glb(input_glb)
    if len(doc.get("materials",[])) != len(MATS): raise ValueError("Material count changed")
    matrices=world_matrices(doc)
    data={k:{"v":[],"vn":[],"f":defaultdict(list),"count":0}
          for k in GROUPS}
    counts={"raw":0,"skipped":0}
    for ni,node in enumerate(doc["nodes"]):
        if "mesh" not in node:continue
        name=node.get("name","")
        for prim in doc["meshes"][node["mesh"]]["primitives"]:
            if prim.get("mode",4) != 4: raise ValueError("Non-triangle primitive")
            idx=elements(doc,bin_chunk,prim["indices"]) if "indices" in prim else None
            pos=elements(doc,bin_chunk,prim["attributes"]["POSITION"])
            normals=elements(doc,bin_chunk,prim["attributes"]["NORMAL"])
            if len(pos)!=len(normals):raise ValueError("Missing vertex normals")
            if idx is None:idx=[(i,) for i in range(len(pos))]
            if len(idx)%3:raise ValueError("Non-triangular indexes")
            tris=len(idx)//3
            counts["raw"]+=tris
            if OMIT.search(name):
                counts["skipped"]+=tris
                continue
            positions=[change_frame(matrices[ni],v) for v in pos]
            group=group_of(name,positions)
            part=data[group]
            pivot=CENTRES.get(group,(0,0,0))
            offset=len(part["v"])
            part["v"].extend((v[0]-pivot[0],v[1]-pivot[1],v[2]-pivot[2])
                             for v in positions)
            part["vn"].extend(change_frame(matrices[ni],v,True) for v in normals)
            mat=prim.get("material",0)
            if not 0<=mat<len(MATS):raise ValueError("Unexpected material")
            faces=part["f"][mat]
            for k in range(0,len(idx),3):
                triplet=[offset+idx[k+t][0]+1 for t in range(3)]
                if any(n<offset+1 or n>len(part["v"]) for n in triplet):
                    raise ValueError("Out of bounds triangle index")
                # RH glTF to LH Unity frame flips handedness. OBJ normals are
                # transformed with the same frame; reverse polygon winding.
                a,b,c=triplet
                faces.append((a,c,b))
            part["count"]+=tris
    # Some source wheel-face normals/outer layers are asymmetric: the right
    # alloy renders clearly but the left rim is almost black in native Unity.
    # Mirror the actually-good right alloys, rubber and hub topology onto
    # the left axle in local wheel coordinates, preserving correct winding.
    # This does not change the face count or cost any runtime reflection.
    for left, right in (("wheel_fl", "wheel_fr"),
                        ("wheel_rl", "wheel_rr")):
        source = data[right]
        data[left] = {
            "v": [(-x, y, z) for x, y, z in source["v"]],
            "vn": [(-x, y, z) for x, y, z in source["vn"]],
            "f": defaultdict(list, {
                material: [(a, c, b) for (a, b, c) in faces]
                for material, faces in source["f"].items()
            }),
            "count": source["count"],
        }
    # Imported source wheel covers are very dark at oblique left-facing
    # views. Add a small real geometric five-spoke alloy face on the
    # outward plane of each wheel: one shared chrome material, no textures,
    # no new GameObjects, no per-frame code, only 72 tris per wheel.
    added_rim_triangles = 0
    for group in GROUPS[1:]:
        part = data[group]
        outside = -1.0 if group.endswith("l") else 1.0
        radius = .343 if group.startswith("wheel_f") else .362
        x = outside * .043
        faces = part["f"][4]  # Existing chrome material; same draw call.
        def vert(y,z):
            part["v"].append((x,y,z))
            part["vn"].append((outside,0.0,0.0))
            return len(part["v"])
        def tri(a,b,c):
            faces.append((a,b,c) if outside < 0 else (a,c,b))
        def circle_point(r, angle):
            return (r*math.sin(angle),r*math.cos(angle))
        def ring_quad(inner,outer,start,end):
            y0,z0=circle_point(inner,start)
            y1,z1=circle_point(outer,start)
            y2,z2=circle_point(outer,end)
            y3,z3=circle_point(inner,end)
            a,b,c,d=(vert(*q) for q in ((y0,z0),(y1,z1),(y2,z2),(y3,z3)))
            tri(a,b,c)
            tri(a,c,d)
        before=len(faces)
        for i in range(24):
            a=i*math.tau/24
            ring_quad(radius*.62,radius*.76,a,a+math.tau/24)
        for i in range(5):
            theta=i*math.tau/5
            ring_quad(radius*.16,radius*.64,theta-.105,theta+.105)
        center=vert(0.0,0.0)
        for i in range(14):
            a=i*math.tau/14
            u=vert(*circle_point(radius*.145,a))
            v=vert(*circle_point(radius*.145,a+math.tau/14))
            tri(center,u,v)
        newfaces=len(faces)-before
        part["count"]+=newfaces
        added_rim_triangles+=newfaces
    tris=sum(part["count"] for part in data.values())
    if counts["raw"]!=104282 or tris>budget or tris<50000:
        raise ValueError("Unexpected geometry budget: "+str(counts))
    if any(data[k]["count"] < 2300 for k in GROUPS[1:]):
        raise ValueError("Incomplete separate wheel geometry")
    output.mkdir(parents=True,exist_ok=True)
    save_materials(output)
    for group in GROUPS:
        part=data[group]
        lines=["# Innerscene CC0 unbranded premium mid-engine coupe",
               "mtllib AarisRoadGT.mtl", "o AarisRoadGT_"+group, "s 1"]
        for v in part["v"]:
            lines.append("v "+" ".join(f"{x:.6f}" for x in v))
        for n in part["vn"]:
            lines.append("vn "+" ".join(f"{x:.6f}" for x in n))
        for i,(name,_,_,_) in enumerate(MATS):
            faces=part["f"].get(i,[])
            if not faces:continue
            lines.append("usemtl "+name)
            lines.extend(f"f {a}//{a} {b}//{b} {c}//{c}" for a,b,c in faces)
        (output/f"AarisRoadGT_{group}.obj").write_text("\n".join(lines)+"\n")
    report={
        "license":"CC0 1.0 Universal",
        "source":SOURCE_PAGE,"sha256":SOURCE_SHA256,
        "originalTriangles":counts["raw"],"removedInteriorDetailTriangles":counts["skipped"],
        "mobileVariantTriangles":tris,
        "proceduralAlloySpokeTriangles":added_rim_triangles,
        "wheelTriangles":{k:data[k]["count"] for k in GROUPS[1:]},
        "bodyTriangles":data["body"]["count"],
        "originalTextureCount":12,
        "originalTextureResolution":"2048x2048",
        "newGpuTextureCount":0,
        "type":"unbranded closed-body GT, four separate wheel pivots",
        "limits":"Requires Unity visual/physics validation, not yet Android FPS proof",
    }
    (output/"geometry-report.json").write_text(json.dumps(report,indent=2)+"\n")
    return report

if __name__=="__main__":
    a=argparse.ArgumentParser()
    a.add_argument("input_glb",type=Path)
    a.add_argument("output",type=Path)
    args=a.parse_args()
    print(json.dumps(convert(args.input_glb,args.output),indent=2))
