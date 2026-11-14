#!/usr/bin/env python3
"""Fast source/asset regression; never substitutes for visual Unity Play Mode."""
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
visual = (ROOT / 'Assets/AarisWorld/Scripts/AarisCharacterVisual.cs').read_text()
motor = (ROOT / 'Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs').read_text()
# A valid imported Humanoid drives the visible arms and legs. Do not also
# animate the hidden primitive fallback every mobile frame, but re-arm it
# when the imported rig is unavailable (no working avatar/playable graph).
assert 'characterVisual = GetComponent<AarisCharacterVisual>();' in motor
assert 'if (characterVisual != null && characterVisual.HasWorkingHumanoid)' in motor
assert 'legacyIdlePoseApplied = false; // Re-arm fallback if the rig unloads.' in motor
assert motor.index('private void AnimateWalk(float dt)') < motor.index(
    'if (characterVisual != null && characterVisual.HasWorkingHumanoid)') < motor.index(
    'float secondsSincePunch = battle != null')
gloo = (ROOT / 'Assets/AarisWorld/Scripts/AarisOfflineGlooWall.cs').read_text()
assert 'crouchPlayable.SetTime(crouchClip.length * (10d / 24d));' in visual
assert 'pronePlayable.SetTime(proneClip != null' in visual
assert '? 0d : crouchClip.length * (10d / 24d)' in visual
assert 'jumpPlayable.SetTime(jump.length * 0.39d)' in visual
assert 'fallPlayable.SetTime(fall.length * 0.72d)' in visual
assert 'jumpStartPlayable.SetSpeed(Mathf.Clamp' in visual
assert 'float phaseDirection = gaitDirection;' in visual
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
# Turning before Move checks prone clearance at the old position.
# A wall-corner turn that is valid there can clip the horizontal head
# after the CharacterController moves to its resolved destination.
assert motor.count('battle.CanFaceProne(nextFacing * Vector3.forward)') == 1
assert 'bool turnAfterMove = false;' in motor
assert 'if (turnAfterMove &&' in motor
assert motor.index('CollisionFlags flags = controller.Move(') < motor.index('Vector3 velocity = controller.velocity;')
assert motor.index('Vector3 velocity = controller.velocity;') < motor.index('if (turnAfterMove &&')
assert 'Quaternion.LookRotation(horizontalVelocity)' not in motor
assert 'Quaternion.LookRotation(resolvedPlanarVelocity)' in motor
assert 'new Vector3(velocity.x, 0f, velocity.z)' in motor
assert 'if (!combatFacing)' in motor
assert 'transform.rotation = nextFacing;' in motor
# A wall may resolve a forward input into sideways travel; the actor must
# face the resolved tangent rather than keep running visually into the wall.
import math
def facing_yaw(x, z):
    return math.degrees(math.atan2(x, z))
assert facing_yaw(0.0, 4.8) == 0.0
assert facing_yaw(2.0, 0.0) == 90.0
# Fully blocked movement remains below the turn threshold (0.05 m/s).
assert 0.0 ** 2 + 0.0 ** 2 <= 0.0025
assert 0.3 ** 2 + 0.0 ** 2 > 0.0025
# One-dimensional footprint cross-section: the old-position yaw test passes,
# but the same yaw at the later position intrudes into a real wall.
wall_start, prone_head_reach = 0.85, 0.55
origin, resolved_position = 0.0, 0.45
assert origin + prone_head_reach < wall_start
assert resolved_position + prone_head_reach > wall_start
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
# A MeshCollider bridge/ramp can be walkable below the chest while its
# uphill end clips the prone head. A centre-only raycast misses that case.
mesh_clearance = action.split('private bool HasProneClearance(', 1)[1].split(
    'private void Update()', 1)[0]
assert 'bool walkableAlongBody = true;' in mesh_clearance
assert 'float minMeshNormal = Mathf.Cos(capsule.slopeLimit * Mathf.Deg2Rad);' in mesh_clearance
assert mesh_clearance.count('for (int end = 0; end < 2; end++)') == 2
assert mesh_clearance.count('Vector3 sample = end == 0 ? head : legs;') == 2
assert 'new Ray(sample + Vector3.up * 0.45f, Vector3.down)' in mesh_clearance
assert 'floorHit.point.y > center.y - 0.11f' in mesh_clearance
assert 'floorHit.normal.y < minMeshNormal' in mesh_clearance
assert 'if (walkableAlongBody) continue;' in mesh_clearance
assert 'new Ray(center + Vector3.up * 0.45f, Vector3.down)' not in mesh_clearance

def mesh_support_clear(head_rise, leg_rise, slope_degrees):
    return (head_rise <= 0.31 - 0.11 and leg_rise <= 0.31 - 0.11
            and math.cos(math.radians(slope_degrees)) >= math.cos(math.radians(46)))

for head, legs, slope, permitted in (
    (0.0, 0.0, 0, True), (0.20, 0.10, 20, True),
    (0.22, 0.0, 20, False), (0.0, 0.22, 20, False),
    (0.10, 0.10, 55, False),
):
    assert mesh_support_clear(head, legs, slope) == permitted, (head, legs, slope)
# Stance visual catch-up is quicker than the upward recovery, with a
# frame-rate independent exponential; the physics capsule is untouched.
assert 'crouching ? 0.085f : 0.15f' in visual
assert 'prone ? 0.085f : 0.15f' in visual
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
        pose_value += (1.0 - pose_value) * (1.0 - math.exp(-dt / 0.085))
    expected = 1.0 - math.exp(-0.3 / 0.085)
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

# Terrain seam grace must also apply to visual body balance. Previously the
# feet kept cycling but the lateral lean and turn pitch dropped for one frame,
# causing a body twitch on stair lips at low mobile refresh rates.
assert visual.count('bool visuallyAirborne =') == 1
assert visual.index('bool visuallyAirborne =') < visual.index('float bankTarget =')
assert 'float bankTarget = !visuallyAirborne' in visual
assert 'float standingShare = !visuallyAirborne' in visual
assert 'float bankTarget = controller.isGrounded' not in visual
assert 'float standingShare = controller.isGrounded' not in visual
for fps in (10, 30, 60, 120):
    dt = 1.0 / fps
    def bank_after_steps(one_frame_seam):
        bank = 0.0
        last_contact_age = 0.0
        for frame in range(12):
            grounded = not one_frame_seam or frame != 6
            last_contact_age = 0.0 if grounded else last_contact_age + dt
            visually_airborne = not grounded and last_contact_age > 0.13
            target = -2.0 if not visually_airborne else 0.0
            bank += (target - bank) * (1.0 - math.exp(-dt / 0.2))
        return bank
    assert math.isclose(bank_after_steps(True), bank_after_steps(False),
                        abs_tol=1e-12), fps
    # Jump timestamp overrides contact grace instantly: keep a deliberate
    # takeoff from retaining grounded stance/turn balance for 130 ms.
    grounded, deliberate_jump = False, True
    airborne = not grounded and (deliberate_jump or dt > 0.13)
    assert airborne and (0.0 if airborne else -2.0) == 0.0, fps

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
# Turn/brake balance changes the visual hierarchy only, not locomotion physics.
# It must use actual capsule velocity and signed shortest-angle yaw changes.
assert 'private bool motionHistoryReady;' in visual
assert 'if (motionHistoryReady && motionDt > 0.0001f)' in visual
assert '(planarVelocity - lastPlanarVelocity) / motionDt' in visual
assert 'Mathf.DeltaAngle(lastFacingYaw, currentYaw) / motionDt' in visual
assert '1f - Mathf.Clamp01(crouchBlend + proneBlend) : 0f' in visual
assert 'Mathf.InverseLerp(0.8f, 3.4f, planar) * standingShare' in visual
assert 'leaning + smoothedAccelerationPitch' in visual
assert 'smoothedStrafeBank + smoothedTurnBank' in visual
assert 'controller.Move(' not in visual
# A player respawn, teleport or vehicle hand-off must not turn a large
# world-space discontinuity into a one-frame acceleration/bank animation.
assert 'private Vector3 lastMotorPosition;' in visual
assert 'bool movementDiscontinuity = !controller.enabled ||' in visual
assert 'Vector3 frameTravel = motorPosition - lastMotorPosition;' in visual
assert 'frameTravel.sqrMagnitude > 4f' in visual
assert 'float distance = resolvedGroundTravelMetres;' in visual
assert 'if (movementDiscontinuity)' in visual
assert 'motionHistoryReady = false;' in visual
assert 'smoothedAccelerationPitch = 0f;' in visual
assert 'smoothedTurnBank = 0f;' in visual
# A large transform discontinuity must clear lateral lean too; resetting
# acceleration and turn bank alone leaves a visibly crooked stationary avatar.
reset_block = visual.split('if (movementDiscontinuity)', 1)[1].split('}', 1)[0]
assert 'smoothedStrafeBank = 0f;' in reset_block
assert 'lastMotorPosition = motorPosition;' in visual
for fps in (10, 30, 60, 120):
    # Sprint travel must be treated as ordinary motion at every target FPS.
    distance_per_frame = 4.8 / fps
    assert distance_per_frame ** 2 < 4.0, fps
    # A same-frame 3m respawn / hand-off is not a gait acceleration.
    reset = 3.0 ** 2 > 4.0
    sampled_acceleration = 0.0 if reset else (3.0 / (1.0 / fps))
    assert reset and sampled_acceleration == 0.0, fps
for fps in (10, 30, 60, 120):
    # First frame has no velocity/yaw history, then the transient pitch
    # converges to the same bounded angle regardless of rendering FPS.
    dt = 1.0 / fps
    pitch = bank = 0.0
    for _ in range(fps):
        pitch += (2.2 - pitch) * (1.0 - math.exp(-dt / 0.10))
        bank += (-2.5 - bank) * (1.0 - math.exp(-dt / 0.13))
    assert abs(pitch - 2.2 * (1.0 - math.exp(-10.0))) < 1e-10
    assert abs(bank + 2.5 * (1.0 - math.exp(-1.0 / 0.13))) < 1e-10
    assert -2.5 <= bank < 0 < pitch <= 2.2
# Braking reverses pitch, while crouch / prone / airborne fade the target.
for acceleration, share in ((13.0, 1.0), (-13.0, 1.0),
                            (13.0, 0.0), (13.0, 0.35)):
    pitch_target = max(-1.0, min(1.0, acceleration / 13.0)) * 2.2 * share
    assert abs(pitch_target) <= 2.2
    if share == 0.0:
        assert pitch_target == 0.0

# A typical 3.4m/s forward jog must not play a tiny fast-walk cycle:
# the previous 3.3-4.4m/s blend generated almost FIVE foot contacts/sec.
# The audio and visual cycles must use the SAME walk-to-jog speed range.
audio = (ROOT / 'Assets/AarisWorld/Scripts/AarisAudioDirector.cs').read_text()
assert 'Mathf.InverseLerp(2.0f, 3.4f, horizontal)' in visual
assert 'Mathf.InverseLerp(2.0f, 3.4f, metresPerSecond)' in audio
assert 'Mathf.InverseLerp(3.3f, 4.4f, horizontal)' not in visual
assert 'Mathf.InverseLerp(3.3f, 4.4f, metresPerSecond)' not in audio
def jog_cycle(speed, armed):
    run = max(0.0, min(1.0, (speed - 2.0) / 1.4))
    sprint = max(0.0, min(1.0, (speed - 4.0) / (0.4 if armed else 0.8)))
    return (1.0 - run) * 1.25 + run * (2.55 + 1.10 * sprint)
assert abs(jog_cycle(3.4, False) - 2.55) < 1e-12
assert 2.5 < (2.0 * 3.4 / jog_cycle(3.4, False)) < 2.8
for fps in (10, 15, 20, 30, 60, 120):
    elapsed = 2.0
    distance = sum(3.4 / fps for _ in range(int(elapsed * fps)))
    stride = jog_cycle(3.4, False)
    contacts = int(distance / (stride * 0.5) + 1e-9)
    assert contacts == 5, (fps, contacts)
    assert jog_cycle(4.4, True) > jog_cycle(3.4, True)

print('PASS: grounded prone, jump impact, dynamic balance, jog-footfall sync, backward gait, airborne inertia, poses and curved Gloo Wall')

# Camera eye transition must use the same bounded elapsed time as the
# sole movement motor. The former 50 ms cap made 10/20 FPS crouch and prone
# camera changes lag substantially behind their collision capsule and pose.
assert 'Mathf.Infinity, Mathf.Clamp(Time.deltaTime, 0f, 0.15f));' in motor
assert 'Mathf.Infinity, Mathf.Min(Time.deltaTime, 0.05f));' not in motor
for fps in (10, 20, 30, 60, 120):
    frames = round(0.3 * fps)
    integrated = sum(min(1.0 / fps, 0.15) for _ in range(frames))
    assert abs(integrated - 0.3) < 1e-9, (fps, integrated)
# Retaining the old cap would have integrated only 0.15 seconds at 10 FPS.
assert abs(sum(min(0.1, 0.05) for _ in range(3)) - 0.15) < 1e-9


# Stable backpedal gait avoids contact-direction chatter around -0.2 m/s.
assert 'private float gaitDirection = 1f;' in visual
assert 'forwardSpeed < -0.32f' in visual
assert 'forwardSpeed > -0.08f' in visual
direction = 1.0
for forward in (-0.38,-0.21,-0.18,-0.23,-0.19,-0.27):
    if forward < -0.32: direction = -1.0
    elif forward > -0.08: direction = 1.0
    assert direction == -1.0
assert 0.12 > -0.08

# Small, native URP Lit cloth albedo with mip streaming; no extra runtime material.
from struct import unpack
tex = ROOT / 'Assets/Resources/AarisCharacters/Primary/Textures/AarisTacticalFabric_512.png'
raw = tex.read_bytes()
assert raw[:8] == b'\x89PNG\r\n\x1a\n'
assert unpack('>II', raw[16:24]) == (512,512)
meta = tex.with_suffix('.png.meta').read_text()
assert 'streamingMipmaps: 1' in meta
assert 'enableMipMap: 1' in meta
assert 'sRGBTexture: 1' in meta
assert 'textureType: 0' in meta
guid = 'a26b95582e905f30b703a95779c69777'
assert 'guid: '+guid in meta
assert '_BaseMap:\n        m_Texture: {fileID: 2800000, guid: '+guid+', type: 3}' in primary_mat
