"""Source and integration contract for the five-control Android battle HUD.

This is NOT a Unity editor playmode test. It verifies that the saved Unity
controller is preserved, that exactly five safe-area action controls exist,
that sprint/stance/fire aren't implemented as a competing movement motor,
and that all new C# script GUIDs are checked in.
"""
from __future__ import annotations

import re
import json
from pathlib import Path
from tree_sitter import Language, Parser
import tree_sitter_c_sharp
import yaml

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "Assets/AarisWorld/Scripts"


def text(name: str) -> str:
    return (SCRIPTS / name).read_text(encoding="utf-8")


def check() -> None:
    filenames = (
        "AarisThirdPersonPlayer.cs",
        "AarisBattleActions.cs",
        "AarisBattleHUD.cs",
        "AarisHudButton.cs",
        "AarisEquippedGun.cs",
        "AarisHudGlyphs.cs",
        "AarisCharacterVisual.cs",
    )
    parser = Parser(Language(tree_sitter_c_sharp.language()))
    guids = set()
    for name in filenames:
        content = (SCRIPTS / name).read_bytes()
        assert not parser.parse(content).root_node.has_error, f"Invalid C# syntax: {name}"
        meta = SCRIPTS / (name + ".meta")
        assert meta.exists(), f"Missing Unity asset GUID for {name}"
        header = yaml.safe_load(meta.read_text(encoding="utf-8"))
        guid = header["guid"]
        assert re.fullmatch(r"[0-9a-f]{32}", guid), f"Invalid GUID on {name}"
        assert guid not in guids, f"Duplicate GUID on {name}"
        guids.add(guid)

    motor = text("AarisThirdPersonPlayer.cs")
    state = text("AarisBattleActions.cs")
    hud = text("AarisBattleHUD.cs")
    glyph = text("AarisHudGlyphs.cs")
    button = text("AarisHudButton.cs")
    gun = text("AarisEquippedGun.cs")
    visual = text("AarisCharacterVisual.cs")

    controls = re.findall(r"^\s+Add\(Action\.(\w+),", hud, flags=re.MULTILINE)
    assert controls == ["Sprint", "Fire", "Jump", "Crouch", "Prone"], controls
    assert len(set(controls)) == 5
    assert "Screen.safeArea" in hud and "CanvasScaler.ScaleMode.ScaleWithScreenSize" in hud
    assert "OnPointerDown" in button and "OnPointerUp" in button
    assert "SetFireHeld(false)" in hud and "CapturesScreenPosition" in hud
    assert "standaloneInputModule" not in hud  # Do not rely on arbitrary user-provided code.

    # A single CharacterController is moved by the original, terrain-checked
    # saved player script. Movement or gravity can never be applied twice.
    assert motor.count("controller.Move(") == 1
    assert "controller.Move(" not in state and "controller.Move(" not in hud
    assert "SafeFooting(candidate)" in motor and "Mathf.Abs(candidate.x) > 975f" in motor
    assert "followCamera" in motor and "AarisBattleHUD.CapturesScreenPosition" in motor
    assert "RequestJump()" in motor and "CancelAutoSprint()" in motor
    assert "horizontalVelocity = Vector3.MoveTowards" in motor
    assert "if (running && intent.sqrMagnitude < 0.04f) intent = Vector2.up;" in motor

    for speed in ["6.5f", "5.7f", "4.2f", "2.4f", "1.1f", "2.0f"]:
        assert speed in motor, f"2km battle movement speed missing: {speed}"
    assert "horizontalBraking" in motor and "normalCameraFov" in motor
    assert "Debug.isDebugBuild" in motor, "Release APK must hide saved debugging buttons"

    # Collider width stays compatible with the saved radius of 0.28m.
    assert "Standing, Crouching, Prone" in state
    assert "crouchHeight = 1.14f" in state and "proneHeight = 0.62f" in state
    assert "capsule.radius * 2f" in state and "HasRoomAbove" in state
    assert "Physics.OverlapCapsuleNonAlloc" in state
    assert "TrySetStance(PlayerStance.Standing)" in state
    assert "OverlapSphereNonAlloc" in state and "equippedGun.TryFire" in state
    assert "Physics.RaycastNonAlloc" in gun
    assert "IAarisDamageable" in gun
    assert "Animator.applyRootMotion = false" not in state
    assert "animator.applyRootMotion = false" in visual
    assert 'FindClip("crouch", "crouch")' in visual and "LastPunchTime" in visual
    # Nine normalized locomotion clips remain on the full body; the
    # pistol-shot reference, punch and ready hold are upper-body masked.
    assert "AnimationMixerPlayable.Create(graph, 9)" in visual
    assert "weaponUpperMixer = AnimationMixerPlayable.Create(graph, 3)" in visual
    assert "AnimationLayerMixerPlayable.Create(graph, 2)" in visual
    assert "SetLayerMaskFromAvatarMask(1, upperBodyMask)" in visual
    assert "LastJumpStartedTime" in motor and "LastLandingTime" in motor
    assert "jumpStartPlayable" in visual and "jumpLandPlayable" in visual
    assert 'FindClip("crouch_walk", "crouch")' in visual
    assert 'FindClip("punch", "punch")' in visual
    assert 'FindClip("shoot", "shoot")' in visual
    source = json.loads((ROOT / "Assets/Resources/AarisCharacters/Primary/Animations/clip-sources.json")
                        .read_text(encoding="utf-8"))
    expected = {
        "jump": "Jump_Loop",
        "jump_start": "Jump_Start",
        "jump_land": "Jump_Land",
        "crouch": "Crouch_Idle",
        "crouch_walk": "Crouch_Fwd",
        "punch": "Punch_Jab",
        "shoot": "Pistol_Shoot",
    }
    for clip, action_name in expected.items():
        assert action_name in source[clip]["source_action"], (clip, source[clip])
        base = ROOT / "Assets/Resources/AarisCharacters/Primary/Animations"
        assert (base / (clip + ".fbx")).is_file(), f"Missing native {clip} FBX LFS pointer"
        assert (base / (clip + ".fbx.meta")).is_file(), f"Missing Unity GUID for {clip}"

    # Glyphs are locally rasterized once, without asset download links.
    assert "new Texture2D(Size, Size" in glyph and "Sprite.Create" in glyph
    assert "https://" not in glyph and "UnityWebRequest" not in hud
    assert "Resources.Load<Sprite>" not in hud
    # Android UI shader must be compiled into the player instead of being
    # reachable only through Shader.Find (which can be build-stripped).
    shader_path = ROOT / "Assets/Resources/AarisHUDOverlay.shader"
    shader_meta = ROOT / "Assets/Resources/AarisHUDOverlay.shader.meta"
    shader_source = shader_path.read_text(encoding="utf-8")
    assert shader_meta.exists(), "Missing stable ShaderImporter GUID"
    assert 'Shader "Aaris/UI/MobileOverlay"' in shader_source
    assert '"RenderPipeline" = "UniversalPipeline"' in shader_source
    assert "UnityGet2DClipping" in shader_source and "Stencil" in shader_source
    assert 'Resources.Load<Shader>("AarisHUDOverlay")' in glyph
    assert "ApplyCanvasShader" in glyph
    for canvas_source in (
        "AarisBattleHUD.cs", "AarisWeaponHUD.cs", "AarisVehicleHUD.cs",
        "AarisOfflineTacticalHUD.cs", "AarisOfflineDrop.cs",
    ):
        assert "AarisHudGlyphs.ApplyCanvasShader(" in text(canvas_source), canvas_source

    print("PASS: five mobile controls, one motor, sprint/stance/combat, "
          "visual animation hooks, all script GUIDs and original offline icons")


if __name__ == "__main__":
    check()
