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
# Prone head/feet must not tunnel into an uphill TerrainCollider. The
# underlying terrain still supports the body if BOTH end samples have a
# walkable normal and leave at least 10 cm below the chest center.
assert 'if (obstacle is TerrainCollider terrain)' in action
assert 'new Ray(sample + Vector3.up * 1.2f, Vector3.down)' in action
assert 'sampleHit.point.y > center.y - 0.10f' in action
assert 'sampleHit.normal.y < minWalkableNormal' in action
assert 'if (obstacle is TerrainCollider) continue;' not in action
import math
for rise, angle, allowed in (
    (0.00, 0, True), (0.20, 20, True),
    (0.22, 20, False), (0.00, 55, False)
):
    # center is 0.31 m above feet; terrain must stay <= 0.21 m.
    got = rise <= (0.31 - 0.10) and math.cos(math.radians(angle)) >= math.cos(math.radians(45))
    assert got == allowed, (rise, angle, got)
# Stance visual catch-up is quicker than the upward recovery, with a
# frame-rate independent exponential; the physics capsule is untouched.
assert 'crouching ? 0.11f : 0.15f' in visual
assert 'prone ? 0.11f : 0.15f' in visual
assert 'if (!visuallyAirborne)' in visual
assert 'if (!visuallyAirborne && horizontal < 0.045f)' in visual
assert 'if (controller.isGrounded && horizontal < 0.045f)' not in visual
assert 'if (pose < 0.01f && runBlend < 0.01f)' in visual
assert 'if (crouchWeight > 0.99f && crouchMoving < 0.01f)' in visual
assert 'if (proneWeight > 0.99f && crawlMoving < 0.01f)' in visual
assert 'locomotionPhase = 0f;' in visual
assert 'crouchPhase = 0f;' in visual
assert 'crawlPhase = 0f;' in visual
for fps in (10, 30, 60, 120):
    dt = 1.0 / fps
    pose_value = 0.0
    for _ in range(round(0.3 * fps)):
        pose_value += (1.0 - pose_value) * (1.0 - math.exp(-dt / 0.11))
    expected = 1.0 - math.exp(-0.3 / 0.11)
    assert abs(pose_value - expected) < 1e-12, (fps, pose_value)
    assert pose_value > 1.0 - math.exp(-0.3 / 0.16)
# Idle-only rephase cannot touch an actively visible or airborne gait.
for airborne, horizontal, pose, blend in (
    (False, 0.0, 0.0, 0.005), (False, 0.5, 0.0, 0.0),
    (True, 0.0, 0.0, 0.0), (False, 0.0, 0.4, 0.0)
):
    reset = not airborne and horizontal < 0.045 and pose < 0.01 and blend < 0.01
    assert reset == (not airborne and horizontal == 0.0 and pose == 0.0 and blend == 0.005)

# A one-frame ground-contact seam must NOT interrupt distance-driven gait.
# Real takeoff and sustained falls still stop the planted-foot cycle.
for fps in (10, 30, 60, 120):
    dt = 1.0 / fps
    def stride_phase(one_frame_seam):
        phase = 0.0
        contact_age = 0.0
        for frame in range(12):
            grounded = not one_frame_seam or frame != 6
            contact_age = 0.0 if grounded else contact_age + dt
            visibly_airborne = not grounded and contact_age > 0.13
            if not visibly_airborne:
                phase += 3.4 * dt / 2.55
        return phase
    assert math.isclose(stride_phase(True), stride_phase(False),
                        abs_tol=1e-12), fps
    # A deliberate jump overrides the support grace even in the first frame.
    grounded, deliberate_jump, age_since_contact = False, True, dt
    visibly_airborne = not grounded and (deliberate_jump or age_since_contact > 0.13)
    assert visibly_airborne, fps

# Character's original embedded .fbm image is a tangent-space normal atlas,
# not a skin albedo. Import its small derived normal atlas as a REAL Unity
# normal texture, bind it to URP Lit and keep mip/streaming affordable on 2GB.
normal_png = ROOT / 'Assets/Resources/AarisCharacters/Primary/Player_Normal_512.png'
normal_meta = (ROOT / 'Assets/Resources/AarisCharacters/Primary/Player_Normal_512.png.meta').read_text()
normal_bytes = normal_png.read_bytes()
assert normal_bytes[:8] == b'\x89PNG\r\n\x1a\n'
assert int.from_bytes(normal_bytes[16:20], 'big') == 512
assert int.from_bytes(normal_bytes[20:24], 'big') == 512
assert 'guid: 66e5bd5db61b49c9ac85aadd3d345ae1' in normal_meta
assert 'textureType: 1' in normal_meta and 'sRGBTexture: 0' in normal_meta
assert 'enableMipMap: 1' in normal_meta and 'streamingMipmaps: 1' in normal_meta
primary_mat = (ROOT / 'Assets/Resources/AarisCharacters/Primary/Materials/Player-MI_Superhero_Male.mat').read_text()
assert 'm_ValidKeywords:\n  - _NORMALMAP' in primary_mat
assert '_BumpMap:\n        m_Texture: {fileID: 2800000, guid: 66e5bd5db61b49c9ac85aadd3d345ae1, type: 3}' in primary_mat

# Actual ground contact records downward impact before resetting gravity.
# Visual-only landing compression increases smoothly and stays tightly bounded.
assert 'public float LastLandingImpactSpeed => lastLandingImpactSpeed;' in motor
assert 'preSupportFallSpeed = Mathf.Max(0f, -verticalVelocity);' in motor
assert 'lastLandingImpactSpeed = Mathf.Max(preSupportFallSpeed,' in motor
assert 'Mathf.Clamp01((landingImpact - 1f) / 9f)' in visual
assert 'Mathf.Lerp(0.55f, 1.15f, impactBlend)' in visual
strengths = [0.55 + 0.60 * max(0.0, min(1.0, (speed - 1.0) / 9.0))
             for speed in (0.0, 3.0, 6.0, 10.0, 40.0)]
assert strengths == sorted(strengths)
assert abs(strengths[0] - 0.55) < 1e-7
assert abs(strengths[-1] - 1.15) < 1e-7
assert max(strengths) * 0.055 < 0.07
print('PASS: grounded prone, jump impact, backward gait, airborne inertia, poses and curved Gloo Wall')
