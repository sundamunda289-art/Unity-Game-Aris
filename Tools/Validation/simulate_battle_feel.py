#!/usr/bin/env python3
"""Deterministic 10–120 FPS tuning contract for Aaris battle movement.

Independent mathematical regression checks. This does NOT execute Unity or
certify PlayerController behavior; Unity playmode and Android device profiling
remain mandatory before merge.
"""
from __future__ import annotations
import math
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
MOTOR = (ROOT / "Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs").read_text("utf-8")
ACTION = (ROOT / "Assets/AarisWorld/Scripts/AarisBattleActions.cs").read_text("utf-8")
VISUAL = (ROOT / "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text("utf-8")
HUD = (ROOT / "Assets/AarisWorld/Scripts/AarisBattleHUD.cs").read_text("utf-8")
GUN = (ROOT / "Assets/AarisWorld/Scripts/AarisEquippedGun.cs").read_text("utf-8")


def setting(name: str) -> float:
    match = re.search(r"\b" + re.escape(name) + r"\s*=\s*([0-9.]+)f\b", MOTOR)
    if match is None:
        raise AssertionError("Missing numeric tuning constant: " + name)
    return float(match.group(1))


def approach(current: float, target: float, rate: float, dt: float) -> float:
    return min(target, current + rate * dt) if current <= target else max(target, current - rate * dt)


def jump(fps: int, release_after_seconds: float | None) -> tuple[float, float]:
    gravity = setting("gravity")
    jump_height = setting("jumpHeight")
    extra = setting("earlyJumpReleaseGravity")
    minimum_ascent = setting("minimumJumpAscentSeconds")
    v = math.sqrt(2 * gravity * jump_height)
    y = apex = 0.0
    dt = 1.0 / fps
    for i in range(fps * 3):
        t = i * dt
        airborne_gravity = gravity * (extra if v > 0 and release_after_seconds is not None
                                     and t >= max(release_after_seconds, minimum_ascent) else 1.0)
        previous_v = v
        v = max(v - airborne_gravity * dt, -40.0)
        y += (previous_v + v) * 0.5 * dt
        apex = max(apex, y)
        if y <= 0 and t > 0.08:
            return apex, t + dt
    raise AssertionError("Jump never landed")


def assert_regression() -> None:
    assert setting("unarmedSprintSpeed") == 6.5
    assert setting("armedSprintSpeed") == 5.7
    assert setting("walkSpeed") == 4.2
    assert setting("crouchMoveSpeed") == 2.4
    assert setting("proneMoveSpeed") == 1.45
    assert setting("combatMoveSpeed") == 2.0
    acc = setting("horizontalAcceleration")
    brake = setting("horizontalBraking")

    # Simulated target approach is consistent across mobile refresh rates,
    # including frame-rate-independent velocity convergence.
    for fps in (10, 15, 20, 30, 60, 120):
        dt = 1.0 / fps
        s = 0.0
        for _ in range(math.ceil(0.55 * fps)):
            s = approach(s, 6.5, acc, dt)
        assert 6.49 <= s <= 6.51, (fps, "sprint acceleration", s)
        for _ in range(math.ceil(0.4 * fps)):
            s = approach(s, 0.0, brake, dt)
        assert 0.0 <= s <= 0.01, (fps, "braking", s)

        full_apex, full_time = jump(fps, None)
        short_apex, short_time = jump(fps, 0.10)
        quick_tap_apex, quick_tap_time = jump(fps, 0.0)
        assert 1.16 <= quick_tap_apex <= 1.28, (fps, "quick tap from stair", quick_tap_apex)
        assert quick_tap_time > 0.35, (fps, "quick tap must not instantly land")
        assert 1.33 <= full_apex <= 1.36, (fps, "frame-independent full jump height", full_apex)
        assert 1.12 <= short_apex < full_apex - 0.08, (fps, "short jump", short_apex)
        assert 0.61 <= short_time < full_time <= 0.86, (fps, "jump timings", short_time, full_time)

    assert MOTOR.count("controller.Move(") == 1, "Duplicate motor/controller update"
    assert "jumpUntil = Time.unscaledTime + 0.20f" in MOTOR
    assert "now - lastGrounded <= 0.15f" in MOTOR
    assert "jumpButtonHeld || Input.GetKey(KeyCode.Space)" in MOTOR
    assert "earlyJumpReleaseGravity" in MOTOR
    assert "minimumJumpAscentSeconds" in MOTOR
    assert "minimumAscentElapsed" in MOTOR
    assert "controller.stepOffset = !launchJump && (wasGrounded || verticalVelocity <= 0f)" in MOTOR
    assert "controller.stepOffset = groundedStep;" in MOTOR
    assert "float rise = savedGroundStepOffset + 0.1f;" in MOTOR
    assert "public float GroundStepOffset" in ACTION
    assert "0.20f : 0.16f" in ACTION
    assert "Time.deltaTime" in MOTOR and "airborneSteering" in MOTOR
    assert "previousVerticalVelocity" in MOTOR and "verticalStepSpeed" in MOTOR
    assert "(previousVerticalVelocity + verticalVelocity) * 0.5f" in MOTOR
    assert "Physics.SphereCast(" in MOTOR
    assert "HasWalkableGroundWithinStep()" in MOTOR
    assert "ProneWallSlide(horizontalVelocity, dt)" in MOTOR
    assert "CollisionFlags.Below" in MOTOR
    assert "Mathf.SmoothDamp(smoothedEyeHeight" in MOTOR
    assert "player.SetJumpHeld(true)" in HUD and "player.SetJumpHeld(false)" in HUD

    # The headroom query must begin above the old capsule, excluding the
    # TerrainCollider at foot level. No inflation to larger radius on prone.
    assert "currentTop - radius * 0.35f" in ACTION
    assert "floor + radius" in ACTION
    assert "capsule.radius - capsule.skinWidth" in ACTION
    assert "count == nearHits.Length" in ACTION
    assert "HasProneClearance" in ACTION
    assert "Physics.OverlapCapsuleNonAlloc(head, legs" in ACTION
    assert "CanAdvanceProne(candidate, horizontalVelocity)" in MOTOR

    # No melee through cover; no hitscan overflow or behind-cover damage.
    assert "Physics.Raycast(origin, delta.normalized" in ACTION
    assert "if (count >= rayHits.Length)" in GUN
    assert "foundHits = Physics.RaycastAll(ray, range," in GUN
    assert "foundHits[i].distance >= minDistance" in GUN
    assert "if (muzzleHitFound &&" in GUN
    assert "if (saturated) return false;" not in GUN
    assert "SameTarget" in GUN
    assert "collider.transform.IsChildOf(owner)" in GUN
    assert "Physics.DefaultRaycastLayers" in GUN
    assert "Vector3 muzzle" in GUN and "muzzleHit" in GUN

    # Pure visuals must never move gameplay capsule.
    assert "landingCompressionMetres" in VISUAL
    assert "sprintForwardLeanDegrees" in VISUAL
    assert "controller.Move(" not in VISUAL
    assert "animator.applyRootMotion = false" in VISUAL
    print("PASS: 10–120 FPS sprint, braking, stair tap/hold jump arc, coyote/buffer, " +
          "downhill probe, stance clearance, combat occlusion and in-place animation contracts")


if __name__ == "__main__":
    assert_regression()
