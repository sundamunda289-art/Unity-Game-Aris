#!/usr/bin/env python3
"""Source + frame-rate regression checks for low-overhead on-foot and audio loops.

No native Unity simulation is claimed here; Play Mode and real device timing
still require the Unity Editor and an Android profiler.
"""
from pathlib import Path
import math

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets" / "AarisWorld" / "Scripts"


def audit():
    motor = (SRC / "AarisThirdPersonPlayer.cs").read_text(encoding="utf-8")
    audio = (SRC / "AarisAudioDirector.cs").read_text(encoding="utf-8")

    # Support is queried before CharacterController.Move; a failed first
    # SphereCast must not be repeated with identical pre-Move geometry.
    assert "bool downhillProbeChecked = false;" in motor
    assert "downhillProbeChecked = true;\n                wasGrounded = HasWalkableGroundWithinStep();" in motor
    assert "if (!downhillProbeChecked && !wasGrounded && !launchJump &&" in motor
    assert motor.index("downhillProbeChecked = true") < motor.index("controller.Move(")
    assert motor.index("if (!downhillProbeChecked") < motor.index("controller.Move(")
    assert motor.count("controller.Move(") == 1
    assert "HasWalkableGroundWithinStep()" in motor
    assert "Mathf.Cos(controller.slopeLimit * Mathf.Deg2Rad)" in motor

    # High-rate frames settle to exact target FOV instead of writing a tiny
    # new fraction to the native camera property every stable frame.
    assert "float fovError = targetFov - currentFov;" in motor
    assert "if (Mathf.Abs(fovError) > 0.001f)" in motor
    assert "Mathf.Abs(fovError) <= 0.1f" in motor
    for fps in (10, 15, 20, 30, 60, 90, 120):
        value, target = 60.0, 52.0
        writes = 0
        for _ in range(5 * fps):
            delta = target - value
            if abs(delta) > 0.001:
                value = target if abs(delta) <= 0.1 else (
                    value + delta * (1 - math.exp(-5.0 / fps))
                )
                writes += 1
        assert value == target, (fps, value)
        assert writes < fps * 2, (fps, writes)

    # A missing optional Drop/Vehicle/Battle component may be added at runtime,
    # but GetComponent retries must be bounded rather than once per frame.
    assert "private float nextOptionalComponentRefresh;" in audio
    assert "Time.unscaledTime >= nextOptionalComponentRefresh" in audio
    assert "nextOptionalComponentRefresh = Time.unscaledTime + 1f;" in audio
    assert audio.count("GetComponent<AarisOfflineDrop>()") == 2
    assert audio.count("GetComponent<AarisVehicleSession>()") == 2
    assert audio.count("GetComponent<AarisBattleActions>()") == 2

    # Keep water, road and concrete footsteps without a temporary lowercase
    # string allocation per footstep.
    assert "hit.collider.name.ToLowerInvariant()" not in audio
    for surface in ("water", "river", "stream", "road", "asphalt", "concrete", "floor", "bridge"):
        assert 'label.IndexOf("' + surface + '", StringComparison.OrdinalIgnoreCase)' in audio

    # The collision-clamped camera target is always tested once, including
    # while stationary; the second cast is only necessary while smoothing.
    # The epsilon is 0.1 mm, far smaller than the camera collision radius.
    camera = motor[motor.index("private void PlaceCamera(bool instant)"):]
    first_cast = camera.index("wanted = ObstructionSafe(pivot, wanted);")
    interpolation = camera.index("Vector3 next = instant ? wanted")
    second_guard = camera.index("if ((next - wanted).sqrMagnitude > 0.00000001f)")
    second_cast = camera.index("next = ObstructionSafe(pivot, next);")
    assert first_cast < interpolation < second_guard < second_cast
    assert camera.count("ObstructionSafe(pivot, next)") == 1
    assert math.sqrt(0.00000001) <= 0.0001
    # Stable camera and instant reset skip the duplicate cast; a moving
    # camera still checks the interpolated position for obstructions.
    assert not (0.0 > 0.00000001)
    assert (0.02 * 0.02) > 0.00000001

    # Simplified/stripped player prefabs may omit battle or inventory;
    # checking missing components on every 60/120 Hz frame burns CPU.
    # Prefetch at Awake and retry no faster than once per real-time second.
    awake = motor[motor.index("private void Awake()"):motor.index("private void OnDestroy()")]
    update = motor[motor.index("private void Update()"):motor.index("private void AnimateWalk")]
    for component in ("AarisBattleActions", "AarisWeaponInventory"):
        assert "GetComponent<" + component + ">()" in awake
        assert "GetComponent<" + component + ">()" in update
    assert "private float nextOptionalComponentRefresh;" in motor
    assert "Time.unscaledTime >= nextOptionalComponentRefresh" in update
    assert "nextOptionalComponentRefresh = Time.unscaledTime + 1f;" in update
    assert update.index("nextOptionalComponentRefresh = Time.unscaledTime + 1f;") < update.index("ReadInput();")

    # Animation-only optional scripts must not trigger GetComponent per
    # LateUpdate on stripped player prefabs. Start caches present components;
    # absent/late-added ones are retried at most once per unscaled second.
    visual = (SRC / "AarisCharacterVisual.cs").read_text(encoding="utf-8")
    startup = visual[visual.index("private void Start()"):visual.index("private bool FitVisualToController()")]
    late = visual[visual.index("private void LateUpdate()"):visual.index("private void OnDestroy()")]
    assert "aerialDrop = GetComponent<AarisOfflineDrop>();" in startup
    assert "private float nextOptionalComponentRefresh;" in visual
    assert "Time.unscaledTime >= nextOptionalComponentRefresh" in late
    assert "nextOptionalComponentRefresh = Time.unscaledTime + 1f;" in late
    assert late.count("GetComponent<AarisBattleActions>()") == 1
    assert late.count("GetComponent<AarisOfflineDrop>()") == 1
    assert late.count("GetComponent<AarisWeaponInventory>()") == 1
    assert late.index("nextOptionalComponentRefresh = Time.unscaledTime + 1f;") < late.index("float dt =")
    assert late.index("GetComponent<AarisOfflineDrop>()") < late.index("if (aerialDrop != null && !aerialDrop.HasLanded)")

    for fps in (10, 30, 60, 90, 120):
        next_retry, lookups = 0.0, 0
        for frame in range(fps * 5):
            now = frame / fps
            if now >= next_retry:
                next_retry = now + 1.0
                lookups += 1
        assert 4 <= lookups <= 6, (fps, lookups)

    # The legacy fallback has four animated limb Transform setters. Once it
    # reaches a grounded idle pose, skip duplicate writes at any frame rate.
    # Movement, jump, punch and aerial/vehicle transitions must re-arm updates.
    idle = motor[motor.index("private void AnimateWalk(float dt)"):
                 motor.index("private bool HasWalkableGroundWithinStep()")]
    assert "bool punchAnimating = secondsSincePunch >= 0f && secondsSincePunch <= 0.36f;" in idle
    assert "travelSpeed < 0.01f && controller.isGrounded && !punchAnimating" in idle
    assert "if (settled && legacyIdlePoseApplied) return;" in idle
    assert "legacyIdlePoseApplied = settled;" in idle
    assert idle.index("if (settled && legacyIdlePoseApplied) return;") < idle.index("gait += travelSpeed")
    for transition in ("internal void SetLegacyAerialPose(", "internal void RestoreLegacyAerialPose(",
                       "public void ResumeFromAerialDrop()", "public void ResetCameraAfterVehicle()",
                       "private void ResetPlayer()"):
        start = motor.index(transition)
        end = motor.find("\n        }", start)
        assert "legacyIdlePoseApplied = false;" in motor[start:end], transition

    for fps in (10, 30, 60, 120):
        already_idle, writes = False, 0
        for _ in range(fps * 5):
            settled = True
            if settled and already_idle:
                continue
            already_idle = settled
            writes += 1
        assert writes == 1, (fps, writes)
        # Punch immediately leaves the idle guard; landing/movement settle once.
        for settled in (False, False, True, True):
            if settled and already_idle:
                continue
            already_idle = settled
            writes += 1
        assert writes == 4, (fps, writes)

    # Android GPU fill-rate pressure should be reduced only after sustained
    # p95 misses. These are source/threshold contracts, NOT a GPU profiler.
    quality = (SRC / "AarisMobileRuntime.cs").read_text(encoding="utf-8")
    assert "ConfigureAndroidQuality();" in quality
    assert "#if UNITY_ANDROID && !UNITY_EDITOR" in quality
    assert "QualitySettings.streamingMipmapsMemoryBudget" in quality
    assert "memoryMB <= 2304 ? 160f : 224f" in quality
    assert "originalRenderScale = androidPipeline.renderScale;" in quality
    # The low-RAM adaptation must never be enabled on high-RAM or
    # unknown-RAM Android devices. Merely returning from ConfigureAndroidQuality
    # after storing androidPipeline would still enable TuneAndroidResolution.
    quality_setup = quality[quality.index("private void ConfigureAndroidQuality()"):
                            quality.index("private void TuneAndroidResolution(")]
    assert "if (memoryMB <= 0 || memoryMB > 3072) return;" in quality_setup
    assert quality_setup.index("if (memoryMB <= 0 || memoryMB > 3072) return;") < quality_setup.index("androidPipeline = GraphicsSettings.currentRenderPipeline")
    assert "if (androidPipeline == null || !adaptiveAndroidResolution) return;" in quality
    for ram_mb in (0, -1, 4096, 8192):
        assert not (0 < ram_mb <= 3072)
    for ram_mb in (1536, 2048, 2304, 3072):
        assert 0 < ram_mb <= 3072
    assert "lastTargetFps != targetFps" in quality
    assert "slowIntervals >= 2" in quality and "steadyIntervals >= 4" in quality
    assert "current - 0.05f" in quality and "current + 0.025f" in quality
    assert "Mathf.Clamp(minimumAndroidRenderScale, 0.65f, 1f)" in quality
    assert 'reason == "sample" && frameCount >= 30' in quality
    assert "frameIndex = frameCount = 0;" in quality
    assert "slowIntervals = steadyIntervals = 0;" in quality

    def simulate_quality(p95_p99_samples, starting_scale=.85, original_scale=1., floor=.72):
        scale = starting_scale
        slow = steady = 0
        for p95, p99 in p95_p99_samples:
            if p95 > 1000 / 60 * 1.20:
                slow += 1
                steady = 0
            elif p95 < 1000 / 60 * 1.08 and p99 < 1000 / 60 * 1.40:
                steady += 1
                slow = 0
            else:
                slow = steady = 0
            if slow >= 2:
                scale = max(floor, scale - .05)
                slow = 0
            elif steady >= 4:
                scale = min(original_scale, scale + .025)
                steady = 0
        return round(scale, 3)

    # One stutter cannot degrade quality; sustained misses lower scale.
    assert simulate_quality([(28, 34)]) == .85
    assert simulate_quality([(28, 34), (28, 34)]) == .8
    assert simulate_quality([(28, 34)] * 100) == .72
    # Recovery is intentionally slower and capped at the native authored scale.
    assert simulate_quality([(16.7, 19)] * 4) == .875
    assert simulate_quality([(16.7, 19)] * 100) == 1.0
    assert simulate_quality([(28, 34), (16.7, 19), (28, 34)]) == .85

    # Stream only originally enabled authored obstacles. Lifecycle teardown
    # must not wake deliberately disabled collision shapes across the 2 km map.
    # Periodic stationary checks stay available, but at 1 Hz instead of 4 Hz.
    nearby = (SRC / "AarisNearbyColliders.cs").read_text(encoding="utf-8")
    assert "if (c == null || !c.enabled) continue;" in nearby
    assert "else { worldBounds[i] = c.bounds; continue; }" in nearby
    assert "if (streamed[i] && obstacles[i] != null)" in nearby
    on_disable = nearby[nearby.index("private void OnDisable()"):]
    assert "if (obstacles == null || streamed == null) return;" in on_disable
    assert "if (streamed[i] && obstacles[i] != null)" in on_disable
    assert "foreach (Collider c in obstacles)" not in on_disable
    assert "if ((target.position - lastPosition).sqrMagnitude >= 4 || Time.unscaledTime >= nextCheck)" in nearby
    assert "nextCheck = Time.unscaledTime + 1f;" in nearby
    assert "if (forcePhysicsSync) Physics.SyncTransforms();" in nearby

    # Two-threshold streaming prevents collider enable/disable churn when
    # a player strafes over the 55 m boundary. The entry distance stays
    # unchanged; only an already enabled shape gets a 6 m release margin.
    assert "private const float ColliderReleaseMargin = 6f;" in nearby
    assert "float enterDistance = Mathf.Max(0f, activationDistance);" in nearby
    assert "float retainDistance = enterDistance + ColliderReleaseMargin;" in nearby
    assert "float enterLimit = enterDistance * enterDistance;" in nearby
    assert "float retainLimit = retainDistance * retainDistance;" in nearby
    assert "Cell(lastPosition.x - retainDistance)" in nearby
    assert "Cell(lastPosition.z - retainDistance)" in nearby
    assert "float limit = c.enabled ? retainLimit : enterLimit;" in nearby
    assert "if (dx * dx + dz * dz > limit) continue;" in nearby

    def collider_transitions(distances, margin):
        enabled, changes = False, 0
        for metres in distances:
            limit = 55.0 + margin if enabled else 55.0
            should_enable = metres <= limit
            if should_enable != enabled:
                changes += 1
                enabled = should_enable
        return changes, enabled

    boundary_jitter = [54.7, 55.3] * 60
    assert collider_transitions(boundary_jitter, 0.0)[0] == 120
    assert collider_transitions(boundary_jitter, 6.0) == (1, True)
    # An unloaded object at 58 m cannot activate just because exit distance
    # is 61 m. It only comes online after reaching the 55 m entry threshold.
    assert collider_transitions([58.0, 57.0, 55.1], 6.0) == (0, False)
    # Leaving beyond 61 m switches off; re-entry at 54 m activates again.
    assert collider_transitions([54.0, 60.0, 62.0, 58.0, 54.0], 6.0) == (3, True)

    def simulated_collider_polls(fps, speed, seconds=5):
        last_x, next_check, count = 0., 0., 0
        for frame in range(fps * seconds):
            now = frame / fps
            x = now * speed
            if (x - last_x) ** 2 >= 4 or now >= next_check:
                last_x, next_check = x, now + 1.
                count += 1
        return count

    for fps in (15, 30, 60, 120):
        assert 4 <= simulated_collider_polls(fps, 0) <= 6
        assert simulated_collider_polls(fps, 6.5) >= 10

    # A held Gloo Wall preview may be polled every frame by a touchscreen,
    # but optional vehicle/drop lookup must be cached and bounded. Recheck
    # immediately before deployment so newly attached driving state wins.
    gloo = (SRC / "AarisOfflineGlooWall.cs").read_text(encoding="utf-8")
    can_use = gloo[gloo.index("public bool CanUse"):
                   gloo.index("private void RefreshOptionalComponents")]
    refresh = gloo[gloo.index("private void RefreshOptionalComponents"):
                   gloo.index("private void Awake()")]
    release = gloo[gloo.index("public void ReleaseAndDeploy()"):
                   gloo.index("private void UpdatePlacement()")]
    assert "GetComponent<" not in can_use
    assert "RefreshOptionalComponents();" in can_use
    assert "if (!isActiveAndEnabled || carriedWalls <= 0) return false;" in can_use
    assert "if (!force && Time.unscaledTime < nextOptionalComponentRefresh) return;" in refresh
    assert "nextOptionalComponentRefresh = Time.unscaledTime + 1f;" in refresh
    assert refresh.count("GetComponent<AarisOfflineDrop>()") == 1
    assert refresh.count("GetComponent<AarisVehicleSession>()") == 1
    assert "RefreshOptionalComponents(true);" in release
    assert release.index("RefreshOptionalComponents(true);") < release.index("UpdatePlacement();")
    assert "private void OnDisable() => CancelAim();" in gloo

    # An already-converged audio loop must make zero native volume/pitch
    # writes. Changed targets preserve MoveTowards's bounded fades and snap.
    loop = audio[audio.index("private static void AdjustLoop("):
                 audio.index("private void LateUpdate()")]
    assert "float nextVolume = Mathf.MoveTowards(currentVolume, volume" in loop
    assert "float nextPitch = Mathf.MoveTowards(currentPitch, pitch" in loop
    assert "if (nextVolume != currentVolume) source.volume = nextVolume;" in loop
    assert "if (nextPitch != currentPitch) source.pitch = nextPitch;" in loop
    assert "if (nextVolume <= 0.001f && volume <= 0f && source.isPlaying)" in loop

    for fps in (10, 30, 60, 120):
        level, target, writes = 0.0, 0.75, 0
        for frame in range(3 * fps):
            next_level = min(target, level + 1.6 / fps)
            if next_level != level:
                level = next_level
                writes += 1
        assert level == target
        assert writes < 2 * fps
        assert not (level != target)  # Steady-state setter is skipped.

    print("PASS: airborne support reuse, single motor, steady FOV writes, "
          "bounded player/animation optional-component lookup, legacy idle pose, collision-safe camera, audio/gloo hotpaths and adaptive Android quality contracts")
    print("NOT TESTED: Unity compilation, live camera occlusion and device FPS.")


if __name__ == "__main__":
    audit()
