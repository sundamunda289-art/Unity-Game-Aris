#!/usr/bin/env python3
"""Regression for anatomically feasible crouch-walk IK using native sample bones.

Synthetic angle feasibility is not equivalent to Unity Play Mode/phone FPS.
"""
from pathlib import Path
from math import sqrt,cos,sin,acos,pi,degrees
src=(Path(__file__).resolve().parents[2]/"Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text()
support=src.split("float kneeClearance = Mathf.Lerp(0.21f, 0.045f, proneShare);",1)[1].split("Vector3 leftFootGoal",1)[0]
solver=src.split("// The same native limb-circle sample requires",1)[1].split("}",1)[0]
for s in ("crouching && !prone", "0.20f, 0.88f, planar", "0.78f, 0.98f, effectiveCrouchBlend", "kneeClearance -= 0.06f * movingCrouchKneeRelax", "(1f - proneShare)"):
    assert s in support,s
for s in ("proneShare < 0.05f ? 110f : 75f","movingCrouchPoleCap * movingLowStance", "kneeTurnStability"):
    assert s in solver,s
assert "Physics.Raycast" not in support+solver
assert "visualRoot.localPosition += Vector3.up * movingCrouchHipLift;" not in src
# Bone locations are recorded by Unity in a real CrouchWalking back-view frame.
lh=(-763.905,-2.446,80.265)
h=(-764.034,-2.402,80.034)
k=(-764.385,-2.128,79.751)
f=(-764.099,-2.529,79.959)
def sub(x,y):return tuple(a-b for a,b in zip(x,y))
def dot(x,y):return sum(a*b for a,b in zip(x,y))
def mag(x):return sqrt(dot(x,x))
def unit(x):return tuple(v/mag(x) for v in x)
def cross(a,b):return (a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0])
side=unit((h[0]-lh[0],0,h[2]-lh[2]))
forward=cross(side,(0.,1.,0.))
axis=unit(sub(f,h))
upper,lower=mag(sub(k,h)),mag(sub(f,k))
d=mag(sub(f,h))
along=(upper*upper-lower*lower+d*d)/(2*d)
across=sqrt(max(0.,upper*upper-along*along))
p=unit(cross(axis,side))
q=cross(axis,p)
old=sub(sub(k,h),tuple(axis[i]*dot(sub(k,h),axis) for i in range(3)))
old=unit(old)
def valid_angles(clearance):
    out=[]
    for j in range(8192):
        rad=2*pi*j/8192
        bend=tuple(p[i]*cos(rad)+q[i]*sin(rad) for i in range(3))
        newk=tuple(h[i]+axis[i]*along+across*bend[i] for i in range(3))
        if (newk[1] >= f[1]+clearance and
            abs(dot(sub(newk,h),side)) <= .23 and
            dot(sub(newk,h),forward)>0):
            out.append(degrees(acos(max(-1.,min(1.,dot(old,bend))))))
    return out
assert not valid_angles(.21),"21cm knee floor should be infeasible on recorded frame"
angles=valid_angles(.15)
assert angles,"15cm clearance should admit a floor-safe forward bend"
minimum=min(angles)
assert 75 < minimum < 110,minimum
def smooth(x):
    x=max(0.,min(1.,x))
    return x*x*(3-2*x)
def clearance(speed,blend,prone):
    move=smooth((speed-.20)/(.88-.20))*smooth((blend-.78)/(.98-.78))
    return .21-.06*move*(1.-prone)
assert abs(clearance(2.4,1.,0.)-.15)<1e-9
assert clearance(0.,1.,0.)==.21
assert clearance(2.4,0.,0.)==.21
assert clearance(2.4,1.,1.)==.21
print("PASS: 21cm infeasible; 15cm+110deg can reach floor-safe anatomical knee; other stances gated")
