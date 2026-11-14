#!/usr/bin/env python3
"""Offline authoring: bake seven windowless, door-leaf-free Kenney CC0 houses.

Run once from the Unity repository root:
  python3 Tools/Buildings/generate_kenney_windowless.py

This does not mutate any existing prefab, scene, material or terrain.
The Unity Editor menu Aaris/Houses then previews and saves scene replacements.
"""
from __future__ import annotations

import hashlib
import io
import sys
import urllib.request
import zipfile
from pathlib import Path

SHA256 = "47e1614686b4c0fe55190e88b22ff2b8b10935edf61f37f478a9b6a99477edc6"
URL = "https://opengameart.org/sites/default/files/kenney_modular-buildings.zip"
ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "Assets/AarisWorld/Models/KenneyWindowlessHouses"
CACHE = Path("/tmp/aaris-houses-thirdparty/kenney_modular-buildings.zip")
PARTS = ("building-block", "roof-gable", "roof-flat-top", "roof-flat-border-straight")
# House wall heights in normalized template metres. All share a 10m x 8m footprint.
STYLES = (
    ("bungalow_house", 4.5, "gable"),
    ("pitched_roof_cottage", 4.3, "gable"),
    ("a_frame_cabin", 4.1, "steep"),
    ("detached_two_storey_house", 6.4, "gable"),
    ("garage_apartment", 6.1, "flat"),
    ("two_bedroom_garage_house", 5.2, "gable"),
    ("row_three_townhouses", 6.2, "flat"),
)


def get_zip() -> zipfile.ZipFile:
    if CACHE.is_file() and hashlib.sha256(CACHE.read_bytes()).hexdigest() == SHA256:
        payload = CACHE.read_bytes()
    else:
        with urllib.request.urlopen(urllib.request.Request(
                URL, headers={"User-Agent": "Aaris-Kenney-CC0-asset-import/1.0"}),
                timeout=45) as response:
            payload = response.read(6 * 1024 * 1024)
    if hashlib.sha256(payload).hexdigest() != SHA256:
        raise RuntimeError("Untrusted Kenney archive: SHA-256 mismatch")
    archive = zipfile.ZipFile(io.BytesIO(payload))
    if archive.testzip() is not None:
        raise RuntimeError("Kenney ZIP CRC verification failed")
    return archive


def parse_obj(archive: zipfile.ZipFile, name: str):
    text = archive.read("Models/OBJ format/" + name + ".obj").decode("utf-8")
    vertices, uvs, faces = [], [], []
    for line in text.splitlines():
        parts = line.split()
        if not parts:
            continue
        if parts[0] == "v":
            vertices.append(tuple(map(float, parts[1:4])))
        elif parts[0] == "vt":
            uvs.append(tuple(map(float, parts[1:3])))
        elif parts[0] == "f":
            face = []
            for pair in parts[1:]:
                indices = pair.split("/")
                face.append((int(indices[0]) - 1, int(indices[1]) - 1))
            faces.append(face)
    if not vertices or not uvs or not faces:
        raise RuntimeError("Malformed Kenney OBJ: " + name)
    return vertices, uvs, faces


# Seven style-specific facade tones sampled from the SAME Kenney 512px atlas.
# One URP material remains shared: no added draw calls or textures.
# U offsets are in opaque lower-row swatches (Unity UV V=0 at image bottom).
FACADE_U = {
    "bungalow_house": 0.94,             # warm ivory
    "pitched_roof_cottage": 0.81,       # sandstone
    "a_frame_cabin": 0.68,              # timber brown
    "detached_two_storey_house": 0.43,  # pale plaster
    "garage_apartment": 0.30,          # blue-gray
    "two_bedroom_garage_house": 0.55,   # terracotta
    "row_three_townhouses": 0.16,      # slate
}


def bake_mesh(parts, slug: str, wall_height: float, roof_type: str) -> str:
    vertices, texcoords, faces = [], [], []

    def add(part: str, lo, hi):
        src_v, src_uv, src_f = parts[part]
        mins = [min(p[i] for p in src_v) for i in range(3)]
        spans = [max(p[i] for p in src_v) - mins[i] for i in range(3)]
        if min(spans) <= 0:
            raise RuntimeError("Degenerate Kenney source mesh: " + part)
        vo, uo = len(vertices), len(texcoords)
        for v in src_v:
            vertices.append(tuple(
                lo[i] + (v[i] - mins[i]) / spans[i] * (hi[i] - lo[i])
                for i in range(3)))
        if part == "building-block":
            texcoords.extend((FACADE_U[slug], v) for _, v in src_uv)
        else:
            texcoords.extend(src_uv)
        faces.extend([[(vo + vi + 1, uo + ui + 1) for vi, ui in f] for f in src_f])

    w, d, h = 10.0, 8.0, wall_height
    gap, thick, passage_height = 1.12, 0.22, 2.62
    # No windows and no opening/closing door panel: free front passage.
    add("building-block", (-w/2, 0, d/2-thick), (w/2, h, d/2))
    add("building-block", (-w/2, 0, -d/2), (-w/2+thick, h, d/2))
    add("building-block", (w/2-thick, 0, -d/2), (w/2, h, d/2))
    add("building-block", (-w/2, 0, -d/2), (-gap, h, -d/2+thick))
    add("building-block", (gap, 0, -d/2), (w/2, h, -d/2+thick))
    add("building-block", (-gap, passage_height, -d/2),
        (gap, h, -d/2+thick))
    add("building-block", (-w/2, -0.15, -d/2), (w/2, 0, d/2))
    add("building-block", (-1.55, -0.14, -d/2-0.55),
        (1.55, -0.03, -d/2+0.02))
    # Canopy, lintel, pilasters: small silhouette detail from Kenney roof pieces.
    add("roof-flat-border-straight", (-1.55, passage_height+0.12, -d/2-0.55),
        (1.55, passage_height+0.30, -d/2+0.25))
    for x in (-w/2+0.23, w/2-0.23):
        # Plain Kenney block pillars look cleaner, save 128 polygons/home.
        add("building-block", (x-0.10, 0, -d/2-0.12),
            (x+0.10, h-0.15, -d/2+0.15))
        add("building-block", (x-0.10, 0, d/2-0.15),
            (x+0.10, h-0.15, d/2+0.12))
    band = h*0.49 if h >= 6 else 0.55
    add("roof-flat-border-straight", (-w/2-0.04, band-0.08, -d/2-0.13),
        (w/2+0.04, band+0.08, -d/2+0.15))
    if roof_type == "flat":
        add("roof-flat-top", (-w/2-0.38, h, -d/2-0.38),
            (w/2+0.38, h+0.30, d/2+0.38))
        add("roof-flat-border-straight", (-w/2-0.4, h+0.25, -d/2-0.42),
            (w/2+0.4, h+0.42, -d/2+0.15))
    else:
        rise = 1.85 if roof_type == "steep" else 1.15
        add("roof-gable", (-w/2-0.45, h, -d/2-0.5),
            (w/2+0.45, h+rise, d/2+0.5))

    lines = [
        "# Kenney Modular Buildings 2.1 CC0; combined, windowless static home",
        "mtllib AarisWindowless.mtl",
        "o AarisWindowless_" + slug,
        "usemtl colormap",
    ]
    lines += ["v {:.5f} {:.5f} {:.5f}".format(*v) for v in vertices]
    lines += ["vt {:.8f} {:.8f}".format(*uv) for uv in texcoords]
    lines += ["f " + " ".join("{}/{}".format(vi, ui) for vi, ui in f) for f in faces]
    print("{}: {} vertices, {} polygons".format(slug, len(vertices), len(faces)))
    return "\n".join(lines) + "\n"


def main():
    archive = get_zip()
    parts = {name: parse_obj(archive, name) for name in PARTS}
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "colormap.png").write_bytes(archive.read(
        "Models/OBJ format/Textures/colormap.png"))
    (OUT / "Kenney-CC0-License.txt").write_bytes(archive.read("License.txt"))
    (OUT / "AarisWindowless.mtl").write_text(
        "newmtl colormap\nKa 1 1 1\nKd 1 1 1\nillum 2\nmap_Kd colormap.png\n")
    (OUT / "PROVENANCE.md").write_text(
        "Kenney Modular Buildings 2.1: https://kenney.nl/assets/modular-buildings\n"
        "CC0 1.0, see Kenney-CC0-License.txt.\n"
        "Source ZIP: " + URL + "\nSHA-256: " + SHA256 + "\n"
        "These are combined, single-mesh, doorless/windowless, author-time assets.\n"
        "Do not use this script for runtime downloads or procedural instantiation.\n")
    for name, height, roof in STYLES:
        (OUT / ("AarisWindowless_" + name + ".obj")).write_text(
            bake_mesh(parts, name, height, roof))
    print("AARIS_KENNEY_WINDOWLESS_7_ASSETS_READY", OUT)


if __name__ == "__main__":
    main()
