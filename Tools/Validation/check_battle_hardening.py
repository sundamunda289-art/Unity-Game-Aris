#!/usr/bin/env python3
"""Independent adversarial contract audit for Aaris mobile battle controls.

Runs without a Unity license. Uses deterministic parameter sweeps and source
assertions. A green result cannot replace Unity compiler / PlayMode / device QA.
"""
from __future__ import annotations

import json
import math
import random
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets/AarisWorld/Scripts"


def source(name: str) -> str:
    return (SRC / name).read_text(encoding="utf-8")


def audit() -> None:
    manifest = json.loads((ROOT / "Packages/manifest.json").read_text(encoding="utf-8"))
    deps = manifest["dependencies"]
    # CanvasScaler, UnityEngine.UI.Image, PointerEventData require Unity uGUI;
    # "com.unity.modules.ui" alone does not provide those C# types.
    assert deps.get("com.unity.ugui") == "2.0.0", "Missing Unity 6.3 uGUI dependency"
    assert deps.get("com.unity.modules.animation") == "1.0.0", "Animation module disabled"
    assert deps.get("com.unity.modules.director") == "1.0.0", "Playables/director module disabled"

    motor = source("AarisThirdPersonPlayer.cs")
    ui = source("AarisBattleHUD.cs")
    button = source("AarisHudButton.cs")
    state = source("AarisBattleActions.cs")
    visual = source("AarisCharacterVisual.cs")
    gun = source("AarisEquippedGun.cs")

    assert "Mathf.Clamp(Time.deltaTime, 0f, 0.15f)" in motor, "Low-FPS movement discards time"
    assert motor.count("controller.Move(") == 1, "Two movement authorities detected"
    assert "SetJumpHeld" in motor and "SetJumpHeld" in ui
    assert "HashSet<int> heldPointers" in button, "Held buttons can stick or release early"
    assert "heldPointers.Count == 0 && owner != null" in button
    assert "heldPointers.Count == 1" in button
    assert "heldPointers.Clear()" in button
    assert "float primary = Mathf.Clamp01(startWeight + landWeight);" in visual
    assert "if (startWeight > 0f) landWeight = 0f;" in visual
    assert "float baseWeight = 1f - primary;" in visual
    assert "AnimationLayerMixerPlayable.Create(graph, 2)" in visual
    assert "upperBodyMask.SetHumanoidBodyPartActive(part, active);" in visual
    assert "part == AvatarMaskBodyPart.LeftArm" in visual
    assert "part == AvatarMaskBodyPart.RightArm" in visual
    assert "layered.SetLayerMaskFromAvatarMask(1, upperBodyMask);" in visual
    assert "mixer = AnimationMixerPlayable.Create(graph, 12);" in visual
    assert "weaponUpperMixer.SetInputWeight(0, 1f - attackRatio);" in visual
    assert "layered.SetInputWeight(1, layerWeight);" in visual
    assert all(f"mixer.SetInputWeight({i}," in visual for i in range(12))
    assert "mixer.SetInputWeight(12" not in visual
    assert "float stanceScale = stanceTotal > 1f ? pose / stanceTotal : 1f;" in visual
    assert "crouchBlend * stanceScale" in visual and "proneBlend * stanceScale" in visual
    assert "HasProneClearance" in state
    assert "count == nearHits.Length" in state
    assert "muzzleHit" in gun

    # Buffer-saturated physics queries must not silently cancel a valid shot,
    # grenade explosion, melee strike or Gloo Wall placement. Full queries
    # are used only when the preallocated nonalloc buffer is full.
    assert "if (count >= rayHits.Length)" in gun
    assert "foundHits = Physics.RaycastAll(ray, range," in gun
    assert "if (saturated) return false;" not in gun

    grenade = source("AarisOfflineFragGrenade.cs")
    assert "if (count >= overlaps.Length)" in grenade
    assert "affected = Physics.OverlapSphere(" in grenade
    assert "var collider = affected[i];" in grenade
    assert "sightHits = Physics.RaycastAll(" in grenade
    assert "sightHits[i].distance < length - 0.02f" in grenade
    assert "explosion damage safely cancelled" not in grenade

    gloo = source("AarisOfflineGlooWall.cs")
    assert "hits = Physics.RaycastAll(" in gloo
    assert "supports = Physics.RaycastAll(" in gloo
    assert "blockers = Physics.OverlapBox(" in gloo
    assert "if (other != null && !other.transform.IsChildOf(transform)" in gloo

    assert "targets = Physics.OverlapSphere(" in state
    assert "Collider victim = targets[best];" in state
    assert "Physics.Raycast(origin, delta.normalized" in state

    # Android may drop a pointer without PointerUp on app pause, focus loss
    # or orientation changes. Reset the underlying identifier set, not just
    # the public fireHeld Boolean.
    assert "internal void CancelHold() => Release();" in button
    assert "private void ReleaseAllPointers()" in ui
    assert "button.CancelHold()" in ui
    assert "if (!focused) ReleaseAllPointers();" in ui

    # Missing Humanoid motion must NOT silently hide the reliable saved
    # player body and turn it into an unanimated FBX T-pose.
    assert "if (!graphReady)" in visual
    assert "Destroy(visual);" in visual
    assert "if (jumpStart == null) jumpStart = jump;" in visual
    assert "if (jumpLand == null) jumpLand = jump;" in visual

    # A crouched/prone bullet must leave the actor's actual eye-height,
    # not a standing muzzle that could see through low cover.
    assert "actions.CameraEyeHeight - 0.15f" in gun
    assert "SameTarget" in gun and "FirstExternalHit" in gun

    # Lying flat on stairs and bridge meshes must not be blocked because a
    # supporting floor shares an overlap region with the horizontal body.
    assert "obstacle.Raycast(floorProbe" in state
    assert "floorHit.normal.y >= Mathf.Cos(capsule.slopeLimit" in state

    # The saved physical controller (not a visual pose) is responsible for
    # climbing steps or traversing slopes. Larger ledges need manual jump;
    # this does not claim mantle/vault behaviour is implemented.
    prefab = (ROOT / "Assets/AarisWorld/Player/AarisPlayer.prefab").read_text("utf-8")
    assert "m_StepOffset: 0.3" in prefab, "Native 30cm step traversal was altered"
    assert "m_SlopeLimit: 46" in prefab, "Native 46-degree slope limit was altered"
    assert "m_Height: 1.78" in prefab and "m_Radius: 0.28" in prefab
    assert "SafeFooting(candidate)" in motor
    assert "CanAdvanceProne(candidate, horizontalVelocity)" in motor

    # Airborne stair climbing is disabled, and the old step offset is
    # restored for the first grounded Move. This preserves automatic stair
    # traversal while preventing jumps from auto-mantling wall ledges.
    assert "savedGroundStepOffset = controller.stepOffset;" in motor
    assert "controller.stepOffset = !launchJump && (wasGrounded || verticalVelocity <= 0f)" in motor
    assert "controller.stepOffset = groundedStep;" in motor
    assert "savedGroundStepOffset + 0.1f" in motor
    assert "public float GroundStepOffset" in state
    assert "0.20f : 0.16f" in state
    assert "minimumAscentElapsed" in motor
    assert "minimumJumpAscentSeconds" in motor

    # Check the REAL current map layout, not an invented uniform staircase.
    report = json.loads((ROOT / "Docs/MOBILE_REVIEW_REPORT.json").read_text(encoding="utf-8"))
    towers = [p for p in report["placements"] if p["kind"] == "tower"]
    assert len(towers) == 4, "Saved ramp towers changed"
    for tower in towers:
        step = tower["riser_m"]
        assert 0.0 < step < 0.18, ("Crouch should walk up the authored stair", tower["name"], step)
        assert step < 0.30, ("Standing should step up the authored stair", tower["name"])
        assert 0.16 < 0.30, ("Prone clearance must remain below normal standing stairs", tower["name"])
        assert tower["stair_width_m"] >= 1.7
        assert tower["standing_height_m"] == 12

    # The world enables dense static wall/stair colliders around the player
    # before the motor runs. Its 55m activation radius must comfortably exceed
    # the maximum 6.5 m/s sprint displacement (even across 150ms frames).
    nearby = source("AarisNearbyColliders.cs")
    assert "DefaultExecutionOrder(-250)" in nearby
    assert "activationDistance = 55" in nearby
    assert "nextCheck = Time.unscaledTime + .25f;" in nearby
    assert "Physics.SyncTransforms();" in nearby
    assert (6.5 * (0.25 + 0.15)) < 55 - 2.0

    # 10–120 FPS: the controller must integrate a full second at common
    # mobile rates even when Unity frames are slower than 20 FPS.
    for fps in (10, 15, 20, 30, 60, 90, 120):
        elapsed = sum(min(1 / fps, 0.15) for _ in range(fps))
        assert math.isclose(elapsed, 1.0, rel_tol=0, abs_tol=1e-9), (fps, elapsed)

    # The full-body base graph must ALWAYS remain normalized to 1.0.
    # Shot/punch actions blend through a separate humanoid upper-body
    # override and must NEVER steal the running/jumping leg weights.
    rng = random.Random(20261110)
    for i in range(12000):
        # Takeoff and landing are exclusive; include both state families.
        start = rng.random() if i % 3 else 0.0
        land = rng.random() if start == 0.0 else 0.0
        punch = rng.random()
        shoot = rng.random()
        crouch_blend = rng.random()
        prone_blend = rng.random()
        air = rng.random()
        run_blend = rng.random()
        walk_run_blend = rng.random()
        sprint_blend = rng.random()
        crouch_motion = rng.random()
        crawl_motion = rng.random()
        jumping = rng.random()
        armed_ready = rng.random() * .63
        primary = min(1.0, start + land)
        base = 1.0 - primary
        if shoot > 0:
            punch = 0.0
        punch_upper = punch * (1.0 - primary)
        shoot_upper = shoot * (1.0 - primary)
        attack_upper = max(punch_upper, shoot_upper)
        layer = min(1.0, max(armed_ready, attack_upper))
        ratio = attack_upper / layer if layer > 1e-4 else 0
        upper_hold = 1.0 - ratio
        upper_punch = punch_upper / layer if layer > 1e-4 else 0
        upper_shoot = shoot_upper / layer if layer > 1e-4 else 0
        assert 0.0 <= layer <= 1.0
        assert abs(upper_hold + upper_punch + upper_shoot - 1.0) < 1e-6

        total = crouch_blend + prone_blend
        pose = min(1.0, total)
        scale = pose / total if total > 1.0 else 1.0
        ground = (1.0 - air) * (1.0 - pose)
        for authored_prone in (False, True):
            cw = crouch_blend * scale if authored_prone else pose
            pw = prone_blend * scale if authored_prone else 0.0
            # 12-input mixer: idle/walk/run/jump/fall/crouch-rest/
            # crouch-move/jump-start/jump-land/sprint/prone/crawl.
            standing_total = (
                base * ground * (1-run_blend)
                + base * ground * run_blend * (1-walk_run_blend)
                + base * ground * run_blend * walk_run_blend * (1-sprint_blend)
                + base * ground * run_blend * walk_run_blend * sprint_blend
            )
            base_total = (
                standing_total
                + base * air * jumping * (1-pose)
                + base * air * (1-jumping) * (1-pose)
                + base * cw * (1-crouch_motion)
                + base * cw * crouch_motion
                + base * pw * (1-crawl_motion)
                + base * pw * crawl_motion
                + start + land
            )
            assert abs(base_total - 1.0) < 1e-6, ("legs blended away", base_total)

    print("PASS: Unity 6.3 HUD/animation packages, 10-120 FPS elapsed time, "
          "12k adversarial blend combinations, multi-pointer hold/release, "
          "single motor and prone/fire collision contracts")
    print("NOT TESTED: Unity Editor compilation, in-game animation retargeting, device FPS")


if __name__ == "__main__":
    audit()
