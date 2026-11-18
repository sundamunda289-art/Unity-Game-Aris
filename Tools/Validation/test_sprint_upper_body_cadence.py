#!/usr/bin/env python3
"""Regression for natural shoulder animation on a full-speed sprint.

Only the passive, upper-body idle/ready overlay is released. Gait, aiming,
attacks and the physical movement capsule keep their existing authority.
"""
from pathlib import Path

v=(Path(__file__).resolve().parents[2] /
   "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text()
start=v.index("float sprintArmRelease = !aim")
end=v.index("float layerWeight = Mathf.Clamp01",start)
part=v[start:end]
assert "Mathf.InverseLerp(0.20f, 0.85f, sprintBlend)" in part
assert "carryTarget *= 1f - sprintArmRelease;" in part
assert "carryTarget < readyPoseBlend ? 1.65f : 1f" in part
assert "readyPoseBlend = Mathf.Lerp(readyPoseBlend," in part
assert "!aim" in part
assert "Physics." not in part and "new " not in part
assert "float attackUpper = Mathf.Max(punchUpper, shootUpper);" in v
assert "float layerWeight = Mathf.Clamp01(Mathf.Max(readyPoseBlend, attackUpper));" in v
assert "bool sustainedSprint = sprintBlend >= 0.35f &&" in v

def smoothstep(x):
    x=max(0.0,min(1.0,x))
    return x*x*(3.0-2.0*x)

def release(sprint,aim):
    return 0.0 if aim else smoothstep((sprint-.20)/(.85-.20))

def step(sprint,aim,ready,carry,alpha):
    target=carry*(1.0-release(sprint,aim))
    blend_alpha=min(1.0,alpha*(1.65 if target<ready else 1.0))
    return ready+(target-ready)*blend_alpha,target

assert release(0,False)==0
assert release(.2,False)==0
assert release(.85,False)==1
assert release(1,False)==1
assert release(1,True)==0
assert all(release(a,False)<=release(b,False)
           for a,b in ((.0,.2),(.2,.4),(.4,.6),(.6,.8),(.8,1.)))
for sprint in (0.0,.25,.45,.65,.85,1):
    for aim in (True,False):
        ready,target=step(sprint,aim,.45,.36,.25)
        assert 0<=ready<=1 and 0<=target<=.36
        if aim:
            assert abs(target-.36)<1e-9
        if sprint>=.85 and not aim:
            assert target==0 and ready<.45
# Returning from sprint should smoothly blend toward passive ready pose;
# never snap to its target in one ordinary 60 FPS frame.
ready,target=step(0.0,False,0.0,.36,.25)
assert abs(ready-.09)<1e-9 and target==.36
# Dropping the pose uses stronger damping so the arms unlock sooner.
ready,target=step(1.0,False,.36,.36,.25)
assert abs(ready-(.36*(1-.25*1.65)))<1e-9 and target==0

print("PASS: sprint frees authored arm cadence, aim/attacks and jog remain intact")
print("NOT TESTED: Unity render, model grip alignment or Android FPS")
