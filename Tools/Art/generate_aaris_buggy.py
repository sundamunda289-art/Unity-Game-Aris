#!/usr/bin/env python3
"""Generate Aaris' original, lightweight open-cockpit off-road buggy OBJ.

Art-only offline build: real four-wheel physics/visual wheels are sourced from
the existing verified local FBX. No paid or third-party vehicle mesh required.
"""
from pathlib import Path
from collections import OrderedDict
from math import sqrt, sin, cos, pi
from uuid import uuid5, NAMESPACE_URL
import re

ROOT=Path(__file__).resolve().parents[2]
OUT=ROOT/"Assets/Resources/AarisVehicles/Authored"
OBJ=OUT/"AarisBuggy.obj"
MTL=OUT/"AarisBuggy.mtl"
PALETTE=OrderedDict((
 ("AarisPaint",(.23,.34,.30)),
 ("AarisDark",(.045,.052,.058)),
 ("AarisMetal",(.43,.49,.51)),
 ("AarisSeat",(.10,.105,.10)),
 ("AarisGlass",(.08,.12,.15)),
 ("AarisHeadlight",(.84,.89,.85)),
 ("AarisTaillight",(.60,.04,.027)),
 ("AarisLampClearCover",(.64,.72,.78)),
 ("AarisLampRedCover",(.68,.055,.03)),
))
vertices=[]
faces={name:[] for name in PALETTE}
def mesh(mat,points,tris):
    offset=len(vertices)+1
    vertices.extend(points)
    faces[mat].extend(tuple(offset+i for i in tri) for tri in tris)
def box(mat,center,size):
    x,y,z=(n/2 for n in size);cx,cy,cz=center
    v=[(cx+a*x,cy+b*y,cz+c*z)
       for a,b,c in ((-1,-1,-1),(1,-1,-1),(-1,1,-1),(1,1,-1),
                     (-1,-1,1),(1,-1,1),(-1,1,1),(1,1,1))]
    t=((0,2,1),(1,2,3),(4,5,6),(5,7,6),(0,1,4),
       (1,5,4),(2,6,3),(3,6,7),(0,4,2),(2,4,6),
       (1,3,5),(3,7,5))
    mesh(mat,v,t)
def tube(mat,a,b,r=.02,sides=8):
    d=tuple(b[i]-a[i] for i in range(3))
    length=sqrt(sum(v*v for v in d))
    if length<.0001:return
    f=tuple(v/length for v in d)
    up=(0,1,0) if abs(f[1])<.87 else (0,0,1)
    x=(f[1]*up[2]-f[2]*up[1],f[2]*up[0]-f[0]*up[2],
       f[0]*up[1]-f[1]*up[0])
    n=sqrt(sum(v*v for v in x));x=tuple(v/n for v in x)
    y=(f[1]*x[2]-f[2]*x[1],f[2]*x[0]-f[0]*x[2],
       f[0]*x[1]-f[1]*x[0])
    v=[]
    for center in (a,b):
        for i in range(sides):
            theta=2*pi*i/sides
            v.append(tuple(center[j]+r*(cos(theta)*x[j]+sin(theta)*y[j])
                           for j in range(3)))
    t=[]
    for i in range(sides):
        k=(i+1)%sides
        t.extend(((i,k,sides+i),(k,sides+k,sides+i)))
    for i in range(1,sides-1):
        t.extend(((0,i+1,i),(sides,sides+i,sides+i+1)))
    mesh(mat,v,t)
def slant(mat,front,rear,frontY,rearY,width,height=.045):
    # Tapered hood/deflector panel with wedge-like longitudinal profile.
    hw=width*.5; hf=width*.42
    v=[(-hw,frontY-height,front),(hw,frontY-height,front),
       (-hf,rearY-height,rear),(hf,rearY-height,rear),
       (-hw,frontY,front),(hw,frontY,front),
       (-hf,rearY,rear),(hf,rearY,rear)]
    t=((0,1,2),(1,3,2),(4,6,5),(5,6,7),
       (0,2,4),(2,6,4),(1,5,3),(3,5,7),
       (2,3,6),(3,7,6),(0,4,1),(1,4,5))
    mesh(mat,v,t)

# Low-slung original buggy chassis. A separate shared wheel mesh below
# is instantiated at the four existing WheelCollider poses.
box("AarisDark",(0,.48,0),(1.25,.21,2.76))
box("AarisMetal",(0,.35,0),(1.17,.13,2.68))
slant("AarisPaint",1.48,.52,.66,.83,1.53,.12)  # hood
box("AarisPaint",(0,.62,-1.12),(1.57,.29,.76)) # rear motor cover
box("AarisMetal",(0,.77,-1.44),(1.31,.21,.15))
box("AarisDark",(0,.91,-1.23),(1.12,.28,.30))
# Widened but open fender lips sit above the separate physical wheel art.
for sign in (-1,1):
    x=sign*.70
    # Recessed curved mudguards follow the swept tyre rather than
    # leaving a bright gap through flat hovering fender blocks.
    for axle in (1.10,-1.10):
        for i in range(11):
            a=.18+i*(2.78/11);b=.18+(i+1)*(2.78/11)
            def lip(theta, lateral, radius):
                return (sign*lateral,.36+sin(theta)*radius,axle+cos(theta)*radius)
            v=[lip(a,.57,.47),lip(b,.57,.47),lip(a,.93,.47),lip(b,.93,.47),
               lip(a,.57,.445),lip(b,.57,.445),lip(a,.93,.445),lip(b,.93,.445)]
            tris=((0,1,2),(2,1,3),(4,6,5),(6,7,5),
                  (0,2,4),(2,6,4),(3,1,7),(1,5,7),(0,4,1),(1,4,5),
                  (2,3,6),(3,7,6))
            mesh("AarisPaint",v,tris if sign>0 else tuple((i,k,j) for i,j,k in tris))
    box("AarisDark",(x,.47,0),(.21,.22,1.14))
    box("AarisMetal",(sign*.74,.48,1.52),(.20,.15,.18))
    box("AarisMetal",(sign*.74,.48,-1.53),(.20,.15,.18))
    # Outer protective rails and rear bumper braces.
    for z in (-1.30,1.30):
        tube("AarisMetal",(sign*.74,.50,z),(sign*.82,.57,z),.029)
    tube("AarisMetal",(sign*.74,.50,-1.50),(sign*.74,.50,1.50),.028)
    tube("AarisMetal",(sign*.67,.49,.58),(sign*.70,1.20,-.54),.038)
    tube("AarisMetal",(sign*.67,1.20,-.54),(sign*.66,1.57,-.60),.033)
    tube("AarisMetal",(sign*.71,.49,-1.28),(sign*.66,1.57,-.60),.032)
    tube("AarisMetal",(sign*.62,1.57,.46),(sign*.66,1.57,-.60),.034)
    tube("AarisMetal",(sign*.62,1.57,.46),(sign*.70,.58,.85),.033)
    # One small side mirror per flank, no extra draw call.
    tube("AarisMetal",(sign*.65,1.13,.52),(sign*.83,1.20,.53),.019)
    box("AarisDark",(sign*.83,1.20,.55),(.15,.095,.12))
    box("AarisGlass",(sign*.83,1.20,.611),(.12,.068,.008))

# Real open cockpit: two detailed bucket seats, roll cage + cross bars.
for sign in (-1,1):
    x=sign*.31
    box("AarisSeat",(x,.74,-.41),(.46,.15,.43))
    box("AarisSeat",(x,1.03,-.69),(.45,.59,.12))
    box("AarisDark",(x,1.12,-.624),(.28,.30,.025))
    box("AarisMetal",(x,.91,-.02),(.05,.44,.14))
    tube("AarisMetal",(x*.88,1.08,-.91),(x*.88,1.56,-.60),.023)
tube("AarisMetal",(-.66,1.57,-.60),(.66,1.57,-.60),.037)
tube("AarisMetal",(-.62,1.57,.46),(.62,1.57,.46),.033)
tube("AarisMetal",(0,1.57,.46),(0,1.57,-.60),.025)
# Thin tinted wind deflector tucked inside the structural roll cage.
slant("AarisGlass",.60,.37,1.00,1.44,1.10,.019)
box("AarisDark",(0,.91,.15),(.24,.11,.33))  # center console
# Stainless corner bumpers, work lights and restrained rear optics.
tube("AarisMetal",(-.67,.49,1.58),(.67,.49,1.58),.043)
tube("AarisMetal",(-.67,.51,-1.59),(.67,.51,-1.59),.040)
for sign in (-1,1):
    box("AarisDark",(sign*.57,.74,1.474),(.30,.21,.061))
    for delta in (-.058,.058):
        x=sign*.57+delta
        tube("AarisMetal",(x,.748,1.492),(x,.748,1.522),.045,12)
        tube("AarisHeadlight",(x,.748,1.520),(x,.748,1.532),.018,10)
    box("AarisLampClearCover",(sign*.57,.75,1.541),(.245,.123,.010))
    box("AarisDark",(sign*.56,.76,-1.487),(.30,.18,.04))
    for delta in (-.048,.048):
        x=sign*.56+delta
        tube("AarisTaillight",(x,.766,-1.517),(x,.766,-1.533),.031,10)
    box("AarisLampRedCover",(sign*.56,.765,-1.545),(.244,.099,.011))
    # Bright mechanical shock struts behind wheels (visual only).
    tube("AarisMetal",(sign*.53,.45,-1.0),(sign*.63,.79,-.73),.021)
    tube("AarisMetal",(sign*.53,.45,1.07),(sign*.63,.79,.79),.021)
# Engine grille bars use the same metal material.
for x in (-.39,-.19,0,.19,.39):
    tube("AarisDark",(x,.81,-1.523),(x,.91,-1.523),.010,6)

OUT.mkdir(parents=True,exist_ok=True)
lines=["# Aaris original open-buggy body, authored locally, CC0-compatible",
       "mtllib AarisBuggy.mtl","o AarisBuggy"]
lines.extend("v %.6f %.6f %.6f"%v for v in vertices)
for name,tris in faces.items():
    if not tris:continue
    lines.extend(("usemtl "+name,"s 1" if name in
                 ("AarisMetal","AarisGlass") else "s off"))
    lines.extend("f %d %d %d"%tri for tri in tris)
OBJ.write_text("\n".join(lines)+"\n")
material=["# Original authored paint, rubber and glass. URP material conversion happens once per type."]
for name,rgb in PALETTE.items():
    material.extend(["newmtl "+name,"Kd %.3f %.3f %.3f"%rgb,
                     "Ks .30 .30 .30","Ns 180"])
    if name=="AarisGlass":material.append("d 0.32")
    material.append("")
MTL.write_text("\n".join(material).rstrip()+"\n")
template=(OUT/"QuaterniusSports.obj.meta").read_text()
guid=uuid5(NAMESPACE_URL,"aaris-original-offroad-buggy-obj-v1").hex
meta=re.sub(r"(?m)^guid: [a-f0-9]{32}$","guid: "+guid,template)
Path(str(OBJ)+".meta").write_text(meta)
mtl_meta="fileFormatVersion: 2\nguid: "+uuid5(NAMESPACE_URL,"aaris-original-offroad-buggy-mtl-v1").hex+"\n"
Path(str(MTL)+".meta").write_text(mtl_meta)
print("AARIS_ORIGINAL_BUGGY",len(vertices),"vertices",
      sum(map(len,faces.values())),"triangles",
      len(PALETTE),"materials",OBJ.stat().st_size,"bytes")

# Shared symmetric wheel with real sidewall / crown, staggered mud tread,
# cast spokes and five lugs on each face. WheelCollider uses the real mesh
# diameter; the mesh stays centred at the origin for correct steering.
wheel = OUT / "AarisBuggyWheel.obj"
wheel_mtl = OUT / "AarisBuggyWheel.mtl"
vertices.clear()
faces = {name: [] for name in ("AarisTyre", "AarisAlloy", "AarisDark", "AarisMetal")}
profile = [(-.135,.236),(-.175,.290),(-.176,.336),(-.144,.378),
           (-.085,.390),(0,.392),(.085,.390),(.144,.378),(.176,.336),
           (.175,.290),(.135,.236)]
points=[]
segments=48
for turn in range(segments):
    a=turn*2*pi/segments
    for j,(x,r) in enumerate(profile):
        if 3 <= j <= 7 and (turn+abs(j-5))%4 == 0:
            r-=.010
        points.append((x,sin(a)*r,cos(a)*r))
n=len(profile)
tris=[]
for i in range(segments):
    for j in range(n-1):
        a=i*n+j;b=((i+1)%segments)*n+j
        tris.extend(((a,a+1,b),(b,a+1,b+1)))
mesh("AarisTyre",points,tris)

for side in (-1,1):
    # Circumferential bevels stay open between the spokes. The dished
    # backing visible through them is the darker stamped brake rotor.
    lip=[(.115,.219),(.142,.232),(.160,.231),(.165,.218),(.148,.205)]
    n=len(lip)
    points=[(side*x,sin(i*2*pi/32)*r,cos(i*2*pi/32)*r)
            for i in range(32) for x,r in lip]
    tris=[]
    for i in range(32):
        for j in range(n-1):
            a=i*n+j;b=((i+1)%32)*n+j
            t=((a,a+1,b),(b,a+1,b+1))
            tris.extend(t if side==-1 else ((a,c,b) for a,b,c in t))
    mesh("AarisAlloy",points,tris)
    tube("AarisDark",(side*.113,0,0),(side*.128,0,0),.159,24)
    tube("AarisAlloy",(side*.129,0,0),(side*.175,0,0),.074,16)
    for i in range(5):
        a=i*2*pi/5
        tube("AarisAlloy",(side*.148,sin(a)*.053,cos(a)*.053),
             (side*.147,sin(a+.20)*.208,cos(a+.20)*.208),.021,7)
        tube("AarisMetal",(side*.177,sin(a)*.045,cos(a)*.045),
             (side*.188,sin(a)*.045,cos(a)*.045),.010,6)
lines=["# Original Aaris off-road wheel","mtllib AarisBuggyWheel.mtl","o AarisBuggyWheel"]
lines.extend("v %.6f %.6f %.6f"%v for v in vertices)
for name,tris in faces.items():
    lines.extend(("usemtl "+name,"s 1"))
    lines.extend("f %d %d %d"%tri for tri in tris)
wheel.write_text("\n".join(lines)+"\n")
wheel_mtl.write_text("newmtl AarisTyre\nKd .063 .068 .072\n\n"
                     "newmtl AarisAlloy\nKd .47 .50 .53\n\n"
                     "newmtl AarisDark\nKd .13 .14 .15\n\n"
                     "newmtl AarisMetal\nKd .39 .42 .45\n")
for path in (wheel,wheel_mtl):
    guid=uuid5(NAMESPACE_URL,"aaris-original-buggy-wheel-"+path.suffix).hex
    metadata=re.sub(r"(?m)^guid: [a-f0-9]{32}$","guid: "+guid,template) if path==wheel \
             else "fileFormatVersion: 2\nguid: "+guid+"\n"
    Path(str(path)+".meta").write_text(metadata)
