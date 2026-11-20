#!/usr/bin/env python3
"""Saved-player binding and Editor QA fail-fast source contract.

This is a static preflight, NOT native Unity import or screenshot certification.
"""
from pathlib import Path
import re

root = Path(__file__).resolve().parents[2]
editor = (root / "Assets/AarisWorld/Editor/AarisPoseFrames.cs").read_text()
scene = (root / "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity").read_text()
prefab = (root / "Assets/AarisWorld/Player/AarisPlayer.prefab").read_text()
meta = (root / "Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs.meta").read_text()
script = (root / "Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs").read_text()

guid = re.search(r"^guid:\s*([0-9a-f]{32})$", meta, re.M)
assert guid is not None
assert "public sealed class AarisThirdPersonPlayer : MonoBehaviour" in script
assert "namespace Aaris.MapReview" in script
assert f"guid: {guid.group(1)}" in prefab

player_name = "  m_Name: AarisPlayer\n"
assert scene.count(player_name) == 1, "Expected exactly one saved scene actor"
idx = scene.index(player_name)
game_object = scene.rfind("--- !u!1 &", 0, idx)
assert game_object >= 0
scene_segment = scene[game_object:scene.find("\n--- !u!", idx)]
object_id = re.search(r"--- !u!1 &(\d+)", scene_segment).group(1)
assert "  m_IsActive: 1" in scene_segment
component_ids = re.findall(r"component: \{fileID: (\d+)\}", scene_segment)
assert component_ids, "Player object has no saved components"
matched = False
for component_id in component_ids:
    marker = f"--- !u!114 &{component_id}\nMonoBehaviour:"
    if marker not in scene:
        continue
    start = scene.index(marker)
    end = scene.find("\n--- !u!", start)
    component = scene[start:end if end >= 0 else None]
    if (f"m_GameObject: {{fileID: {object_id}}}" in component
            and f"guid: {guid.group(1)}" in component
            and "Aaris.MapReview.AarisThirdPersonPlayer" in component):
        matched = True
assert matched, "Saved scene Player MonoBehaviour lost script GUID binding"

preflight = editor[editor.index("static void VerifyNativeSceneBindings("):
                   editor.index("public static void CaptureBatch()", editor.index(
                       "static void VerifyNativeSceneBindings("))]
for token in (
    "AssetDatabase.LoadAssetAtPath<MonoScript>(",
    "monoScript.GetClass()",
    "GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(player)",
    'player.GetComponent<AarisThirdPersonPlayer>()',
    'prefab.GetComponent<AarisThirdPersonPlayer>()',
    "QA_PLAYER_FBX_NOT_MATERIALIZED",
    'File.OpenRead(fbxFile)',
    'stream.Length',
    '"Kaydara FBX Binary"',
    '" fbxBytes="',
    "QA_PLAYER_SCRIPT_BINDING_MISSING",
    "AARIS_POSE_QA_PREFLIGHT",
):
    assert token in preflight, f"Missing QA preflight protection: {token}"
assert "new FileInfo(fbxFile).Length" not in preflight
assert "new GameObject" not in preflight and "AddComponent" not in preflight
assert "QA_BOOTSTRAP_DETAIL roots=" in editor
assert "FindObjectsInactive.Include, FindObjectsSortMode.None" in editor
assert "motorsIncludeInactive=" in editor
assert "dropsIncludeInactive=" in editor
assert "rootMissingScripts=" in editor
assert "VerifyNativeSceneBindings(scene);" in editor
assert "MOTION_TIMESTAMP stage=" in editor
assert 'Time.unscaledTime.ToString("F3")' in editor
print("PASS: scene Player GUID matches its source and QA fails fast on missing native bindings")
print("NOT TESTED: Unity MonoScript.GetClass(), runtime actor spawn or 16/30 screenshots")
