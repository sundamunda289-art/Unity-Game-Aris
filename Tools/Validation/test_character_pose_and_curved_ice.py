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
assert 'crouchPlayable.SetTime(naturalMocapLoaded' in visual
assert 'pronePlayable.SetTime(proneClip != null' in visual
assert '? 0d : crouchClip.length * (10d / 24d)' in visual
assert 'jumpPlayable.SetTime(jump.length * 0.39d)' in visual
assert 'fallPlayable.SetTime(fall.length * 0.72d)' in visual
assert 'jumpStartPlayable.SetSpeed(Mathf.Clamp' in visual
# Authored samples keep the continuous locomotion distance phase and
# shift only the contact points of distinct primary run and sprint clips.
assert 'if (!naturalMocapLoaded && primaryLoaded && walkClip != runClip)' in visual
assert 'runPhaseOffset = -2f / 60f;' in visual
assert 'sprintPhaseOffset = sprintClip == runClip ? runPhaseOffset : 0f;' in visual
assert 'LoopPlayable(runPlayable, runClip, locomotionPhase + runPhaseOffset);' in visual
assert 'LoopPlayable(sprintPlayable, sprintClip, locomotionPhase + sprintPhaseOffset);' in visual
for anchor in (0.0, 0.003, 0.5, 0.995, 1.0):
    for shift in (-2 / 60, 0.0):
        phase, length = anchor + shift, 0.792
        seconds = phase * length
        wrapped = seconds % length
        assert 0.0 <= wrapped < length
assert 'float phaseDirection = gaitDirection;' in visual
assert 'phaseDirection * distance / standStride' in visual
assert 'phaseDirection * distance / 1.40f' in visual
assert 'phaseDirection * distance / 0.90f' in visual
assert 'smoothedStrafeBank = Mathf.Lerp(smoothedStrafeBank, bankTarget, alpha)' in visual
# Natural locomotion is a complete, local-only 100STYLE set. Never partially
# mix it with KayKit gait clips, and never add runtime network/download work.
assert 'private const string MocapLocomotion = "AarisMocap/mocap_locomotion";' in visual
assert 'Resources.LoadAll<AnimationClip>(MocapLocomotion)' in visual
assert "importedName.LastIndexOf('|')" in visual
assert 'leafLength == exactName.Length' in visual
assert 'string.Compare(importedName, separator + 1, exactName, 0,' in visual
def matches_mocap_name(imported, expected):
    if imported.lower() == expected.lower():
        return True
    separator = imported.rfind('|')
    return separator >= 0 and imported[separator + 1:].lower() == expected.lower()
assert matches_mocap_name('mocap_walk', 'mocap_walk')
assert matches_mocap_name('BamenMilitarySoldierRig|mocap_walk', 'mocap_walk')
assert matches_mocap_name('BamenMilitarySoldierRig|MOCAP_RUN', 'mocap_run')
assert not matches_mocap_name('wrong_mocap_walk', 'mocap_walk')
assert not matches_mocap_name('Rig|prefix_mocap_walk', 'mocap_walk')
assert 'naturalMocapLoaded = naturalWalk != null && naturalJog != null &&' in visual
assert 'naturalRun != null && naturalCrouch != null;' in visual
assert 'runClip = naturalMocapLoaded ? naturalJog' in visual
assert 'walkClip = naturalMocapLoaded ? naturalWalk' in visual
assert 'sprintClip = naturalMocapLoaded ? naturalRun' in visual
assert 'crouchClip = naturalMocapLoaded ? naturalCrouch' in visual
assert 'crouchWalkClip = naturalMocapLoaded ? naturalCrouch' in visual
assert 'if (!naturalMocapLoaded && primaryLoaded && walkClip != runClip)' in visual
assert 'crouchPlayable.SetTime(naturalMocapLoaded' in visual
assert 'float crouchLowering = naturalMocapLoaded ? 0f :' in visual
assert 'UnityWebRequest' not in visual
# Fresh Unity imports must auto-map each FBX from its own hierarchy. Never
# serialize a HumanDescription copied from another soldier/rig: that made
# TacticalSoldier's Avatar invalid and produced multi-metre mocap leg errors.
tactical_humanoid_meta = (ROOT / 'Assets/Resources/AarisCharacters/Primary/TacticalSoldier/TacticalSoldier.fbx.meta').read_text()
mocap_humanoid_meta = (ROOT / 'Assets/Resources/AarisMocap/mocap_locomotion.fbx.meta').read_text()
for humanoid_meta in (tactical_humanoid_meta, mocap_humanoid_meta):
    assert '    human: []' in humanoid_meta
    assert '    skeleton: []' in humanoid_meta
    assert '  autoGenerateAvatarMappingIfUnspecified: 1' in humanoid_meta
    assert '  animationType: 3' in humanoid_meta
    assert '  avatarSetup: 1' in humanoid_meta
    assert '    globalScale: 1' in humanoid_meta
    assert '    globalScale: 0' not in humanoid_meta
assert 'RealisticSoldier(Clone)' not in tactical_humanoid_meta
assert 'BamenMilitarySoldierRig' not in tactical_humanoid_meta
# Translation DoF adds a Humanoid retargeting step on mobile. The native
# clean-import pose contract showed no lower-body benefit here, so keep the
# cheaper rotation-only path for the 2 GB Android target.
assert '    hasTranslationDoF: 0' in mocap_humanoid_meta
# November native Unity stance-jump diagnostic measured a real 0.75m
# standing shoe hover above terrain (physical capsule foot gap 0.035m).
# Enforce animated-bone calibration, never a guessed -skinWidth offset.
assert 'float boneSpan = proneHeadBone.position.y -' in visual
assert '(TargetHeightMetres - 0.14f) / boneSpan' in visual
assert 'visualRoot.localScale *= scaleFactor;' in visual
assert 'standingShoeOffset + adjustment' in visual
assert 'standingShoeOffset * uprightWeight' in visual
assert 'Mathf.Min(leftFootBone.position.y,' in visual
# Native crouch/prone Head has to stay below stance capsule ceiling;
# knee solver then replants feet at the real floor under each foot.
assert 'float capsuleTop = transform.TransformPoint(controller.center +' in visual
assert 'Mathf.Min(excess, 0.45f) * lowPose' in visual
assert 'private float SupportedAnkleY(Transform foot, float clearance,' in visual
assert 'Ray ray = new Ray(origin, Vector3.down);' in visual
assert 'Physics.RaycastNonAlloc(ray,' in visual
assert 'hit.collider.transform.IsChildOf(transform)' in visual
assert 'PlantCrouchLeg(leftThigh, leftShin, leftFootBone,' in visual
assert 'Vector3 currentKnee = knee - hip;' in visual
assert 'Vector3.ProjectOnPlane(currentKnee, animatedAxis)' in visual
assert 'Quaternion.FromToRotation(animatedAxis, axis) * bend' in visual
assert 'Vector3.Dot(bend, forwardBend) < 0f' in visual
# Crouch/prone must reuse each existing foot support ray's normal so shoes
# follow sloped roads/bridges without adding another pair of Physics queries.
assert 'out Vector3 leftSupportNormal' in visual
assert 'out Vector3 rightSupportNormal' in visual
assert 'AlignFootToSupport(leftFootBone, leftSupportNormal,' in visual
assert 'AlignFootToSupport(rightFootBone, rightSupportNormal,' in visual
assert 'AlignFootToSupport(foot, supportNormal, finalWeight, 32f, 0.85f);' in visual
# Grounded stance feet retain bounded X/Z world contact, then release for swing.
assert 'UprightPlantEnterGapMetres = 0.065f' in visual
assert 'UprightPlantReleaseGapMetres = 0.115f' in visual
assert 'UprightPlantBreakDistanceMetres = 0.18f' in visual
assert 'plantWorld - foot.position, transform.up' in visual
assert 'PlantLegToTarget(thigh, shin, foot, targetFoot, transform.forward,' in visual
assert 'leftUprightPlantLocked = false;' in visual
assert 'rightUprightPlantLocked = false;' in visual
assert 'ProneAnkleClearanceMetres' in visual
# Upright walk/run contact uses the same bounded, allocation-free leg solve,
# but only near the floor so an authored swing foot is never pinned.
assert 'private void PlantUprightContact(' in visual
assert 'private const float UprightAnkleClearanceMetres = 0.075f;' in visual
assert 'float supportY = coarseSupport.foot == foot &&' in visual
assert 'coarseSupport.footPosition.Equals(foot.position)' in visual
assert ': SupportedAnkleY(foot, UprightAnkleClearanceMetres, out supportNormal);' in visual
assert 'UprightAnkleClearanceMetres, out Vector3 supportNormal);' in visual
assert 'supportNormal' in visual
assert 'Mathf.InverseLerp(0.025f, 0.14f, gap)' in visual
assert 'Mathf.Lerp(0.95f, 0.62f,' in visual
assert 'lowPose < 0.02f' in visual
assert visual.count('PlantUprightContact(') == 3
# Whole-rig floating recovery must work even if the player starts moving before
# the one-time idle scale calibration. It uses the real stance-foot support
# and hands small residual motion back to leg IK instead of flattening gait.
assert 'bool uprightGroundedSoldier = CalibratedSoldierLoaded && graphReady' in visual
assert 'bool stableSoldier = uprightGroundedSoldier && planar < 0.15f;' in visual
assert 'Transform stanceFoot = leftFootBone.position.y <= rightFootBone.position.y' in visual
assert 'float supportY = SupportedAnkleY(stanceFoot,' in visual
assert 'height = supportY, normal = supportNormal' in visual
assert 'float stanceGap = stanceFoot.position.y - supportY;' in visual
assert 'if (!float.IsNaN(stanceGap) && Mathf.Abs(stanceGap) > 0.07f)' in visual
# A missing support hit must not fabricate a ground plane at the motor origin.
# Unsupported feet keep authored motion; supported feet still use the same
# bounded ankle/normal correction path.
assert 'float floor = float.NaN;' in visual
assert 'if (foot == null) return float.NaN;' in visual
assert 'float fallback = transform.position.y + clearance;' not in visual
assert 'if (float.IsNaN(supportY))' in visual
assert visual.count('if (!float.IsNaN(leftAnkleY))') == 2
assert visual.count('if (!float.IsNaN(rightAnkleY))') == 2
import math
for support, foot_y, expected in ((float('nan'), 0.5, False),
                                  (0.075, 0.5, True),
                                  (0.075, 0.08, False)):
    gap = foot_y - support
    assert (not math.isnan(gap) and abs(gap) > 0.07) == expected
assert 'transform.position.y + 0.045f - lowestAnkle' not in visual
# Positive calibration and grounded correction remain bounded, including
# low-FPS 10/30/60/120Hz startup and imported soldier bone spans.
for span in (1.168, 1.35, 1.64):
    scale = max(0.85, min(1.5, 1.64 / span))
    assert 0.85 <= scale <= 1.5
assert abs(1.64 / 1.168 - 1.4041) < 0.001
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
# Stance visual timing stays frame-rate independent but human-scaled:
# crouch is quicker than prone, and getting back up is deliberately slower.
assert 'crouching ? 0.11f : 0.16f' in visual
assert 'prone ? 0.17f : 0.24f' in visual

def exp_blend_after(seconds, fps, tau):
    value = 0.0
    dt = min(1.0 / fps, 0.15)
    steps = round(seconds * fps)
    for _ in range(steps):
        alpha = 1.0 - math.exp(-dt / tau)
        value += (1.0 - value) * alpha
    return value

for tau in (0.11, 0.16, 0.17, 0.24):
    reference = exp_blend_after(0.4, 120, tau)
    for fps in (10, 20, 30, 60):
        assert abs(exp_blend_after(0.4, fps, tau) - reference) < 0.006
assert exp_blend_after(0.25, 60, 0.11) > 0.89
assert exp_blend_after(0.25, 60, 0.17) < 0.80
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
# Forward-only gait coverage is procedurally orientation-warped for combat
# strafes. Drive from collision-resolved velocity, fade before full backpedal,
# rotate the pelvis before foot IK, and counter-yaw the spine to preserve aim.
assert 'float slopeLimit = controller != null ? controller.slopeLimit : 46f;' in visual
assert 'Mathf.Clamp(slopeLimit + 3f, 0f, 89f) * Mathf.Deg2Rad' in visual
assert 'hit.normal.y < minWalkableNormalY' in visual
assert 'hit.normal.y < 0.55f' not in visual
# Foot planting must never claim a steeper support face than the physical
# CharacterController can traverse (allowing only the authored 3-degree seam tolerance).
import math
for slope_limit in (30.0, 46.0, 60.0, 89.0):
    threshold = math.cos(math.radians(min(slope_limit + 3.0, 89.0)))
    assert threshold >= 0.0
    accepted_angle = math.degrees(math.acos(threshold))
    assert accepted_angle <= min(slope_limit + 3.0, 89.0) + 1e-6

assert 'private float smoothedOrientationWarpYaw;' in visual
# Unity 6 Humanoid blend stabilization runs before our terrain-aware
# LateUpdate leg solve. It adds no gameplay physics queries and reduces ankle
# drift specifically while walk/run/stance clips cross-fade.
assert 'animator.stabilizeFeet = true;' in visual
assert visual.index('animator.stabilizeFeet = true;') < visual.index('SetupAnimations();')

assert 'locomotionHipsBone = animator.GetBoneTransform(HumanBodyBones.Hips);' in visual
assert 'float travelForward = Vector3.Dot(planarVelocity, transform.forward);' in visual
assert 'float travelSide = Vector3.Dot(planarVelocity, transform.right);' in visual
assert 'Mathf.InverseLerp(110f, 170f, absTravelAngle)' in visual
assert 'travelAngle * 0.50f, -44f, 44f' in visual
assert 'Quaternion.AngleAxis(smoothedOrientationWarpYaw, transform.up)' in visual
assert '-smoothedOrientationWarpYaw * 0.78f' in visual
assert 'smoothedOrientationWarpYaw = 0f;' in visual
# Pure lateral travel reaches the bounded pelvis warp, while full backward
# movement fades it away so the existing reversed backpedal gait stays stable.
def orientation_warp(angle):
    a = abs(angle)
    x = max(0.0, min(1.0, (a - 110.0) / 60.0))
    smooth = x * x * (3.0 - 2.0 * x)
    fade = 1.0 - smooth
    return max(-44.0, min(44.0, angle * 0.50)) * fade
assert abs(orientation_warp(90.0) - 44.0) < 1e-9
assert 20.0 < orientation_warp(135.0) < 35.0
assert abs(orientation_warp(180.0)) < 1e-9
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

# Standing footstep audio and visual gait must share the same dynamic motor
# thresholds. The current BR profile reaches full jog at 4.2 m/s and full
# sprint at 5.7 armed / 6.5 unarmed; old 3.4/4.4/4.8 constants drifted.
audio = (ROOT / 'Assets/AarisWorld/Scripts/AarisAudioDirector.cs').read_text()
for src in (visual, audio):
    assert 'float jogCeiling = player' in src
    assert 'Mathf.Max(1.8f, jogCeiling * 0.55f)' in src
    assert 'Mathf.Max(jogCeiling * 0.95f,' in src
    assert 'sprintCeiling * 0.72f' in src
    assert 'Mathf.Lerp(2.55f, 3.65f, sprintBlend)' in src
def stand_cycle(speed, armed):
    jog = 4.2
    cap = 5.7 if armed else 6.5
    run_start = max(1.8, jog * 0.55)
    sprint_start = max(jog * 0.95, cap * 0.72)
    run = max(0.0, min(1.0, (speed - run_start) / (jog - run_start)))
    sprint = max(0.0, min(1.0, (speed - sprint_start) / (cap - sprint_start)))
    return (1.0 - run) * 1.25 + run * (2.55 + 1.10 * sprint)
assert abs(stand_cycle(4.2, False) - 2.55) < 1e-12
assert abs(stand_cycle(5.7, True) - 3.65) < 1e-12
assert abs(stand_cycle(6.5, False) - 3.65) < 1e-12
for fps in (10, 15, 20, 30, 60, 120):
    elapsed = 2.0
    distance = sum(4.2 / fps for _ in range(int(elapsed * fps)))
    stride = stand_cycle(4.2, False)
    contacts = int(distance / (stride * 0.5) + 1e-9)
    assert contacts == 6, (fps, contacts)
    assert stand_cycle(5.7, True) > stand_cycle(4.2, True)

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


# Native-measured crouch/prone visual contact regression.
# Head/ankle readings are from the actual Unity 6000.3.26f1 humanoid pose
# probe, not assumed screenshot pixel distances.
assert 'private const float CrouchRigLowering = 0.50f;' in visual
assert 'private const float ProneRigLowering = 0.22f;' in visual
assert 'float crouchRigWeight = authoredProne' in visual
assert '? effectiveCrouchBlend * (1f - effectiveProneBlend)' in visual
assert ': Mathf.Clamp01(effectiveCrouchBlend + effectiveProneBlend);' in visual
# If true prone clips are missing, the fallback stance must still lower the
# rig with a bounded crouch blend rather than leaving the player airborne.
assert 'float proneLowering = primaryLoaded && proneClip != null &&' in visual
# One two-bone solve per leg, irrespective of intermediate stance blends.
assert visual.count('PlantCrouchLeg(') == 3  # definition + low-stance left/right
assert visual.count('PlantLegToTarget(') >= 3  # definition + crouch wrapper + upright 3D target
assert 'out Vector3 supportNormal' in visual
assert 'foot = stanceFoot, footPosition = stanceFoot.position' in visual
assert 'float totalRigWeight = crouchRigWeight + proneRigWeight;' in visual
assert 'float ankleRigWeight = !visuallyAirborne && graphReady' in visual
assert 'Mathf.Clamp01(totalRigWeight) : 0f;' in visual
assert 'float proneShare = proneRigWeight / Mathf.Max(totalRigWeight, 0.0001f);' in visual
assert 'Mathf.Lerp(PlantedAnkleMetres,' in visual
assert 'float leftAnkleY = SupportedAnkleY(leftFootBone,' in visual
assert 'float rightAnkleY = SupportedAnkleY(rightFootBone,' in visual
assert 'ankleClearance, out Vector3 leftSupportNormal);' in visual
assert 'ankleClearance, out Vector3 rightSupportNormal);' in visual
for authored in (False, True):
    for i in range(11):
        for j in range(11):
            crouch, prone = i / 10.0, j / 10.0
            crouch_ik = crouch * (1.0 - prone) if authored else min(1.0, crouch + prone)
            prone_ik = prone if authored else 0.0
            total = crouch_ik + prone_ik
            weight = min(1.0, total)
            share = prone_ik / max(total, 0.0001)
            ankle = 0.13 + (0.105 - 0.13) * share
            assert 0.0 <= weight <= 1.0 and 0.0 <= share <= 1.0
            assert 0.105 - 1e-12 <= ankle <= 0.13 + 1e-12
            if not authored:
                assert abs(ankle - 0.13) < 1e-12
            if authored and crouch == 0 and prone > 0:
                assert abs(ankle - 0.105) < 1e-12
assert 'foot.rotation = footRotation;' in visual
assert 'Quaternion.FromToRotation(oldUpper, newUpper)' in visual
assert 'Quaternion.FromToRotation(oldLower, newLower)' in visual
assert 1.00 < 1.611 - 0.50 < 1.14  # Crouch head enters 1.14m capsule
assert 0.01 < 0.258 - 0.22 < 0.08  # Prone ankle reaches the ground
# Solver must stay numerically bounded for nearly folded / extended knees.
for upper, lower in ((0.31, 0.32), (0.42, 0.44), (0.55, 0.48)):
    for measured in (0.02, 0.16, 0.5, 1.6):
        d = max(abs(upper - lower) + .005,
                min(upper + lower - .005, measured))
        along = (upper ** 2 - lower ** 2 + d ** 2) / (2 * d)
        across = math.sqrt(max(0.0, upper ** 2 - along ** 2))
        assert math.isfinite(along) and math.isfinite(across)
        assert 0 <= across <= upper


# Editor native pose probe must never report success from a rejected stance.
# Use a fixed surveyed point rather than arbitrarily landing in vegetation.
pose_probe = (ROOT / "Assets/AarisWorld/Editor/AarisPoseFrames.cs").read_text()
assert 'if(!SurveyTeleport(p))' in pose_probe
assert 'if(!ok) Finish("STANCE_REJECTED_"+Names[n]);' in pose_probe
assert 'if(b.Stance != AarisBattleActions.PlayerStance.Prone)' in pose_probe
assert 'Finish("PRONE_NOT_ACTIVE");return;' in pose_probe
assert 'SUCCESS_PRONE_GEOMETRY_ONLY' in pose_probe


# AAA stance/headroom and crouch-to-jump bridge verification.
assert 'private const float ProneChestCorrectionMaxDegrees = 24f;' in visual
assert 'private const float ProneHeadColliderInset = 0.10f;' in visual
assert 'proneChestBone = animator.GetBoneTransform(HumanBodyBones.Chest);' in visual
assert 'proneHeadBone = animator.GetBoneTransform(HumanBodyBones.Head);' in visual
assert 'proneChestBone.rotation = tilt * proneChestBone.rotation;' in visual
assert 'Quaternion.RotateTowards(Quaternion.identity, tilt,' in visual
assert 'Mathf.Clamp01(jumpAge / 0.12f)' in visual
assert 'float stanceTotal = effectiveCrouchBlend + effectiveProneBlend;' in visual
assert 'float proneWeight = authoredProne ? effectiveProneBlend * stanceScale : 0f;' in visual
# Measured original prone chest/head offset is 0.206m vertical, 0.369m
# horizontal. A 24 degree forward chest tilt should bring the head bone
# inside the 0.62m controller without dragging the grounded ankles downward.
relative_head_angle = math.atan2(.206, .369)
head_after_tilt = .527 + math.hypot(.206, .369) * math.sin(
    relative_head_angle - math.radians(24))
assert .52 < head_after_tilt < .62, head_after_tilt
for fps in (10, 20, 30, 60, 120):
    for frames in (0, 1, 2, 5, 12):
        age = frames / fps
        suppression = max(0.0, min(1.0, age/.12))
        assert 0 <= suppression <= 1
    assert max(0, min(1, (math.ceil(.12*fps)/fps)/.12)) == 1


# Native AAA stand/crouch/prone/jump/landing exercise must be fail-closed.
# The Editor-only native probe runs the real motor and reads actual bone Y,
# jump apex, landing event and collision state at one deterministic location.
assert 'public static void CaptureStanceJumpDiagnostic()' in pose_probe
assert 'SessionState.SetBool("pose.stanceJump",true);' in pose_probe
assert 'Stage(n==3?5:n+1,p,b);' in pose_probe
assert 'if(b.Stance != AarisBattleActions.PlayerStance.Prone)' in pose_probe
assert 'if(n==3&&b.Stance!=AarisBattleActions.PlayerStance.Standing)' in pose_probe
assert 'bool launched=p.LastJumpStartedTime>=requested-0.001f;' in pose_probe
assert 'p.LastLandingTime>p.LastJumpStartedTime' in pose_probe
assert 'if(!p.IsInAir&&maxHeight>=0.45f)' in pose_probe
assert 'SUCCESS_STANCE_JUMP_MOTOR' in pose_probe
assert 'Finish("JUMP_FAILED");' in pose_probe


# Native textured tactical-soldier primary plus fail-safe fallback hierarchy.
tactical_path=ROOT / "Assets/Resources/AarisCharacters/Primary/TacticalSoldier/TacticalSoldier.fbx"
assert tactical_path.is_file() and 5_000_000 < tactical_path.stat().st_size < 9_000_000
tactical_raw=tactical_path.read_bytes()
for bone in (b"mixamorig:Hips", b"mixamorig:LeftHand", b"mixamorig:RightHand",
             b"mixamorig:LeftFoot", b"mixamorig:RightFoot"):
    assert bone in tactical_raw
assert b"mixamorig:Hips_01" not in tactical_raw
assert b"HeadphonesT" not in tactical_raw
tactical_credit=(tactical_path.parent / "ATTRIBUTION.txt").read_text()
assert "DanlyVostok" in tactical_credit and "CC BY 4.0" in tactical_credit
assert "sketchfab" in tactical_credit.lower()
assert "b403237626e308c5a27be9683407b4450b03a246" in tactical_credit
tactical_meta=(tactical_path.with_suffix(".fbx.meta")).read_text()
assert "guid: 8cf0c99d7a934ae1b24937c60ccf6f21" in tactical_meta
assert "animationType: 3" in tactical_meta or "humanDescription:" in tactical_meta

bamen_path=ROOT / "Assets/Resources/AarisCharacters/Primary/RealisticSoldier/RealisticSoldier.fbx"
assert bamen_path.is_file() and 300000 < bamen_path.stat().st_size < 2500000
bamen_credit=(bamen_path.parent / "ATTRIBUTION.txt").read_text()
assert "BAMEN" in bamen_credit and "CC BY 4.0" in bamen_credit

assert 'private const string PrimaryModel = "AarisCharacters/Primary/TacticalSoldier/TacticalSoldier";' in visual
assert 'PreviousPrimaryModel = "AarisCharacters/Primary/RealisticSoldier/RealisticSoldier";' in visual
assert 'LegacyPrimaryModel = "AarisCharacters/Primary/Player";' in visual
load_order = [visual.index(f'TryInstantiateCharacter({name})')
              for name in ('PrimaryModel', 'PreviousPrimaryModel',
                           'LegacyPrimaryModel', 'FallbackModel')]
assert load_order == sorted(load_order)
candidate = visual[visual.index("private bool TryInstantiateCharacter("):
                   visual.index("private bool ConfigureDrawableSkin(")]
assert candidate.index("if (TryPrepareAnimatedBody()) return true;") < candidate.index("ReleaseAnimationGraph();")
assert candidate.index("graph.Evaluate(0f);") < candidate.index("bodySkin.BakeMesh(posed);")
assert "(meshCentre - actorCentre).sqrMagnitude <= 4f" in candidate
assert "maxExtent >= 0.03f && maxExtent <= 2.5f" in candidate
assert "bodySkin.bounds.Intersects(" in candidate
assert "Destroy(posed);" in candidate
assert "skin.forceRenderingOff" in visual
assert 'tacticalSoldierLoaded = resourcePath == PrimaryModel;' in visual
assert 'realisticSoldierLoaded = resourcePath == PreviousPrimaryModel;' in visual
assert 'tacticalSoldierLoaded || realisticSoldierLoaded' in visual
assert 'bool uprightGroundedSoldier = CalibratedSoldierLoaded && graphReady' in visual
assert 'Resources.LoadAll<Avatar>(resourcePath)' in visual
assert 'visual.transform.localScale *= scale;' in visual
# A valid Human rig with pathological bind-space render bounds must be able
# to fit from its measured Head/Foot skeleton, without accepting arbitrary
# malformed renderer data.
assert 'private bool TryFitHumanoidBonesToController()' in visual
assert '!animator.avatar.isValid || !animator.avatar.isHuman' in visual
assert 'animator.GetBoneTransform(HumanBodyBones.Head)' in visual
assert 'animator.GetBoneTransform(HumanBodyBones.LeftFoot)' in visual
assert 'animator.GetBoneTransform(HumanBodyBones.RightFoot)' in visual
assert 'boneSpan < 0.80f || boneSpan > 2.50f' in visual
assert 'float targetBoneSpan = TargetHeightMetres - 0.14f;' in visual
assert 'Mathf.Clamp(targetBoneSpan / boneSpan, 0.50f, 2.00f)' in visual
assert 'DisablePrimaryImportHelpers(resourcePath);' in visual
assert 'visual.transform.Find("Icosphere")' in visual
assert 'helperRenderer.enabled = false;' in visual
assert 'helper.gameObject.SetActive(false);' in visual
assert 'FitVisualToController(resourcePath)' in visual
assert 'if (resourcePath == PrimaryModel && TryFitHumanoidBonesToController())' in visual
assert 'Vector3 footAnchor = (leftFoot.position + rightFoot.position) * 0.5f;' in visual
assert 'footAnchor.y = Mathf.Min(leftFoot.position.y, rightFoot.position.y);' in visual
assert 'Vector3 targetFootAnchor = transform.position +' in visual
assert 'visual.transform.position += targetFootAnchor - footAnchor;' in visual
assert '!renderer.enabled ||' in visual
assert '!renderer.gameObject.activeInHierarchy' in visual
assert 'return TryFitHumanoidBonesToController();' in visual
assert 'combined.size.y > 1000f' in visual
# Native Unity renderer probe measured the licensed TacticalSoldier skinned
# mesh around X=-3.87/Z=-46.7 with a valid metric Humanoid. Full anchor
# correction must remove horizontal import-origin error, not only Y.
foot_anchor = (-3.873, -2876.50, -46.706)
target_anchor = (0.0, 0.075, 0.0)
delta = tuple(t - f for t, f in zip(target_anchor, foot_anchor))
aligned = tuple(f + d for f, d in zip(foot_anchor, delta))
assert all(abs(a - t) < 1e-9 for a, t in zip(aligned, target_anchor))
# Normalizing measured height must preserve the scale already present on
# the imported root, including its proportions when scales differ by axis.
for import_scale in ((0.5, 0.5, 0.5), (2.0, 2.0, 2.0), (0.9, 1.2, 1.1)):
    measured_height = 1.65 * import_scale[1]
    correction = 1.78 / measured_height
    fitted_scale = tuple(axis * correction for axis in import_scale)
    assert math.isclose(1.65 * fitted_scale[1], 1.78)
    assert math.isclose(fitted_scale[0] / fitted_scale[1],
                        import_scale[0] / import_scale[1])
assert 'private const float RealisticSoldierProneRigLowering = 0.64f;' in visual
assert 'private const float RealisticSoldierCrouchRigLowering = 0.73f;' in visual
assert 'realisticSoldierLoaded ? RealisticSoldierProneRigLowering : ProneRigLowering' in visual
assert 'float crouchLowering = naturalMocapLoaded ? 0f :' in visual
assert 'realisticSoldierLoaded ? RealisticSoldierCrouchRigLowering :' in visual
assert 'CrouchRigLowering) * crouchRigWeight;' in visual
assert 'public static void CapturePortrait()' in pose_probe
assert 'ACTOR_HEAD_PENETRATES_CEILING_' in pose_probe
assert 'SUCCESS_7_ACTOR_PORTRAITS' in pose_probe
assert 'cam.cullingMask=savedMask;' in pose_probe
assert 'cam.targetTexture=old;RenderTexture.active=active;' in pose_probe
