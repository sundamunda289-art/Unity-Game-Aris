#!/usr/bin/env python3
"""Fast source/asset regression; never substitutes for visual Unity Play Mode."""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
visual = (ROOT / 'Assets/AarisWorld/Scripts/AarisCharacterVisual.cs').read_text()
motor = (ROOT / 'Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs').read_text()
gloo = (ROOT / 'Assets/AarisWorld/Scripts/AarisOfflineGlooWall.cs').read_text()
assert 'crouchPlayable.SetTime(crouchClip.length * 0.87d)' in visual
assert 'pronePlayable.SetTime((proneClip ?? crouchClip).length * 0.87d)' in visual
assert 'jumpPlayable.SetTime(jump.length * 0.39d)' in visual
assert 'fallPlayable.SetTime(fall.length * 0.72d)' in visual
assert 'jumpStartPlayable.SetSpeed(Mathf.Clamp' in visual
assert 'if (launchJump && battle != null && !battle.TryStand())' in motor
assert 'GlooWall-ArcticBlue' in gloo and 'GlooWall-FrostHighlight' in gloo
assert 'renderer.SetPropertyBlock(block, slot)' in gloo
assert 'slots[i] = material;' in gloo and 'if (ghost)' in gloo
# Dense collider cases must inspect complete hit sets, not silently fail.
assert 'private readonly RaycastHit[] crowdedRayHits = new RaycastHit[128];' in gloo
assert gloo.count('CompleteRayHits(') == 4
assert 'selectedHits = Physics.RaycastAll(origin, direction, distance,' in gloo
assert 'if (count >= forwardHits.Length) return 0f;' not in gloo
assert 'if (count >= groundHits.Length) return false;' not in gloo
assert 'nearby = Physics.OverlapBox(center, halfSize, proposedRotation,' in gloo
assert 'if (count >= overlaps.Length) return false;' not in gloo
models = ROOT / 'Assets/Resources/AarisCombat'
geometry = (models / 'GlooWall.obj').read_text()
verts = [tuple(map(float, line.split()[1:4])) for line in geometry.splitlines() if line.startswith('v ')]
assert len(verts) == 432
assert geometry.count('usemtl ') >= 2
central = [z for x,y,z in verts if abs(x) < 0.5]
edge = [z for x,y,z in verts if abs(x) > 1.4]
assert central and edge
assert abs(sum(central)/len(central) - sum(edge)/len(edge)) > 0.06
for name in ('GlooWall-ArcticBlue','GlooWall-FrostHighlight'):
    mat = models / 'Materials' / (name + '.mat')
    assert mat.is_file() and mat.with_suffix('.mat.meta').is_file()
    txt = mat.read_text()
    assert '_BaseColor' in txt and '_Smoothness' in txt
print('PASS: crouch, prone, jump and sprint pose contracts; 432-vertex curved two-material Gloo Wall')
