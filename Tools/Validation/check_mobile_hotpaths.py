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
    # Do not dirty the native visual hierarchy every idle frame. The aerial
    # override is selected before the single final rotation setter (previously
    # two setters each skydiving frame). This changes no physical motor.
    assert "Quaternion desiredRotation = restingLocalRotation *" in late
    assert "desiredRotation = restingLocalRotation *" in late
    assert late.index("desiredRotation = restingLocalRotation *", late.index("if (aerialDrop != null && !aerialDrop.HasLanded)")) < late.index("if (visualRoot.localRotation != desiredRotation)")
    assert "if (visualRoot.localRotation != desiredRotation)" in late
    assert "if (visualRoot.localPosition != desiredPosition)" in late
    assert late.count("visualRoot.localRotation = desiredRotation;") == 1
    assert late.count("visualRoot.localPosition = desiredPosition;") == 1
    assert "visual.transform.localRotation =" not in late
    assert "visual.transform.localPosition =" not in late

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

    # Android image quality should fall only after sustained frame misses,
    # never just from isolated GC/streaming hitches. These source/threshold
    # contracts are not real device GPU/CPU profiling.
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
    # Unity mobile pacing must be evaluated against the effective display
    # divisor, not the nominal request (90 Hz / 60 requested -> 45 real).
    assert "Screen.currentResolution.refreshRateRatio.value" in quality
    assert "Mathf.CeilToInt(refreshHz / requestedFps)" in quality
    assert "float effectiveFps = EffectiveAndroidTargetFps(targetFps);" in quality
    assert "float budgetMs = 1000f / effectiveFps;" in quality
    assert "s.effectiveTargetFps = EffectiveAndroidTargetFps(s.targetFps);" in quality
    assert "float budgetMs = 1000f / targetFps;" not in quality

    def effective_android_fps(refresh_hz, requested_fps):
        return 30.0 if requested_fps <= 0 else (refresh_hz / math.ceil(refresh_hz / requested_fps)
            if refresh_hz > 0 else requested_fps)

    for hz, requested, effective in ((60, 60, 60), (90, 60, 45),
                                     (120, 60, 60), (144, 60, 48),
                                     (90, 30, 30), (60, 30, 30)):
        assert effective_android_fps(hz, requested) == effective
    assert effective_android_fps(59.94, 60) == 59.94
    # 45 FPS at 90 Hz is healthy, not a sustained 60 FPS render miss.
    assert 22.2 < (1000 / effective_android_fps(90, 60)) * 1.08
    assert 22.2 > (1000 / 60) * 1.20
    assert "slowIntervals >= 2" in quality and "steadyIntervals >= 4" in quality
    assert "sample.p75FrameMs > budgetMs * 1.06f" in quality
    assert "p75FrameMs = frameCount > 0 ? sortedTimes[Mathf.CeilToInt(frameCount * .75f) - 1]" in quality
    assert "current - 0.05f" in quality and "current + 0.025f" in quality
    assert "Mathf.Clamp(minimumAndroidRenderScale, 0.65f, 1f)" in quality
    assert 'reason == "sample" && frameCount >= 30' in quality
    assert "frameIndex = frameCount = 0;" in quality
    assert "slowIntervals = steadyIntervals = 0;" in quality

    def simulate_quality(p75_p95_p99_samples, starting_scale=.85, original_scale=1., floor=.72):
        scale = starting_scale
        slow = steady = 0
        for p75, p95, p99 in p75_p95_p99_samples:
            if p95 > 1000 / 60 * 1.20 and p75 > 1000 / 60 * 1.06:
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

    # A few long frames (GC, asset streaming, app notifications) must not
    # continuously blur the world while 75% of frames meet the budget.
    assert simulate_quality([(16.7, 28, 34)] * 100) == .85
    assert simulate_quality([(16.7, 28, 34), (24, 28, 34)]) == .85
    # Sustained >25% slow frames merit fill-rate relief, with hysteresis.
    assert simulate_quality([(24, 28, 34)]) == .85
    assert simulate_quality([(24, 28, 34), (24, 28, 34)]) == .8
    assert simulate_quality([(24, 28, 34)] * 100) == .72
    # Recovery is intentionally slower and capped at the native authored scale.
    assert simulate_quality([(16.7, 16.7, 19)] * 4) == .875
    assert simulate_quality([(16.7, 16.7, 19)] * 100) == 1.0
    assert simulate_quality([(24, 28, 34), (16.7, 16.7, 19), (24, 28, 34)]) == .85

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

    # Vehicle streaming must not instantiate/destroy many full 3D cars on
    # one low-RAM render frame. Keep native prefabs/physics and all plans.
    vehicle = (SRC / "AarisVehicleWorld.cs").read_text(encoding="utf-8")
    stream = vehicle[vehicle.index("private void Update()"):
                     vehicle.index("private void OnDestroy()")]
    quality = vehicle[vehicle.index("private void Awake()"):
                      vehicle.index("[RuntimeInitializeOnLoadMethod")]
    assert "private int spawnBudgetPerPoll = 2;" in vehicle
    assert "private int unloadBudgetPerPoll = 4;" in vehicle
    assert "#if UNITY_ANDROID && !UNITY_EDITOR" in quality
    assert "memoryMB > 0 && memoryMB <= 3072" in quality
    assert "spawnBudgetPerPoll = 1;" in quality
    assert "unloadBudgetPerPoll = 2;" in quality
    assert "unloaded < unloadBudgetPerPoll" in stream
    assert "built >= spawnBudgetPerPoll" in stream
    assert "record.visible.CanPark" in stream
    assert "active >= MaximumNearbyVehicles" in stream
    assert stream.count("player.position") == 1  # Cache native Transform once.
    assert stream.count("Time.unscaledTime") == 1  # Cache clock once.
    assert "record.nextProbe = now + 3.0f;" in stream
    assert "new List<" not in stream

    # Frame-paced simulation checks operation bursts at 15-120 FPS.
    # This is a bounded scheduler model, NOT a Unity physics simulation.
    for fps in (15, 30, 60, 90, 120):
        for max_spawn, max_unload in ((1, 2), (2, 4)):
            next_poll = 0.0
            spawn_remaining, unload_remaining = 7, 7
            spawned = unloaded = 0
            for frame in range(3 * fps):
                now = frame / fps
                if now < next_poll:
                    continue
                next_poll = now + 0.3
                spawn_ops = min(max_spawn, spawn_remaining)
                unload_ops = min(max_unload, unload_remaining)
                assert spawn_ops <= max_spawn and unload_ops <= max_unload
                spawn_remaining -= spawn_ops
                unload_remaining -= unload_ops
                spawned += spawn_ops
                unloaded += unload_ops
            assert spawned == 7 and unloaded == 7, (fps, max_spawn)

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
    # A duplicated Unity MonoBehaviour callback with the same signature
    # fails C# compilation (CS0111) before gameplay can even start.
    # Keep exactly one lifecycle cleanup for a canceled Gloo preview.
    assert gloo.count("private void OnDisable() => CancelAim();") == 1, (
        "Duplicate Gloo Wall OnDisable breaks Unity compilation"
    )

    # Gloo preview's blue/red color depends only on placement validity.
    # Sustained aiming should not resend the same property block on each frame.
    placement = gloo[gloo.index("private void UpdatePlacement()"):
                     gloo.index("private float FindOpenDistance(")]
    tint_guard = "if (!ghostTintInitialized || ghostTintValid != placementValid)"
    assert tint_guard in placement
    assert placement.count("AarisGlooWallVisuals.Tint(ghostRenderers") == 1
    assert placement.index(tint_guard) < placement.index(
        "AarisGlooWallVisuals.Tint(ghostRenderers")
    assert (placement.index("AarisGlooWallVisuals.Tint(ghostRenderers")
            < placement.index("ghostTintValid = placementValid;")
            < placement.index("ghostTintInitialized = true;"))
    for fps in (30, 60, 120):
        tint_initialized = False
        last_valid = False
        updates = 0
        # 4 seconds of aiming, exactly three visible color states.
        for valid in [False] * fps + [True] * (2 * fps) + [False] * fps:
            if not tint_initialized or last_valid != valid:
                updates += 1
                last_valid = valid
                tint_initialized = True
        assert updates == 3, (fps, updates)

    # In a busy match, pooled SFX must use a finished channel before
    # interrupting a still-playing gunshot, grenade or impact. The search is
    # bounded to the 12 existing voices and allocates no new AudioSources.
    one_shot = audio[audio.index("private void OneShot("):
                     audio.index("private void Schedule(")]
    assert "int selected = nextVoice;" in one_shot
    assert "if (voices[selected].isPlaying)" in one_shot
    assert "for (int offset = 1; offset < voices.Length; offset++)" in one_shot
    assert "if (voices[candidate].isPlaying) continue;" in one_shot
    assert "selected = candidate;" in one_shot
    assert "nextVoice = (selected + 1) % voices.Length;" in one_shot
    assert "if (voice.isPlaying) voice.Stop();" in one_shot
    assert "new AudioSource" not in one_shot and "new GameObject" not in one_shot

    def select_idle_voice(busy, cursor):
        selected = cursor
        if busy[selected]:
            for offset in range(1, len(busy)):
                candidate = (cursor + offset) % len(busy)
                if not busy[candidate]:
                    selected = candidate
                    break
        return selected, (selected + 1) % len(busy)

    # Exercise all 4096 channel occupancy patterns and cursor positions;
    # never cut a playing voice if a finished channel exists.
    for mask in range(1 << 12):
        busy = [(mask & (1 << i)) != 0 for i in range(12)]
        for cursor in (0, 3, 7, 11):
            chosen, next_cursor = select_idle_voice(busy, cursor)
            assert 0 <= chosen < 12 and 0 <= next_cursor < 12
            assert next_cursor == (chosen + 1) % 12
            if not all(busy):
                assert not busy[chosen], (mask, cursor, chosen)
            else:
                assert chosen == cursor  # Existing all-busy fallback.

    # Delayed SFX should do zero slot scans until the earliest cue is due.
    # Overwriting a saturated queue must also discard the old deadline.
    # This is a source + scheduling model test, not Unity audio hardware timing.
    assert "private float nextScheduledAt = float.PositiveInfinity;" in audio
    scheduler = audio[audio.index("private void Schedule("):
                      audio.index("/// <summary>Only successful weapon shots")]
    assert "bool replacedOldest = empty < 0;" in scheduler
    assert "if (replacedOldest)" in scheduler
    assert "nextScheduledAt = Mathf.Min(nextScheduledAt, at);" in scheduler
    assert "float now = Time.time;" in scheduler
    assert "if (now < nextScheduledAt) return;" in scheduler
    assert "nextScheduledAt = next;" in scheduler
    assert "scheduled[i].Key = null;" in scheduler
    assert "Array.Clear(scheduled, 0, scheduled.Length);\n            nextScheduledAt = float.PositiveInfinity;" in audio

    def simulate_cues(fps, due_times):
        slots = list(enumerate(due_times))
        deadline = min(due_times) if due_times else math.inf
        emitted, scans = [], 0
        for frame in range(2 * fps + 1):
            now = frame / fps
            if now < deadline:
                continue
            scans += 1
            next_deadline = math.inf
            remaining = []
            for key, due in slots:
                if now >= due:
                    emitted.append((key, now))
                else:
                    next_deadline = min(next_deadline, due)
                    remaining.append((key, due))
            slots, deadline = remaining, next_deadline
        return emitted, scans

    cues = [0.14, 0.36, 0.55, 1.12]
    for fps in (10, 20, 30, 60, 90, 120):
        emitted, scans = simulate_cues(fps, cues)
        assert [i for i, _ in emitted] == list(range(len(cues)))
        assert all(t <= played < t + 1 / fps + 1e-7
                   for (_, played), t in zip(emitted, cues))
        assert scans <= len(cues), (fps, scans)
        assert simulate_cues(fps, [])[1] == 0

    # Saturation keeps 16 total sounds, replacing the earliest tail.
    slots = [(i, 0.2 + i * 0.02) for i in range(16)]
    oldest = min(range(len(slots)), key=lambda i: slots[i][1])
    slots[oldest] = (99, 0.08)
    retained = sorted(slots, key=lambda item: item[1])
    assert len(retained) == 16 and retained[0] == (99, 0.08)
    assert 0 not in [item[0] for item in slots]
    assert min(due for _, due in slots) == 0.08

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

    # A player teleport can invalidate all 40 physical gun pickups at once.
    # Bound Unity Destroy() calls per 330 ms streaming poll: gameplay state,
    # reachability and planned drops must remain owned by the loot world.
    loot = (SRC / "AarisWeaponLootWorld.cs").read_text(encoding="utf-8")
    refresh = loot[loot.index("private bool RefreshPickups("):
                   loot.index("private bool StreamNearby(")]
    assert "private const int MaximumActiveLoot = 40;" in loot
    assert "private const int MaximumUnloadsPerRefresh = 6;" in loot
    assert "int unloaded = 0;" in refresh
    assert "if (unloaded >= MaximumUnloadsPerRefresh) continue;" in refresh
    assert refresh.index("if (unloaded >= MaximumUnloadsPerRefresh)") < refresh.index("Destroy(pickup.gameObject);")
    assert "unloaded++;" in refresh
    assert refresh.count("Destroy(pickup.gameObject);") == 1
    assert "if (d2 <= bestDistance && !IsBlockedDrop(pickup)" in refresh
    assert "if (record.visible != null) continue;" in loot
    for fps in (15, 20, 30, 60, 90, 120):
        remaining, next_poll, maximum_burst = 40, 0.0, 0
        for frame in range(fps * 4):
            now = frame / fps
            if now < next_poll:
                continue
            next_poll = now + 0.33
            destroyed = min(6, remaining)
            remaining -= destroyed
            maximum_burst = max(maximum_burst, destroyed)
        assert remaining == 0 and maximum_burst <= 6, (fps, remaining)

    # Ground loot can be abundant in the original offline map. Hidden weapon
    # meshes must not dirty Unity Transform state at 30/60/120 FPS, while
    # reappearing meshes catch up to the exact elapsed-time spin angle.
    pickup = (SRC / "AarisWeaponPickup.cs").read_text(encoding="utf-8")
    spin = pickup[pickup.index("private void Update()"):
                  pickup.index("private void OnDisable()")]
    assert "private Renderer[] modelRenderers;" in pickup
    assert "modelRenderers = visible.GetComponentsInChildren<Renderer>(true);" in pickup
    assert "pendingSpinDegrees += 19f * Time.deltaTime;" in spin
    assert "if (pendingSpinDegrees >= 360f)" in spin
    assert "Mathf.Repeat(pendingSpinDegrees, 360f)" in spin
    assert "if (renderer == null || !renderer.isVisible) continue;" in spin
    assert spin.index("renderer.isVisible") < spin.index("model.Rotate(")
    assert spin.index("model.Rotate(") < spin.index("pendingSpinDegrees = 0f;")
    assert spin.count("model.Rotate(") == 1
    for fps in (10, 20, 30, 60, 90, 120):
        pending, applied, writes = 0.0, 0.0, 0
        # Four seconds outside the camera, then one second on-screen.
        for frame in range(fps * 5):
            pending += 19.0 / fps
            if pending >= 360.0:
                pending %= 360.0
            visible = frame >= fps * 4
            if visible:
                applied = (applied + pending) % 360.0
                pending = 0.0
                writes += 1
            else:
                assert writes == 0
        assert writes == fps, (fps, writes)
        assert math.isclose(applied, 95.0, abs_tol=1e-7), (fps, applied)

    # Long-hidden pickups keep a bounded angle instead of growing to an
    # imprecise float, even if they stay out of view for several minutes.
    pending = 0.0
    for _ in range(60 * 600):
        pending += 19.0 / 60
        if pending >= 360.0:
            pending %= 360.0
    assert 0 <= pending < 360

    print("PASS: airborne support reuse, single motor, steady FOV writes, "
          "bounded player/animation optional-component lookup, budgeted vehicle/loot streaming, legacy idle pose, collision-safe camera, idle-first pooled SFX, deadline-gated SFX, offscreen loot spin, audio/gloo hotpaths, refresh-aware pacing and adaptive Android quality contracts")
    print("NOT TESTED: Unity compilation, live camera occlusion and device FPS.")


if __name__ == "__main__":
    audit()
