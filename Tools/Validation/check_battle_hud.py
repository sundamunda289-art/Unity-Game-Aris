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

    # Assert each actual field, not an unrelated occurrence of its literal.
    # The current prone speed is 1.45 m/s; 1.1f belongs to another setting.
    expected_speeds = {
        "unarmedSprintSpeed": "6.5",
        "armedSprintSpeed": "5.7",
        "walkSpeed": "4.2",
        "crouchMoveSpeed": "2.4",
        "proneMoveSpeed": "1.45",
        "combatMoveSpeed": "2.0",
    }
    for field, value in expected_speeds.items():
        assert re.search(rf"\b{field}\s*=\s*{value}f\b", motor), (
            f"2km movement setting changed: {field} != {value} m/s"
        )
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
    # Twelve normalized locomotion inputs cover walk/run/sprint,
    # crouch, genuine prone/crawl and jump stages. Punch, shoot and
    # weapon ready are upper-body masked (never a second motion motor).
    assert "AnimationMixerPlayable.Create(graph, 12)" in visual
    assert 'FindClip("sprint", "sprint")' in visual
    assert 'FindClip("prone", "prone")' in visual
    assert 'FindClip("crawl", "crawl")' in visual
    assert "float stanceScale = stanceTotal > 1f ? pose / stanceTotal : 1f;" in visual
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
    # The original Quaternius animation manifest retains only the clips still
    # sourced from that package. All replacement motion clips use the reviewed
    # CC0 KayKit manifest and unchanged local .fbx/.meta GUID pairs.
    for clip, action_name in {
        "jump": "Jump_Loop",
        "jump_start": "Jump_Start",
        "jump_land": "Jump_Land",
        "shoot": "Pistol_Shoot",
    }.items():
        assert action_name in source[clip]["source_action"], (clip, source[clip])
    kaykit = json.loads((ROOT / "Assets/Resources/AarisCharacters/Primary/Animations/kaykit-clip-sources.json")
                        .read_text(encoding="utf-8"))
    assert kaykit["license"] == "CC0-1.0"
    assert "CC0" in (ROOT / "Assets/Resources/AarisCharacters/Primary/LICENSE-KAYKIT-ANIMATIONS.txt").read_text()
    required = {
        "walk": "Walking_A",
        "run": "Running_A",
        "sprint": "Running_B",
        "crouch": "Crouching",
        "crouch_walk": "Crouching",
        "prone": "Crawling",
        "crawl": "Crawling",
        "punch": "Melee_Unarmed_Attack_Punch_A",
    }
    base = ROOT / "Assets/Resources/AarisCharacters/Primary/Animations"
    assert set(kaykit["clips"]) == set(required)
    for clip, action in required.items():
        assert kaykit["clips"][clip]["source_action"] == action, (clip, action)
    for clip in tuple(source) + tuple(required):
        assert (base / (clip + ".fbx")).is_file(), f"Missing local {clip} FBX"
        assert (base / (clip + ".fbx.meta")).is_file(), f"Missing Unity GUID for {clip}"

    # Glyphs are locally rasterized once, without asset download links.
    assert glyph.count("new Texture2D(") == 1 and "BuildAtlas()" in glyph and "Sprite.Create" in glyph
    assert 'AddStickGraphic("Move Stick", AarisHudGlyphs.Ring' in hud
    assert "https://" not in glyph and "UnityWebRequest" not in hud
    assert "Resources.Load<Sprite>" not in hud
    print("PASS: five mobile controls, one motor, sprint/stance/combat, "
          "visual animation hooks, all script GUIDs and original offline icons")


if __name__ == "__main__":
    check()
