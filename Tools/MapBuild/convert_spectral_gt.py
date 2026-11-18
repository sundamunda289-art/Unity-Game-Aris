#!/usr/bin/env python3
"""Offline, deterministic glTF binary -> lightweight Unity OBJ GT source.
Authoring-only: run on a verified CC0 input under /tmp, never at game runtime.
"""
import argparse
import hashlib
import json
import re
import struct
from collections import defaultdict
from pathlib import Path

SOURCE_SHA256 = "e7b7f0d9de0c3bc3897c8134b1ceae5631b1da2690d336df18c3ea82e7238c73"
SOURCE_URL = "https://github.com/JaronKBragg7337/spectral-gt-rs"
EXCLUDE = re.compile(
    r"(^Badge_|^Plate_Text|Coil_Spring|Sidewall_Bead|Tire_Block|"
    r"Brake_Line|^Engine_|^Rear_.*Damper|^Front_.*Damper|"
    r"^Rear_.*Control|^Front_.*Control|^Rear_.*Toe|^Front_.*Toe|"
    r"^Undertray|Subframe|Radiator|^Wheel_Hub$|^Wheel_Spoke)",
    re.I,
)
MATERIAL_NAMES = [
    "AarisPaint", "AarisGlass", "AarisDisplay", "AarisRubberTrim",
    "AarisAlloy", "AarisDarkPanel", "AarisCarbon", "AarisGold",
    "AarisIceBlue", "AarisRubber", "AarisSidewallInk",
    "AarisAluminium", "AarisCaliperRed", "AarisLeather",
    "AarisCockpitDisplay", "AarisLeatherBurgundy", "AarisAmber",
    "AarisHeadlight", "AarisTaillight", "AarisSuspension",
    "AarisEngineMetal", "AarisSpringMetal", "AarisCoolant",
]
WHEEL_CENTERS = {
    "wheel_fl": (-1.015, .53, 1.37),
    "wheel_fr": (1.015, .53, 1.37),
    "wheel_rl": (-1.015, .53, -1.48),
    "wheel_rr": (1.015, .53, -1.48),
}

def model_group(name):
    for before, group in (
        ("Front_Left_", "wheel_fl"), ("Front_Right_", "wheel_fr"),
        ("Rear_Left_", "wheel_rl"), ("Rear_Right_", "wheel_rr"),
    ):
        if name.startswith(before):
            return group
    return "body"

def load_glb(path):
    raw = path.read_bytes()
    if hashlib.sha256(raw).hexdigest() != SOURCE_SHA256:
        raise ValueError("Unverified source GLB SHA-256; refusing mismatched asset")
    if raw[:4] != b"glTF" or struct.unpack_from("<I", raw, 4)[0] != 2:
        raise ValueError("Expected glTF 2.0 binary")
    if struct.unpack_from("<I", raw, 8)[0] != len(raw):
        raise ValueError("GLB length mismatch")
    length, kind = struct.unpack_from("<I4s", raw, 12)
    if kind != b"JSON":
        raise ValueError("First chunk must be JSON")
    doc = json.loads(raw[20:20 + length])
    off = 20 + length
    size, kind = struct.unpack_from("<I4s", raw, off)
    if kind != b"BIN\x00":
        raise ValueError("Second chunk must be BIN")
    binary = raw[off + 8:off + 8 + size]
    if doc.get("extensionsRequired"):
        raise ValueError("Unsupported required glTF extensions")
    return doc, binary

def accessor(doc, binary, index):
    spec = doc["accessors"][index]
    if spec.get("sparse") or "bufferView" not in spec:
        raise ValueError("Sparse and implicit accessors not supported")
    view = doc["bufferViews"][spec["bufferView"]]
    kind = spec["componentType"]
    comp = {5121: "B", 5123: "H", 5125: "I", 5126: "f"}.get(kind)
    n = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}.get(spec["type"])
    if comp is None or n is None:
        raise ValueError("Unsupported accessor component/type")
    fmt = "<" + comp * n
    size = struct.calcsize(fmt)
    stride = view.get("byteStride", size)
    if stride < size:
        raise ValueError("Invalid buffer stride")
    start = view.get("byteOffset", 0) + spec.get("byteOffset", 0)
    stop = start + (spec["count"] - 1) * stride + size
    if stop > len(binary):
        raise ValueError("Accessor crosses binary buffer")
    return [struct.unpack_from(fmt, binary, start + k * stride)
            for k in range(spec["count"])]

def matmul(a, b):
    # Row-major 4x4; column-vector vertices.
    return [[sum(a[i][k] * b[k][j] for k in range(4))
             for j in range(4)] for i in range(4)]

def node_matrix(node):
    if "matrix" in node:
        m = node["matrix"]
        return [[m[4 * j + i] for j in range(4)] for i in range(4)]
    x, y, z, w = node.get("rotation", [0, 0, 0, 1])
    sx, sy, sz = node.get("scale", [1, 1, 1])
    tx, ty, tz = node.get("translation", [0, 0, 0])
    return [
        [(1-2*(y*y+z*z))*sx, 2*(x*y-z*w)*sy, 2*(x*z+y*w)*sz, tx],
        [2*(x*y+z*w)*sx, (1-2*(x*x+z*z))*sy, 2*(y*z-x*w)*sz, ty],
        [2*(x*z-y*w)*sx, 2*(y*z+x*w)*sy, (1-2*(x*x+y*y))*sz, tz],
        [0, 0, 0, 1],
    ]

def world_matrices(doc):
    nodes = doc["nodes"]
    parents = {}
    for i, node in enumerate(nodes):
        for child in node.get("children", []):
            if child in parents:
                raise ValueError("Multiple glTF parents")
            parents[child] = i
    cache = {}
    def matrix(i):
        if i not in cache:
            local = node_matrix(nodes[i])
            cache[i] = (matmul(matrix(parents[i]), local)
                        if i in parents else local)
        return cache[i]
    return [matrix(i) for i in range(len(nodes))]

def transform(m, xyz):
    x, y, z = xyz[:3]
    gx = m[0][0]*x + m[0][1]*y + m[0][2]*z + m[0][3]
    gy = m[1][0]*x + m[1][1]*y + m[1][2]*z + m[1][3]
    gz = m[2][0]*x + m[2][1]*y + m[2][2]*z + m[2][3]
    # RH +X-nose => Unity +Z-nose; RH +Z-left => Unity -X-left.
    return (-gz, gy, gx)

def write_mtl(doc, output):
    lines = ["# Authored from Spectral GT RS CC0 material factors; no runtime fetches."]
    for i, name in enumerate(MATERIAL_NAMES):
        m = doc["materials"][i]
        pbr = m.get("pbrMetallicRoughness", {})
        color = pbr.get("baseColorFactor", [1, 1, 1, 1])
        if i == 6 and "baseColorFactor" not in pbr:
            color = [.035, .04, .045, 1]
        roughness = pbr.get("roughnessFactor", .45)
        lines += [f"newmtl {name}", "Ka 0 0 0",
                  "Kd " + " ".join(format(c, ".6f") for c in color[:3]),
                  "Ks 0.15 0.15 0.15",
                  f"Ns {min(1000, max(1, 1000 * (1 - roughness)**2)):.2f}",
                  "illum 2", ""]
    output.write_text("\n".join(lines) + "\n")

def convert(glb, dest, max_triangles=80000):
    doc, binary = load_glb(glb)
    if len(doc.get("materials", [])) != len(MATERIAL_NAMES):
        raise ValueError("Unexpected material layout")
    world = world_matrices(doc)
    out = {k: {"vertices": [], "faces": defaultdict(list), "count": 0}
           for k in ["body", "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr"]}
    original = skipped = 0
    for i, node in enumerate(doc["nodes"]):
        if "mesh" not in node:
            continue
        name = node.get("name", "")
        group = model_group(name)
        omit = EXCLUDE.search(name) is not None
        for primitive in doc["meshes"][node["mesh"]]["primitives"]:
            if primitive.get("mode", 4) != 4:
                raise ValueError("Only triangle primitives supported")
            idx = (accessor(doc, binary, primitive["indices"])
                   if "indices" in primitive else
                   [(k,) for k in range(len(accessor(
                       doc, binary, primitive["attributes"]["POSITION"])))])
            if len(idx) % 3:
                raise ValueError("Triangle index count mismatch")
            tri = len(idx) // 3
            original += tri
            if omit:
                skipped += tri
                continue
            material = primitive.get("material", 0)
            if not 0 <= material < len(MATERIAL_NAMES):
                raise ValueError("Unexpected material index")
            part = out[group]
            positions = accessor(doc, binary, primitive["attributes"]["POSITION"])
            offset = len(part["vertices"])
            pivot = WHEEL_CENTERS.get(group, (0, 0, 0))
            vertices = [transform(world[i], p) for p in positions]
            part["vertices"].extend(
                (x - pivot[0], y - pivot[1], z - pivot[2])
                for x, y, z in vertices)
            for k in range(0, len(idx), 3):
                face = tuple(offset + idx[k + t][0] + 1 for t in (0, 1, 2))
                if any(z < offset + 1 or z > len(part["vertices"]) for z in face):
                    raise ValueError("Invalid source index")
                part["faces"][MATERIAL_NAMES[material]].append(face)
            part["count"] += tri
    selected = original - skipped
    if selected > max_triangles:
        raise ValueError(f"Premium GT too expensive for target profile: {selected}")
    if original < 100000 or selected < 40000:
        raise ValueError("Unexpected model geometry budget")
    if any(out[w]["count"] < 3000 for w in WHEEL_CENTERS):
        raise ValueError("A wheel lost its visible geometry")
    dest.mkdir(parents=True, exist_ok=True)
    write_mtl(doc, dest / "SpectralGT.mtl")
    for group, part in out.items():
        lines = ["# Spectral GT RS, CC0, optimized for Android source art",
                 "mtllib SpectralGT.mtl", "o AarisSpectralGT_" + group]
        for x, y, z in part["vertices"]:
            lines.append(f"v {x:.6f} {y:.6f} {z:.6f}")
        for material in MATERIAL_NAMES:
            faces = part["faces"].get(material, [])
            if not faces:
                continue
            lines.append("usemtl " + material)
            lines.extend(f"f {a} {b} {c}" for a, b, c in faces)
        (dest / f"SpectralGT_{group}.obj").write_text("\n".join(lines) + "\n")
    report = {
        "sourceSha256": SOURCE_SHA256, "license": "CC0 1.0 Universal",
        "source": SOURCE_URL, "originalTriangles": original,
        "discardedTriangles": skipped, "selectedTriangles": selected,
        "groups": {k: v["count"] for k, v in out.items()},
        "note": "Offline authoring prototype only; Unity rendering/physics/Android FPS unverified.",
    }
    (dest / "geometry-report.json").write_text(json.dumps(report, indent=2) + "\n")
    return report

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("glb", type=Path, help="Previously downloaded CC0 original GLB")
    parser.add_argument("destination", type=Path)
    parser.add_argument("--max-triangles", type=int, default=80000)
    args = parser.parse_args()
    print(json.dumps(convert(args.glb, args.destination, args.max_triangles), indent=2))

if __name__ == "__main__":
    main()
