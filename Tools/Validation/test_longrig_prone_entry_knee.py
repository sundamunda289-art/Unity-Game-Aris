#!/usr/bin/env python3
"""Regress long-rig prone entry pole strength; native screenshots remain essential."""
from pathlib import Path
from math import sqrt,asin,degrees
src=(Path(__file__).resolve().parents[2]/'Assets/AarisWorld/Scripts/AarisCharacterVisual.cs').read_text()
assert src.count('float enteringProneLongRig =')==1
fragment=src.split('// Large retargeted legs need more bend-circle rotation',1)[1].split('// Native 30-frame motor QA',1)[0]
for needed in ('prone && !crouching','effectiveProneBlend > 0.20f',
               'effectiveProneBlend < 0.82f','0.18f, 0.24f',
               'leftThigh.position).sqrMagnitude','transitionKneeSideTurn = 80f * transitionEnvelope','90f * enteringProneLongRig',
               'kneeStillness * kneeTurnStability'):
    assert needed in fragment,needed
assert 'Physics.' not in fragment and '.magnitude' not in fragment and 'new ' not in fragment
def smooth(z):
    z=min(1.,max(0.,z))
    return z*z*(3-2*z)
def turn(leg,prone,enter=True):
    scale=smooth((leg*leg-.18)/.06) if enter and .2<prone<.82 else 0.
    phase=smooth((prone-.18)/.42)
    return (80+90*scale)*phase
assert abs(turn(.352,.445)-80*smooth((.445-.18)/.42))<1e-5
assert turn(.5274,.445)>110.
assert abs(turn(.5274,.445,False)-turn(.352,.445))<1e-5
upper,lower,d=.5274,.5345,.4263
along=(upper**2-lower**2+d**2)/(2*d)
across=sqrt(upper**2-along**2)
required=degrees(asin((.412-.23)/across))
assert required<turn(.5274,.445)
print('PASS: scale-aware entry prone knee circle, old rig unchanged; no new query or allocation')
print('NOT TESTED: Unity knee lateral maximum and terrain-floor feasibility')
