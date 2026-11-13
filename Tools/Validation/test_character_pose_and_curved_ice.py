#!/usr/bin/env python3
"""Fast source/asset regression; never substitutes for visual Unity Play Mode."""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
visual = (ROOT / 'Assets/AarisWorld/Scripts/AarisCharacterVisual.cs').read_text()
motor = (ROOT / 'Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs').read_text()
gloo = (ROOT / 'Assets/AarisWorld/Scripts/AarisOfflineGlooWall.cs').read_text()
assert 'crouchPlayable.SetTime(crouchClip.length * (10d / 24d));' in visual
assert 'pronePlayable.SetTime(proneClip != null' in visual
assert '? 0d : crouchClip.length * (10d / 24d)' in visual
assert 'jumpPlayable.SetTime(jump.length * 0.39d)' in visual
assert 'fallPlayable.SetTime(fall.length * 0.72d)' in visual
assert 'jumpStartPlayable.SetSpeed(Mathf.Clamp' in visual
assert 'float phaseDirection = forwardSpeed < -0.20f' in visual
assert 'phaseDirection * distance / standStride' in visual
assert 'phaseDirection * distance / 1.40f' in visual
assert 'phaseDirection * distance / 0.90f' in visual
assert 'smoothedStrafeBank = Mathf.Lerp(smoothedStrafeBank, bankTarget, alpha)' in visual
assert 'float airResponseScale = desiredVelocity.sqrMagnitude < 0.001f' in motor
assert 'Mathf.Min(airborneSteering, 0.18f)' in motor
assert 'Mathf.Min(airborneSteering, 0.45f)' in motor
# Rate-independent release brake: at 4.8m/s, 0.3 seconds in the air
# should retain at least ~3.7 m/s instead of decelerating to zero.
for fps in (10, 30, 60, 120):
    v = 4.8
    for _ in range(round(0.3 * fps)):
        v = max(0.0, v - 19.0 * 0.18 / fps)
    assert 3.70 < v < 3.90, (fps, v)
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
# A vertical CharacterController cannot block the horizontal body during prone turns.
action = (ROOT / 'Assets/AarisWorld/Scripts/AarisBattleActions.cs').read_text()
assert 'public bool CanFaceProne(Vector3 forward)' in action
assert 'if (target == PlayerStance.Prone && !capsule.isGrounded) return false;' in action
assert motor.count('battle.CanFaceProne(nextFacing * Vector3.forward)') == 2
print('PASS: grounded prone gating, strafe bank, backward gait, airborne inertia, poses and curved Gloo Wall')
