#!/usr/bin/env python3
"""Generate the original small seamless stone surface for Aaris house fascias.

This is offline asset authoring. Neither mobile nor Unity runtime uses Python.
"""
import math
import random
import re
import uuid
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
ASSET = ROOT / "Assets/Resources/AarisBuildings/ArchitecturalLimestone.png"
META = Path(str(ASSET) + ".meta")
REFERENCE = ROOT / "Assets/AarisWorld/Surfaces/Concrete.png.meta"
SIZE = 128
NORMAL_SLOPE = 0.044
SEED = 0xA512026
rng = random.Random(SEED)
harmonics = []
for scale, amplitude in ((2,7.4), (4,5.1), (8,2.5), (16,1.3), (32,.7)):
    for _ in range(6):
        kx = rng.randint(0,scale)
        ky = rng.randint(0,scale)
        if not kx and not ky: kx = 1
        harmonics.append((kx, ky, rng.random()*2*math.pi, amplitude/6))
colors = []
height = []
for y in range(SIZE):
    for x in range(SIZE):
        grain = 0.0
        for kx,ky,phase,amp in harmonics:
            grain += amp*math.sin(2*math.pi*(kx*x+ky*y)/SIZE+phase)
        # Subtle 64px stone course, weathered toward the horizontal seams.
        joint = min(y%64, 64-(y%64))
        seam = -4.5 if joint < 1.5 else (-1.2 if joint < 4 else 0.0)
        veining = 2.0*math.sin(2*math.pi*(x*3+y*.25)/SIZE)
        base = max(192, min(244, round(220 + grain + seam + veining)))
        height.append((base+5+base+2+base-5)/3.0)
        # Rain tracks follow the long axis of the surface. Keep coloration
        # separate from stone relief so stains do not create raised edges.
        angle_x = 2*math.pi*x/SIZE
        angle_y = 2*math.pi*y/SIZE
        streak = max(0.0, math.sin(5*angle_x + .27*math.sin(2*angle_y) +
                                    .63*math.cos(3*angle_x))) ** 3
        runoff = 5.0*streak*(.65+.35*math.cos(angle_y+angle_x))
        soot = 2.1 * (1 + math.cos(3*angle_x-angle_y))
        tones = (base+5-runoff*.79-soot, base+2-runoff-soot, base-5-runoff*1.08-soot*.73)
        colors.append(tuple(round(max(0,min(255,v))) for v in tones))

ASSET.parent.mkdir(parents=True,exist_ok=True)
picture = Image.new("RGB", (SIZE,SIZE))
picture.putdata(colors)
picture.save(ASSET,optimize=True)

# Tiny shared tileable tangent-space normal map: derived offline from
# the same stone height variation. It is one 128px texture for every
# architectural trim mesh, not a per-building material or runtime bake.
normal_asset = ASSET.with_name("ArchitecturalLimestoneNormal.png")
normal_pixels=[]
for py in range(SIZE):
    for px in range(SIZE):
        def h(x,y):
            return height[(y%SIZE)*SIZE+(x%SIZE)]
        dx=(h(px+1,py)-h(px-1,py))*NORMAL_SLOPE
        dy=(h(px,py+1)-h(px,py-1))*NORMAL_SLOPE
        nx,ny,nz=-dx,-dy,1.0
        length=math.sqrt(nx*nx+ny*ny+nz*nz)
        normal_pixels.append((round(127.5+127.5*nx/length),
                              round(127.5+127.5*ny/length),
                              round(127.5+127.5*nz/length)))
normal_image=Image.new("RGB",(SIZE,SIZE))
normal_image.putdata(normal_pixels)
normal_image.save(normal_asset,optimize=True)
# Retain the existing production project TextureImporter settings for
# trilinear filtering, repeat wrapping, Android compression and mip streaming.
meta = REFERENCE.read_text()
meta = re.sub(r"(?m)^guid: [0-9a-f]{32}$",
              "guid: " + uuid.uuid5(uuid.NAMESPACE_URL,"aaris-limestone-trim-v1").hex,
              meta)
meta = meta.replace("maxTextureSize: 2048", "maxTextureSize: 256")
meta = meta.replace("maxTextureSize: 1024", "maxTextureSize: 256")
META.write_text("\n".join(line.rstrip() for line in meta.splitlines())+"\n")
normal_meta = re.sub(r"(?m)^guid: [0-9a-f]{32}$",
    "guid: " + uuid.uuid5(uuid.NAMESPACE_URL,
                          "aaris-limestone-normal-v1").hex,meta)
normal_meta=normal_meta.replace("textureType: 0","textureType: 1")
normal_meta=normal_meta.replace("sRGBTexture: 1","sRGBTexture: 0")
normal_meta=normal_meta.replace("linearTexture: 0","linearTexture: 1")
normal_asset.with_name(normal_asset.name+".meta").write_text(
    "\n".join(line.rstrip() for line in normal_meta.splitlines())+"\n")
print("LIMESTONE_ORIGINAL",ASSET.stat().st_size,"bytes",
      "PNG",SIZE,"x",SIZE,"palette_range",
      tuple(min(c[i] for c in colors) for i in range(3)),
      tuple(max(c[i] for c in colors) for i in range(3)))
