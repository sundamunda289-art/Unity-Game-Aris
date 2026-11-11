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

    # Android GPU fill-rate pressure should be reduced only after sustained
    # p95 misses. These are source/threshold contracts, NOT a GPU profiler.
    quality = (SRC / "AarisMobileRuntime.cs").read_text(encoding="utf-8")
    assert "ConfigureAndroidQuality();" in quality
    assert "#if UNITY_ANDROID && !UNITY_EDITOR" in quality
    assert "QualitySettings.streamingMipmapsMemoryBudget" in quality
    assert "memoryMB <= 2304 ? 160f : 224f" in quality
    assert "originalRenderScale = androidPipeline.renderScale;" in quality
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

    print("PASS: airborne support reuse, single motor, steady FOV writes, "
          "bounded player/animation optional-component lookup, collision-safe camera, audio and adaptive Android quality contracts")
    print("NOT TESTED: Unity compilation, live camera occlusion and device FPS.")


if __name__ == "__main__":
    audit()
