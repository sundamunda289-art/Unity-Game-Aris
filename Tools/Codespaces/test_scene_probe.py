#!/usr/bin/env python3
"""License-free security/contract regressions for native Codespace scene probing."""
from pathlib import Path
import re
import subprocess
ROOT = Path(__file__).resolve().parents[2]
editor = (ROOT / "Assets/AarisWorld/Editor/AarisCodespaceSceneProbe.cs").read_text()
shell = (ROOT / "Tools/Codespaces/scene_probe.sh").read_text()
controller = (ROOT / "Tools/Codespaces/controller.sh").read_text()
workflow = (ROOT / ".github/workflows/aaris-codespaces-control.yml").read_text()
issue = (ROOT / "Tools/Codespaces/issue_response.py").read_text()
agent = (ROOT / "AGENTS.md").read_text()
assert 'AarisExplorerBuild.ScenePath' in editor
assert 'EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single)' in editor
assert 'terrains.Length != 4' in editor
assert 'SCENE_IS_LFS_POINTER' in editor
assert 'cam.transform.position = new Vector3(0f, 1600f, 0f)' in editor
assert 'cam.orthographic = true' in editor
assert 'cam.Render()' in editor and 'ReadPixels' in editor
assert 'FRAME_NEARLY_UNIFORM' in editor and 'FRAME_MAGENTA_SHADER_FAILURE' in editor
assert 'EditorApplication.isPlaying = true' in editor
assert 'EditorApplication.isPlaying = false' in editor
assert 'Time.frameCount < PlayFrames' in editor
assert 'AarisVehicleWorld' in editor and 'AarisThirdPersonPlayer' in editor
assert 'runtimeErrors == 0' in editor
assert 'EditorApplication.Exit(exit)' in editor
assert 'EditorSceneManager.SaveScene' not in editor and 'AssetDatabase.SaveAssets' not in editor
assert shell.startswith('#!/usr/bin/env bash')
# Shell syntax is a release gate: catch truncated diagnostic pipes before
# a failed native Editor run can consume cloud compute and return no PNG.
check = subprocess.run(["bash", "-n", str(ROOT / "Tools/Codespaces/scene_probe.sh")],
                       capture_output=True, text=True, timeout=10)
assert check.returncode == 0, "scene_probe.sh bash syntax invalid"
assert shell.count('python3 - "$REPORT" "$IMAGE" "$MODE" <<\'PY\'') == 1
assert "grep -E '^AARIS_PLAY_EXCEPTION_(FIRST_FRAME_FAMILY_[A-Z_]+|SUBSYSTEM_[A-Z_]+|STACK_FRAMES=[0-9]+)$' || true" in shell
assert 'MODE="$1"' in shell
assert 'render|play' in shell
assert 'SCENE="Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity"' in shell
assert 'scene-probe' in shell and 'map-overview.png' in shell and 'play-frame.png' in shell
assert 'pgrep -f' in shell and 'AARIS_PROBE_EDITOR_ALREADY_OPEN' in shell
assert '-batchmode' in shell and '-executeMethod' in shell and 'timeout --signal=TERM' in shell
active_shell='\n'.join(line.split('#', 1)[0] for line in shell.splitlines())
assert '-nographics' not in active_shell and 'scrot' not in active_shell and 'xdotool' not in active_shell
assert 'git reset' not in shell and 'git clean' not in shell
assert 'pkill' not in shell and 'kill -9' not in shell and 'eval ' not in shell
assert 'git -C "$SOURCE" fetch --quiet origin main' in shell
assert 'GIT_LFS_SKIP_SMUDGE=1 git -C "$SOURCE"' in shell
assert 'worktree add --detach "$PROJECT" "$REV"' in shell
assert 'ISOLATED_ROOT=' in shell and 'git lfs pull' in shell
assert 'AARIS_PROBE_ISOLATED_GIT_WORKTREE_READY' in shell
# Native Unity SIGSEGV must be distinguishable from compiler, license and LFS failure.
assert 'AARIS_PROBE_NATIVE_SIGSEGV' in shell
assert 'AARIS_PROBE_NATIVE_SIGABRT' in shell
assert 'AARIS_PROBE_NATIVE_KILLED_OR_OOM' in shell
assert 'AARIS_PROBE_LOG_ASSET_IMPORT_FAILURE' in shell
assert 'AARIS_PROBE_LOG_SHADER_FAILURE' in shell
assert 'AARIS_PROBE_LOG_MEMORY_FAILURE' in shell
assert 'AARIS_PROBE_LOG_NATIVE_CRASH' in shell
assert 'AARIS_PROBE_EDITOR_EXIT_NONZERO' in shell
assert 'git reset' not in shell and 'git clean' not in shell
assert 'rm -rf' not in shell and 'git stash' not in shell
assert 'data.get("status")!="passed"' in shell
assert 'data.get("frames",0)<60' in shell
assert 'candidate.read_bytes()[:8]' in shell
for op in ('scene_render', 'scene_play'):
    assert "'CS SCENE " + op.split('_')[1].upper() + "') OP=" + op in controller
    assert op in workflow and op in issue
assert 'Tools/Codespaces/test_scene_probe.py' in workflow
assert 'Only verified Unity camera PNGs' in issue
assert 'CS SCENE RENDER' in agent and 'CS SCENE PLAY' in agent
assert 'CS PHOTO -420 -210' in agent
assert 'AarisCodespaceSceneProbe.CaptureAt' in (ROOT / 'Tools/Codespaces/scene_probe.sh').read_text()
assert 'PhotoOption("X"' in editor and 'PhotoOption("Z"' in editor
assert 'photo-detail.png' in editor and 'photo-context.png' in editor
assert 'Elevation(terrains,' in editor and 'bounds.center' in editor
assert 'SCENE_IS_LFS_POINTER' in editor and 'NATIVE_COORDINATE_CAMERA_AND_CONTEXT_RENDERED' in editor
assert "'CS PHOTO '*) OP=photo" in controller
assert 'PHOTO_VALUES=' in controller and 'photo_request.py' in controller
assert 'photo-detail.png' in controller and 'photo-context.png' in controller
assert 'photo' in workflow and 'photo_request.py --selftest' in workflow
assert 'photo' in issue and 'photo_target_x' in issue
assert (ROOT / 'Tools/Codespaces/photo_request.py').exists()
assert 'noVNC' in agent
diag=(ROOT/'Tools/Codespaces/play_probe_diagnostic.sh').read_text()
assert 'play-report.json' in diag and 'unity-probe-editor.log' in diag
assert 'AARIS_PLAY_DIAG_REPORT_FOUND' in diag
assert 'AARIS_PLAY_ERROR_KIND_' in diag and 'AARIS_PLAY_ERROR_SOURCE_' in diag
# Classify the first frame and allowlisted Unity Editor subsystem safely,
# without leaking arbitrary messages, project paths, or user data.
assert 'latest_marker=text.rfind("AARIS_NATIVE_PLAY_REQUESTED")' in diag
assert 'AARIS_PLAY_EXCEPTION_FIRST_FRAME_FAMILY_' in diag
assert 'AARIS_PLAY_EXCEPTION_SUBSYSTEM_' in diag
assert 'AARIS_PLAY_EXCEPTION_STACK_FRAMES=' in diag
assert 'SAFE_PLAY_FINGERPRINT' in shell
assert 'play_probe_diagnostic.sh' in shell
assert 'AARIS_PLAY_EXCEPTION_(FIRST_FRAME_FAMILY_' in shell
assert shell.index('SAFE_PLAY_FINGERPRINT') < shell.index('AARIS_PROBE_EXECUTION_FAILED')
assert 'UnityEditor.UIElements' in diag and 'UnityEditor.GameView' in diag
assert 'print("AARIS_PLAY_EXCEPTION_SUBSYSTEM_"+label)' in diag
assert 'NullReferenceException' in diag and 'AarisVehicleWorld' in diag
assert 'AARIS_PLAY_LOG_COMPILE' not in diag or '"COMPILE":' in diag
assert "'CS PLAY DIAG') OP=play_diag" in controller
assert 'play_diag' in workflow and 'play_diag' in issue
assert 'CS PLAY DIAG' in agent
assert 'public static void DiagnoseFoliage()' in editor
assert 'foliage-original.png' in editor and 'foliage-far-lod.png' in editor
assert 'foliage-ambient-test.png' in editor and 'foliage-report.json' in editor
assert 'foliage-ambient-only.png' in editor and 'foliage-shadows-only.png' in editor
assert 'FOURTH=foliage-ambient-only.png' in controller
assert 'FIFTH=foliage-shadows-only.png' in controller
assert 'treeLODBiasMultiplier' in editor and 'QualitySettings.lodBias = qualityBias' in editor
assert 'QualitySettings.shadows = shadowQuality' in editor
assert "'CS FOLIAGE DIAG') OP=foliage_diag" in controller
assert 'foliage_diag' in workflow and 'foliage_diag' in issue
assert 'CS FOLIAGE DIAG' in agent
assert 'runtimeErrorKind' in editor and 'CategorizePlayError' in editor
# Count Awake/OnEnable/Start errors as well as errors after first Play frame.
onlog = editor.split('private static void OnLog(', 1)[1].split('private static string CategorizePlayError(', 1)[0]
assert 'state != "starting" && state != "running"' in onlog
assert 'bool editorLifecycleOnly' in onlog
assert 'trace.Contains("UnityEditor.EditorApplication")' in onlog
assert '!trace.Contains("Aaris.")' in onlog
assert 'SessionState.SetInt(EditorWarnings,' in onlog
assert 'editorLifecycleWarnings = SessionState.GetInt(EditorWarnings, 0)' in editor
assert '"editorLifecycleWarnings"' in diag
assert 'runtimeErrors == 0' in editor
play_method = editor.split('public static void Play()', 1)[1].split('private static void Tick()', 1)[0]
assert play_method.index('SessionState.SetString(State, "starting")') < play_method.index('EditorApplication.isPlaying = true')
assert 'runtimeErrorSource' in editor and 'CategorizeErrorSource' in editor
assert 'runtime_error_source=' in shell
assert 'if test "$MODE" = play && test -f "$RUN/play-report.json"; then' in shell
assert 'case "$MODE" in' in shell
assert 'rm -rf' not in shell and 'git reset' not in shell and 'git clean' not in shell
# Regression: a restarted Codespace must verify its original authenticated
# native objects locally before trying to redownload the 1.5GB map.
restorer = (ROOT / 'Tools/Codespaces/restore_preserved_source_lfs.py').read_text()
assert 'AARIS_PROBE_LOCAL_FIRST_793_LFS_VERIFIED' in shell
assert shell.index('restore_preserved_source_lfs.py" "$PROJECT"') < shell.index('elif git lfs pull')
assert 'AARIS_PRESERVED_LFS_SNAPSHOT_VERIFIED=' in restorer
assert 'EXPECTED_793_796_OR_822_LFS_POINTERS_NOT_FOUND' in restorer
assert 'len(paths) not in (793, 796, 822)' in restorer
assert 'ANDROID_SNAPSHOTS = Path("/tmp/aaris-native-android-build-worktrees")' in restorer
assert 'AARIS_CODESPACE_ORIGINAL_TREE_ARCHIVE_HASH_VERIFIED=18' in restorer
assert 'AARIS_PRESERVED_LFS_ORIGINAL_WORKSPACE_UNCHANGED=yes' in restorer
# Foliage baselines must be captured after loading the native saved scene.
# Otherwise the Editor's previous scene contaminates the shadows-only arm.
foliage_method = editor.split('public static void DiagnoseFoliage()', 1)[1].split(
    '[MenuItem("Aaris/CI/Native Play Mode smoke test")]', 1)[0]
scene_load_index = foliage_method.index('terrains = LoadNativeScene();')
for setting in ('qualityBias = QualitySettings.lodBias;',
                'shadowQuality = QualitySettings.shadows;',
                'ambientMode = RenderSettings.ambientMode;',
                'ambientColor = RenderSettings.ambientLight;',
                'ambientIntensity = RenderSettings.ambientIntensity;'):
    assert foliage_method.index(setting, scene_load_index) > scene_load_index, setting
assert 'report.savedSceneAmbientMode = ambientMode.ToString();' in foliage_method
assert 'report.savedSceneShadows = shadowQuality.ToString();' in foliage_method
assert 'report.savedSceneAmbientIntensity = ambientIntensity;' in foliage_method
print('AARIS_FOLIAGE_SAVED_SCENE_BASELINE_ORDER_PASS')
print('AARIS_NATIVE_SCENE_PROBE_STATIC_PASS')
