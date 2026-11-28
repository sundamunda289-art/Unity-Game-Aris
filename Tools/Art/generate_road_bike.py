#!/usr/bin/env python3
"""Original reproducible motorcycle OBJ for streamed, static roadside scenery."""
from collections import defaultdict
from math import sin, cos, pi, sqrt
from pathlib import Path
import uuid

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "Assets/Resources/AarisVehicles/Authored"
NAME = "AarisRoadBike"
PALETTE = {
 "Paint": (.095,.20,.34), "Tyre": (.035,.039,.045),
 "Rim": (.53,.58,.64), "Engine": (.17,.19,.22),
 "Seat": (.058,.063,.071), "Headlamp": (.95,.91,.74),
 "TailLamp": (.76,.055,.038), "Amber": (.92,.45,.04),
 "ClearCover": (.67,.74,.80), "RedCover": (.74,.08,.06),
}
verts=[]
faces=defaultdict(list)
def add(material, points, triangles):
 start=len(verts)+1
 verts.extend(points)
 faces[material].extend(tuple(start+n for n in face) for face in triangles)

def ring(material, cy, cz, major, minor, sides=32, profile=6, shift_x=0, tread=False):
 points=[]
 for u in range(sides):
  angle=2*pi*u/sides
  for v in range(profile):
   section=2*pi*v/profile
   crown = cos(section)
   groove = .0055 if tread and .32 < crown < .98 and (u + (v if v < profile / 2 else -v)) % 4 == 0 else 0
   radial=major+minor*crown-groove
   points.append((shift_x+minor*sin(section),
                  cy+radial*cos(angle), cz+radial*sin(angle)))
 tris=[]
 for u in range(sides):
  for v in range(profile):
   a=u*profile+v;b=((u+1)%sides)*profile+v
   c=u*profile+(v+1)%profile;d=((u+1)%sides)*profile+(v+1)%profile
   tris.extend(((a,b,c),(b,d,c)))
 add(material,points,tris)

def tube(material,a,b,radius,sides=10):
 delta=tuple(b[i]-a[i] for i in range(3))
 n=sqrt(sum(x*x for x in delta))
 if n<.001:return
 direction=tuple(x/n for x in delta)
 guide=(0,1,0) if abs(direction[1])<.90 else (1,0,0)
 side=(direction[1]*guide[2]-direction[2]*guide[1],
       direction[2]*guide[0]-direction[0]*guide[2],
       direction[0]*guide[1]-direction[1]*guide[0])
 sl=sqrt(sum(q*q for q in side));side=tuple(q/sl for q in side)
 up=(direction[1]*side[2]-direction[2]*side[1],
     direction[2]*side[0]-direction[0]*side[2],
     direction[0]*side[1]-direction[1]*side[0])
 points=[]
 for p in (a,b):
  for i in range(sides):
   t=2*pi*i/sides
   points.append(tuple(p[j]+radius*(cos(t)*side[j]+sin(t)*up[j])
                       for j in range(3)))
 tris=[]
 for i in range(sides):
  j=(i+1)%sides
  tris.extend(((i,j,sides+i),(j,sides+j,sides+i)))
 for i in range(1,sides-1):
  tris.extend(((0,i+1,i),(sides,sides+i,sides+i+1)))
 add(material,points,tris)

def box(material,center,size):
 cx,cy,cz=center;x,y,z=(v/2 for v in size)
 pts=[(cx+i*x,cy+j*y,cz+k*z)
      for i,j,k in ((-1,-1,-1),(1,-1,-1),(-1,1,-1),(1,1,-1),
                    (-1,-1,1),(1,-1,1),(-1,1,1),(1,1,1))]
 tris=((0,2,1),(1,2,3),(4,5,6),(5,7,6),
       (0,1,4),(1,5,4),(2,6,3),(3,6,7),
       (0,4,2),(2,4,6),(1,3,5),(3,7,5))
 add(material,pts,tris)

def ellipsoid(material,center,radii,longitude=20,latitude=8):
 cx,cy,cz=center;rx,ry,rz=radii
 points=[]
 for j in range(latitude+1):
  phi=-pi/2+pi*j/latitude
  for i in range(longitude):
   theta=2*pi*i/longitude
   points.append((cx+rx*cos(phi)*cos(theta),cy+ry*sin(phi),
                  cz+rz*cos(phi)*sin(theta)))
 tris=[]
 for j in range(latitude):
  for i in range(longitude):
   a=j*longitude+i;b=j*longitude+(i+1)%longitude
   c=(j+1)*longitude+i;d=(j+1)*longitude+(i+1)%longitude
   tris.extend(((a,c,b),(b,c,d)))
 add(material,points,tris)

def brake_ring(x, cy, cz, outer, inner, segments=28):
 points=[]
 for i in range(segments):
  a=2*pi*i/segments
  throat=inner+(.008 if i%4==2 else 0)
  for axial in (x-.004,x+.004):
   for radius in (outer,throat):
    points.append((axial,cy+radius*cos(a),cz+radius*sin(a)))
 tris=[]
 for i in range(segments):
  a=i*4;b=(i+1)%segments*4
  tris.extend(((a,a+1,b),(a+1,b+1,b),
               (a+2,b+2,a+3),(a+3,b+2,b+3),
               (a,b,a+2),(a+2,b,b+2),
               (a+1,a+3,b+1),(a+3,b+3,b+1)))
 add("Rim",points,tris)

# Axle assemblies. The cut tread is part of the tyre mesh, while open
# spokes leave the stamped steel rotors and fixed calipers visible.
for z in (-.78,.78):
 ring("Tyre",.36,z,.29,.065,48,10,tread=True)
 for side in (-1,1):
  ring("Tyre",.36,z,.265,.003,32,4,shift_x=side*.058)
  ring("Rim",.36,z,.226,.014,28,5,shift_x=side*.032)
 tube("Rim",(-.055,.36,z),(.055,.36,z),.060,16)
 for i in range(7):
  angle=2*pi*i/7
  y=.36+.217*cos(angle);wz=z+.217*sin(angle)
  tube("Rim",(0,.36+.025*cos(angle+.5),z+.025*sin(angle+.5)),(.012,y,wz),.013,6)
 disc_side = -.092 if z > 0 else .092
 brake_ring(disc_side,.36,z,.164 if z > 0 else .134,.105 if z > 0 else .088)
 tube("Engine",(disc_side-.008,.36,z),(disc_side+.008,.36,z),.067,16)
 for i in range(5):
  angle=2*pi*i/5
  y=.36+.049*cos(angle);wz=z+.049*sin(angle)
  tube("Rim",(disc_side-.010,y,wz),(disc_side+.010,y,wz),.009,5)
 ellipsoid("Engine",(disc_side,.40,z-.14 if z > 0 else z+.109),
           (.032,.049,.027),10,5)

# Trellis chassis, telescoping front fork and air-cooled twin.
for sign in (-1,1):
 x=sign*.16
 tube("Rim",(x,.46,-.73),(x,.76,-.15),.033,8)
 tube("Engine",(x,.97,-.16),(x,.67,.29),.033,8)
 tube("Engine",(x,.64,.27),(x,.47,-.42),.033,8)
 tube("Engine",(x,.36,.78),(x,.69,.73),.043,12)
 tube("Rim",(x,.62,.74),(x,1.20,.61),.029,12)
 tube("Engine",(x,.99,.655),(x,1.30,.58),.045,12)
 tube("Rim",(x,.53,-.17),(x,.75,-.09),.10,14)
 tube("Engine",(x,.53,-.20),(x,.53,-.05),.105,14)
 for n in range(6):
  box("Engine",(x,.64+n*.031,-.002+n*.01),(.205,.012,.247))
 tube("Rim",(sign*.235,.62,-.70),(sign*.217,.90,-.55),.032,8)
 for n in range(5):
  t=n/5
  tube("Engine",(sign*(.235-.018*t),.64+.229*t,-.69+.13*t),
                (sign*(.234-.018*t),.657+.229*t,-.68+.13*t),.039,7)
 box("Seat",(sign*.27,1.032,-.03),(.026,.102,.22))
 for n in range(4):
  tube("Engine",(sign*.288,1.01+n*.020,-.11),(sign*.288,1.023+n*.020,.048),.003,5)

ellipsoid("Engine",(0,.59,-.12),(.22,.15,.25),16,6)
ellipsoid("Paint",(0,1.075,.02),(.279,.192,.43),24,11)
ellipsoid("Paint",(0,.985,.35),(.225,.12,.208),16,7)
ellipsoid("Seat",(0,1.123,-.48),(.222,.077,.33),20,8)
ellipsoid("Seat",(0,1.155,-.73),(.173,.063,.193),16,7)
ellipsoid("Paint",(0,1.069,-.77),(.174,.089,.29),16,7)
for i in range(5):
 z=-.39-i*.057
 tube("Engine",(-.096,1.192,z),(.096,1.192,z),.0035,5)
box("Engine",(0,.71,-.54),(.24,.095,.44))

# Rider controls, mirror stalks, fork clamps and optical housings.
tube("Engine",(-.187,1.13,.61),(.187,1.13,.61),.034,8)
tube("Rim",(-.39,1.35,.525),(.39,1.35,.525),.021,12)
for sign in (-1,1):
 tube("Seat",(sign*.34,1.35,.525),(sign*.445,1.35,.515),.031,8)
 tube("Rim",(sign*.33,1.34,.58),(sign*.435,1.33,.603),.007,6)
 tube("Rim",(sign*.31,1.35,.55),(sign*.45,1.51,.51),.010,7)
 ellipsoid("Engine",(sign*.45,1.51,.50),(.078,.044,.025),12,6)
 ellipsoid("Rim",(sign*.45,1.51,.477),(.066,.032,.006),12,6)
 ellipsoid("Amber",(sign*.28,1.151,.676),(.057,.023,.031),10,5)
 ellipsoid("Amber",(sign*.206,1.081,-1.008),(.049,.024,.025),10,5)
 tube("Engine",(sign*.19,.68,-.35),(sign*.326,.68,-.35),.014,8)
tube("Engine",(0,1.17,.58),(0,1.17,.74),.138,24)
ellipsoid("Rim",(0,1.17,.736),(.122,.122,.047),24,8)
ellipsoid("Headlamp",(0,1.17,.766),(.037,.037,.022),12,6)
ellipsoid("ClearCover",(0,1.17,.754),(.125,.125,.048),24,9)
tube("Engine",(0,1.24,.47),(0,1.33,.43),.100,18)
ellipsoid("Seat",(0,1.328,.423),(.084,.013,.074),16,6)
tube("Rim",(-.052,1.342,.438),(.035,1.342,.408),.0038,5)
ellipsoid("TailLamp",(0,1.083,-1.017),(.127,.037,.031),18,7)
ellipsoid("RedCover",(0,1.083,-1.037),(.131,.041,.021),20,8)

# Stainless header and tapered can with a recessed outlet.
tube("Rim",(.15,.68,.12),(.13,.48,.31),.030,10)
tube("Rim",(.13,.48,.31),(.28,.39,-.25),.030,10)
tube("Rim",(.28,.39,-.25),(.268,.50,-.65),.040,10)
tube("Rim",(.268,.50,-.65),(.267,.59,-.99),.065,14)
tube("Engine",(.267,.59,-.994),(.267,.591,-1.004),.040,12)
# Side stand and welded foot sit on the same plane as the tyres when parked.
tube("Engine",(.14,.57,-.24),(.39,.054,-.30),.014,8)
box("Engine",(.39,.053,-.30),(.054,.009,.071))

OUT.mkdir(parents=True,exist_ok=True)
obj=OUT/(NAME+'.obj')
lines=['# Aaris original motorcycle geometry - no third-party assets',
       'mtllib '+NAME+'.mtl','o '+NAME]
lines.extend('v %.6f %.6f %.6f'%v for v in verts)
for material,triangles in faces.items():
 lines.extend(('usemtl '+material,'s 1'))
 lines.extend('f %d %d %d'%triangle for triangle in triangles)
obj.write_text('\n'.join(lines)+'\n')
mtl=OUT/(NAME+'.mtl')
with mtl.open('w') as f:
 for name,rgb in PALETTE.items():
  f.write('newmtl '+name+'\n')
  f.write('Kd %.3f %.3f %.3f\nKa 0 0 0\nKs 0.2 0.2 0.2\nNs 60\n\n'%rgb)
mtl.write_text(mtl.read_text().rstrip()+'\n')
for path in (obj,mtl):
 meta=path.with_name(path.name+'.meta')
 guid=uuid.uuid5(uuid.NAMESPACE_URL,'aaris-original-roadside-bike-'+path.suffix).hex
 if path.suffix=='.obj':
  original=(OUT/'QuaterniusSports.obj.meta').read_text()
  import re
  original=re.sub(r'(?m)^guid: [0-9a-f]{32}$','guid: '+guid,original)
  meta.write_text(original)
 else:meta.write_text('fileFormatVersion: 2\nguid: '+guid+'\n')
print('ORIGINAL_BIKE_MODEL',len(verts),'vertices',
      sum(map(len,faces.values())),'triangles',len(PALETTE),'materials',
      obj.stat().st_size,'bytes')
