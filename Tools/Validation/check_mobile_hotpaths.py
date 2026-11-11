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

    print("PASS: airborne support reuse, single motor, steady FOV writes, "
          "bounded optional-component lookup and allocation-free footstep matching")
    print("NOT TESTED: Unity compilation, live camera occlusion and device FPS.")


if __name__ == "__main__":
    audit()
