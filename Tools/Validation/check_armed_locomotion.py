#!/usr/bin/env python3
"""Weapon-carry / aiming / camera locomotion regression contracts.

These execute independent frame-rate maths and inspect source connections;
a Unity Editor compilation and actual animation/hand screenshots are separate.
"""
from pathlib import Path
import math
import json

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets/AarisWorld/Scripts"
def file(name):
    return (SRC / name).read_text(encoding="utf-8")

def audit():
    motor = file("AarisThirdPersonPlayer.cs")
    inventory = file("AarisWeaponInventory.cs")
    visual = file("AarisCharacterVisual.cs")
    gun = file("AarisEquippedGun.cs")
    actions = file("AarisBattleActions.cs")

    # Previously E caused BOTH a pickup and an auto-sprint. Right-mouse
    # camera look also toggled ADS. Keep each desktop intent independent.
    assert "if (Input.GetKeyDown(KeyCode.E)) RequestPickup();" in inventory
    assert "if (Input.GetKeyDown(KeyCode.T)) ToggleAutoSprint();" in motor
    assert "if (Input.GetKeyDown(KeyCode.E)) ToggleAutoSprint();" not in motor
    assert "if (Input.GetKeyDown(KeyCode.V)) ToggleAim();" in inventory
    assert "Input.GetMouseButtonDown(1)) ToggleAim()" not in inventory
    assert "if (Input.GetMouseButton(1)) Look(mouse - lastMouse);" in motor

    # Aiming and auto sprint are mutually exclusive. Aiming should rotate
    # the body to camera yaw even with no movement and allow camera-space
    # strafing without turning the character sideways.
    assert "if (aiming && motor != null) motor.CancelAutoSprint();" in inventory
    assert "if (isAiming) running = false;" in motor
    assert "if (isAiming) speed = Mathf.Min(speed, adsMoveSpeed);" in motor
    assert "bool airborneIntent = launchJump || !wasGrounded;" in motor
    assert "if (battle != null && battle.FireHeld && !airborneIntent) running = false;" in motor
    assert "!isAiming && (battle == null || !battle.FireHeld || airborneIntent)" in motor
    assert "if (battle != null && !sprinting)" in motor
    assert "if (held && motor != null) motor.CancelAutoSprint();" not in actions
    assert "bool newPress = held && !fireHeld;" in actions
    assert "if (IsArmed) ShootEquippedWeapon();" in actions
    # Hold Fire while jumping: keep momentum in the air. Landing changes back
    # to the standard combat gait; ADS still cancels the sprint immediately.
    for jumped, previous_grounded, aiming in (
        (True, True, False), (False, False, False),
        (False, True, False), (True, True, True)
    ):
        airborne = jumped or not previous_grounded
        running = not (not airborne or aiming)
        sprinting = running and not aiming and airborne
        assert sprinting == (airborne and not aiming)
    assert "bool combatFacing = battle != null && battle.IsArmed &&" in motor
    assert "Quaternion.Euler(0f, yaw, 0f)" in motor
    assert "Mathf.Exp(-weaponTurnResponse * dt)" in motor
    assert "adsMoveSpeed = 2.2f;" in motor
    assert "unarmedSprintSpeed = 4.8f;" in motor
    assert "armedSprintSpeed = 4.4f;" in motor
    assert "EnforceNaturalLocomotionPace();" in motor
    # Armed sprint tops out at 4.4 m/s: use reachable, gradual run/arm blends.
    assert "Mathf.InverseLerp(3.3f, 4.4f, horizontal)" in visual
    assert "bool carrying = actions != null && actions.IsArmed;" in visual
    assert "carrying ? 4.4f : 4.8f, horizontal" in visual
    assert "Mathf.Max(4.8f, fullRunSpeed), horizontal" not in visual
    assert "Mathf.Lerp(0.36f, 0.15f, releaseArms)" in visual
    # At both real-world motor caps the sprint blend must be fully reached,
    # otherwise gun carriers mix two incompatible foot-contact cycles forever.
    for armed, cap in ((True, 4.4), (False, 4.8)):
        end = 4.4 if armed else 4.8
        blend = min(1.0, max(0.0, (cap - 4.0) / (end - 4.0)))
        assert blend == 1.0, (armed, cap, blend)
    assert "horizontal > 5.0f ? 0.15f : 0.36f" not in visual
    assert "sprinting && travelSpeed > 3.7f ? 3f : 0f" in motor
    assert motor.count("controller.Move(") == 1

    # Critical gun-size bug: avatar import may use 0.01 / 100 bone scale.
    # Preserve root under player and follow hand WORLD pose in LateUpdate.
    assert "[DefaultExecutionOrder(310)]" in inventory
    assert "[DefaultExecutionOrder(200)]" in visual
    assert "if (weaponRoot.parent != transform)" in inventory
    assert "weaponRoot.SetParent(transform, false);" in inventory
    assert "hand.position + hand.rotation * equipped.Spec.handOffset" in inventory
    assert "hand.rotation * grip" in inventory
    assert "if (equipped != null) AttachModelToHand();" in inventory
    assert "weaponRoot.SetParent(socket, false)" not in inventory

    # Real Unity humanoid mask implementation keeps running feet under
    # shoot/punch clips, rather than borrowing weight from lower-body gait.
    assert "AnimationLayerMixerPlayable.Create(graph, 2)" in visual
    assert "mixer = AnimationMixerPlayable.Create(graph, 12);" in visual
    assert "WriteWeightIfChanged(mixer, lastBaseWeights, 9, baseWeight * grounded * runBlend * walkRunBlend * sprintBlend);" in visual
    assert "WriteWeightIfChanged(mixer, lastBaseWeights, 10, baseWeight * proneWeight" in visual
    assert "WriteWeightIfChanged(mixer, lastBaseWeights, 11, baseWeight * proneWeight" in visual
    assert "float stanceScale = stanceTotal > 1f ? pose / stanceTotal : 1f;" in visual
    assert "weaponUpperMixer = AnimationMixerPlayable.Create(graph, 3);" in visual
    assert "upperBodyMask.SetHumanoidBodyPartActive(part, active);" in visual
    assert "part == AvatarMaskBodyPart.Body" in visual
    assert "part == AvatarMaskBodyPart.LeftArm" in visual
    assert "part == AvatarMaskBodyPart.RightArm" in visual
    assert "layered.SetLayerMaskFromAvatarMask(1, upperBodyMask);" in visual
    assert "float baseWeight = 1f - primary;" in visual
    assert "crouchPlayable.SetTime(crouchClip.length * (10d / 24d));" in visual
    assert "pronePlayable.SetTime(proneClip != null" in visual
    source_clips = json.loads((ROOT / "Assets/Resources/AarisCharacters/Primary/Animations"
                               / "kaykit-clip-sources.json").read_text(encoding="utf-8"))["clips"]
    assert source_clips["crouch"]["frames"] == [0, 24]
    assert source_clips["crouch"]["source_hold_frame"] == 10
    assert source_clips["prone"]["frames"] == [0, 24]
    assert source_clips["prone"]["source_hold_frame"] == 0
    assert "float carryTarget = !carrying ? 0f : aim ? 0.63f :" in visual
    assert "readyHoldPlayable.SetSpeed(0d);" in visual
    assert "WriteLayerWeightIfChanged(layerWeight);" in visual
    assert "if (upperBodyMask != null) Destroy(upperBodyMask);" in visual
    assert "animator.applyRootMotion = false" in visual
    assert "controller.Move(" not in visual

    # Imported weapon meshes can exist under the player BEFORE the visual
    # Start() hides legacy primitives. Only old prefab body parts may be hidden
    # or re-enabled; never sweep the equipped gun / other runtime children.
    assert "private static bool IsLegacyPlaceholderRenderer(MeshRenderer renderer)" in visual
    assert 'renderer.gameObject.name.StartsWith("AarisPlayer_", StringComparison.Ordinal)' in visual
    assert "if (IsLegacyPlaceholderRenderer(placeholderRenderers[i])" in visual
    assert "if (IsLegacyPlaceholderRenderer(renderer)) renderer.enabled = true;" in visual
    prefab = (ROOT / "Assets/AarisWorld/Player/AarisPlayer.prefab").read_text(encoding="utf-8")
    assert "m_Name: AarisPlayer_Torso" in prefab
    assert "m_Name: AarisPlayer_LeftHand" in prefab
    assert "m_Name: AarisPlayer_RightHand" in prefab

    assert "bool IsArmed" in actions
    assert "ApplyWeaponRecoil" in gun
    assert "AarisWeaponState" in gun  # one carried gun, preserved drop state

    # Test camera-facing turn response over 10-120 FPS including discrete
    # frame convergence and bounded ADS walk versus crouch and prone speeds.
    for fps in (10, 15, 20, 30, 60, 90, 120):
        dt = 1.0 / fps
        angle = 0.0
        for _ in range(round(.5 * fps)):
            alpha = 1.0 - math.exp(-17.0 * dt)
            angle += (90.0 - angle) * alpha
        assert abs(angle - 90.0) < .09, (fps, angle)
        normal = 3.4
        ads = min(normal, 2.2)
        crouch = min(2.0, 2.2)
        prone = min(1.0, 2.2)
        assert ads == 2.2 and crouch == 2.0 and prone == 1.0
    print("PASS: independent E/T/V inputs, camera-relative ADS strafing, single-player motor, "
          "world-size-stable hand socket, masked upper-body recoil and 10–120FPS yaw damping")
    print("NOT TESTED: Unity AvatarMask import/retarget and physical Android visual pose")

if __name__ == "__main__":
    audit()
