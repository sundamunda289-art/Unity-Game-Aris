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
    weapon_hud = source("AarisWeaponHUD.cs")
    vehicle_hud = source("AarisVehicleHUD.cs")
    tactical = source("AarisOfflineTacticalHUD.cs")
    # Medkits cannot disappear solely because a dense settlement fills the
    # bounded overlap buffer. A saturated query must inspect ALL colliders,
    # still rejecting actual non-terrain blockers (no wall/floor clipping).
    medical_spawn = tactical.split("private bool TrySpawnMedicalCase(", 1)[1].split(
        "private static Material MedicalPaint", 1
    )[0]
    assert "Collider[] nearby = medicalHits;" in medical_spawn
    assert "if (count >= medicalHits.Length)" in medical_spawn
    assert "nearby = Physics.OverlapBox(center, halfExtents," in medical_spawn
    assert "count = nearby.Length;" in medical_spawn
    assert "Collider c = nearby[i];" in medical_spawn
    assert "!(c is TerrainCollider)" in medical_spawn
    assert "if (count >= medicalHits.Length) return false;" not in medical_spawn

    health = source("AarisOfflineHealth.cs")
    # Helmets share the bounded authored medical loot budget, mitigate only
    # available durability and are never incorrectly counted as medkits.
    assert "public bool TryEquipHelmet()" in health
    # Fatal HP notifications must expose IsDead=true to subscribers,
    # before the one-shot death event reaches the match state machine.
    damage = health.split("public void ApplyDamage(", 1)[1].split(
        "public void RestoreHealth(", 1
    )[0]
    assert "if (current <= 0f) dead = true;" in damage
    assert damage.index("if (current <= 0f) dead = true;") < damage.index(
        "HealthChanged?.Invoke(this);"
    ) < damage.index("Died?.Invoke(this);")
    assert "Mathf.Min(helmetDurability, amount * helmetProtection)" in health
    assert "medicalHealth.TryEquipHelmet()" in tactical
    assert "if (!site.isHelmet) medkits++;" in tactical
    assert "helmetVisualPool.Push(visual)" in tactical
    assert 'new GameObject("Aaris Streamed Helmet")' in tactical
    assert "out RaycastHit overhead, 7f" in tactical
    assert 'helmetIndicator.GetComponent<Image>().raycastTarget = false;' in tactical

    # The eight streamed 3D medkits must reuse visuals without relaxing
    # terrain, rooftop or wall checks or leaking their shared materials.
    medical_stream = tactical.split("private void StreamMedicalSites()", 1)[1].split(
        "private bool CanCollectMedical(", 1
    )[0]
    assert medical_stream.count("RecycleMedicalCase(site);") == 2
    assert "Destroy(site.visual);" not in medical_stream
    assert "medicalVisualPool.Push(visual);" in tactical
    assert "visual.SetActive(false);" in tactical
    assert "GameObject reused = medicalVisualPool.Pop();" in medical_spawn
    assert medical_spawn.index("Physics.OverlapBoxNonAlloc(") < medical_spawn.index(
        "GameObject reused = medicalVisualPool.Pop();"
    )
    assert "reused.SetActive(true);" in medical_spawn
    assert "while (medicalVisualPool.Count > 0)" in tactical
    assert "if (medcasePaint != null) Destroy(medcasePaint);" in tactical


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
    assert "WriteWeightIfChanged(weaponUpperMixer, lastUpperWeights, 0, 1f - attackRatio);" in visual
    assert "WriteLayerWeightIfChanged(layerWeight);" in visual
    assert all(f"WriteWeightIfChanged(mixer, lastBaseWeights, {i}," in visual for i in range(12))
    assert "mixer.SetInputWeight(12" not in visual
    assert "float stanceScale = stanceTotal > 1f ? pose / stanceTotal : 1f;" in visual
    assert "crouchBlend * stanceScale" in visual and "proneBlend * stanceScale" in visual
    assert "HasProneClearance" in state
    assert "return HasProneClearance(worldPosition, transform.forward);" in state, (
        "Prone strafing footprint must align with the facing body, not travel vector"
    )
    assert "horizontalVelocity = Vector3.ClampMagnitude(horizontalVelocity, speed);" in motor, (
        "A crouch/prone transition must not inherit excess sprint velocity"
    )
    assert "FindFirstObjectByType<AarisMobileRuntime>() == null" in motor, (
        "Player Awake must not overwrite AarisMobileRuntime FPS settings"
    )
    # Saturation must never accept a partial hit set or falsely reject
    # valid standing/crawling solely because the fixed buffer filled.
    assert "if (count >= nearHits.Length)" in state
    assert "headroomHits = Physics.OverlapCapsule(low, high, radius, mask," in state
    assert "proneHits = Physics.OverlapCapsule(head, legs, 0.245f, mask," in state
    assert "muzzleHit" in gun
    # The fixed NonAlloc ray buffer can saturate inside dense buildings.
    # A full unsorted buffer is never enough to decide the nearest impact.
    assert "if (count >= rayHits.Length)" in gun
    assert "candidates = Physics.RaycastAll(ray, range," in gun
    assert "count = candidates.Length;" in gun
    assert "candidates[i].distance" in gun
    assert "if (saturated) continue" not in gun

    # Melee must not silently cancel a valid target when 32 nearby static
    # colliders fill the unsorted NonAlloc buffer. Even the 128-slot retry
    # needs a complete-query fallback in pathological dense settlements.
    punch = state.split("private void Punch()", 1)[1].split(
        "private void OnDisable()", 1
    )[0]
    assert "Collider[] punchHits = nearHits;" in punch
    assert "if (count >= nearHits.Length)" in punch
    assert "punchHits = crowdedHits;" in punch
    assert "Physics.OverlapSphereNonAlloc(center, 0.48f, crowdedHits," in punch
    assert "if (count >= crowdedHits.Length)" in punch
    assert "punchHits = Physics.OverlapSphere(center, 0.48f, mask," in punch
    assert "count = punchHits.Length;" in punch
    assert "var collider = punchHits[i];" in punch
    assert "Collider victim = punchHits[best];" in punch
    assert "if (count >= nearHits.Length) return;" not in punch

    # Android may drop a pointer without PointerUp on app pause, focus loss
    # or orientation changes. Reset the underlying identifier set, not just
    # the public fireHeld Boolean.
    assert "internal void CancelHold() => Release();" in button
    assert "private void ReleaseAllPointers()" in ui
    assert "button.CancelHold()" in ui
    assert "if (!focused) ReleaseAllPointers();" in ui

    # Canvas.enabled=false doesn't call the button's OnDisable. On every
    # non-foot frame we must clear real holds, but avoid redundant native
    # ghost.SetActive(false) calls after the hold has already been cleared.
    guard = "if (holding == int.MinValue && (wall == null || !wall.Aiming))"
    assert guard in tactical
    cancel = tactical.split("public void CancelHold()", 1)[1]
    assert cancel.index(guard) < cancel.index("holding = int.MinValue;")
    assert cancel.index("holding = int.MinValue;") < cancel.index("wall.CancelAim();")
    assert "if (wallTouch != null) wallTouch.CancelHold();" in tactical
    assert "private void OnDisable() => CancelHold();" in tactical
    assert "private void OnApplicationPause(bool paused)" in tactical
    assert "private void OnApplicationFocus(bool focused)" in tactical

    # Optional flight/vehicle components may be missing on stripped player
    # prefabs. Don't burn two GetComponent calls per frame; refresh on a
    # bounded cadence, but force a fresh check before the irreversible throw.
    assert "private float nextOptionalComponentRefresh;" in tactical
    update_body = tactical.split("private void Update()", 1)[1].split(
        "public bool TryGiveGrenades", 1
    )[0]
    refresh_body = tactical.split("private void RefreshOptionalComponents(", 1)[1].split(
        "private void Update()", 1
    )[0]
    throw_body = tactical.split("public void ThrowFrag()", 1)[1].split(
        "private void Build()", 1
    )[0]
    assert "RefreshOptionalComponents();" in update_body
    assert "GetComponent<" not in update_body
    assert "Time.unscaledTime < nextOptionalComponentRefresh" in refresh_body
    assert "nextOptionalComponentRefresh = Time.unscaledTime + 1f;" in refresh_body
    assert "if (drop == null) drop = GetComponent<AarisOfflineDrop>();" in refresh_body
    assert "if (vehicle == null) vehicle = GetComponent<AarisVehicleSession>();" in refresh_body
    assert throw_body.index("RefreshOptionalComponents(true);") < throw_body.index(
        "if ((drop != null && !drop.HasLanded)"
    )
    assert throw_body.index("Time.time < nextThrow") < throw_body.index(
        "RefreshOptionalComponents(true);"
    )
    for fps in (10, 15, 30, 60, 90, 120):
        next_refresh = 0.0
        component_polls = 0
        for frame in range(5 * fps):
            now = frame / fps
            if now < next_refresh:
                continue
            next_refresh = now + 1.0
            component_polls += 1
        assert 4 <= component_polls <= 6, (fps, component_polls)

    # Reject grants at capacity; a saturated positive pickup must not
    # report success or consume its source. Limit the increment before
    # addition to avoid signed-int overflow on adversarial large amounts.
    grant = tactical.split("public bool TryGiveGrenades(int amount)", 1)[1].split(
        "public void ThrowFrag()", 1
    )[0]
    assert "if (amount <= 0 || fragGrenades >= 10) return false;" in grant
    assert "fragGrenades += Mathf.Min(amount, 10 - fragGrenades);" in grant
    assert "Mathf.Clamp(fragGrenades + amount" not in grant
    for starting in (0, 3, 9, 10):
        for amount in (-5, 0, 1, 2, 10, (2**31 - 1)):
            inventory = starting
            accepted = amount > 0 and inventory < 10
            if accepted:
                inventory += min(amount, 10 - inventory)
            assert accepted == (amount > 0 and starting < 10)
            assert starting <= inventory <= 10
            if not accepted:
                assert inventory == starting
            if amount >= 10 and starting < 10:
                assert inventory == 10

    # Missing Humanoid motion must NOT silently hide the reliable saved
    # player body and turn it into an unanimated FBX T-pose.
    assert "if (!graphReady)" in visual
    assert "Destroy(visual);" in visual
    assert "if (jumpStart == null) jumpStart = jump;" in visual
    assert "if (jumpLand == null) jumpLand = jump;" in visual

    # CharacterController.isGrounded can blink false for a single mobile frame
    # while walking down a stair lip. Do not pop the full-body jump/fall pose.
    # Explicit jump timestamps must always bypass this grace immediately.
    assert "private float lastReliableGroundContact;" in visual
    assert "if (groundedNow) lastReliableGroundContact = now;" in visual
    assert "now - playerMotor.LastJumpStartedTime" in visual
    assert "jumpAge >= 0f && jumpAge < 0.65f" in visual
    assert "now - lastReliableGroundContact > 0.13f" in visual
    assert "float airTarget = visuallyAirborne ? 1f : 0f;" in visual
    for fps in (10, 15, 20, 30, 60, 90, 120):
        one_frame_lost = 1.0 / fps
        assert not (one_frame_lost > 0.13), (fps, "false airborne on ground seam")
        deliberate_jump = True
        assert deliberate_jump or one_frame_lost > 0.13
        sustained_fall = max(0.2, 3.0 / fps)
        assert sustained_fall > 0.13


    # A crouched/prone bullet must leave the actor's actual eye-height,
    # not a standing muzzle that could see through low cover.
    # The muzzle must track the active actor's stance. The gun caches the
    # owner-bound component to avoid GetComponent on every shot; reject a
    # stale/orphaned component lookup or a standing-height muzzle regression.
    assert "if (cachedOwner != owner)" in gun
    assert "cachedActions = owner.GetComponent<AarisBattleActions>();" in gun
    assert "cachedActions.CameraEyeHeight - 0.15f" in gun
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
    assert "float requestedStep = !launchJump && (wasGrounded || verticalVelocity <= 0f)" in motor
    assert "if (!Mathf.Approximately(controller.stepOffset, requestedStep))" in motor
    assert "if (!Mathf.Approximately(controller.stepOffset, groundedStep))" in motor
    assert "controller.stepOffset = groundedStep;" in motor
    # Stable camera/joystick state should NOT repeatedly dirty native
    # CharacterController state or mobile Canvas layout/text properties.
    assert "if (moveRing.anchoredPosition != stickPosition)" in ui
    assert "if (moveRing.sizeDelta != ringSize)" in ui
    assert "if (moveKnob.anchoredPosition != knobPosition)" in ui
    assert "if (moveKnob.sizeDelta != knobSize)" in ui
    assert "if (captions[0].text != aimCaption)" in weapon_hud
    assert "if (driveCaption.text != desiredCaption)" in vehicle_hud
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
    assert "nextCheck = Time.unscaledTime + 1f;" in nearby
    assert "Physics.SyncTransforms();" in nearby
    assert "private void OnEnable()" in nearby
    assert "streamed[i] && obstacles[i] != null" in nearby
    assert "restoredOnDisable = active != null;" in nearby
    assert "if (c == null || !c.enabled) continue;" in nearby
    assert "active.Clear();" in nearby and "nextActive.Clear();" in nearby
    assert "RefreshNow();" in nearby
    assert (6.5 * (1.0 + 0.15)) < 55 - 2.0

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
          "single motor, grenade inventory saturation and prone/fire collision contracts")
    print("NOT TESTED: Unity Editor compilation, in-game animation retargeting, device FPS")


if __name__ == "__main__":
    audit()
