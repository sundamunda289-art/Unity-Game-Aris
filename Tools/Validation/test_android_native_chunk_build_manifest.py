"""Native Android APK must package every authored Unity additive detail scene.

Source contracts plus serialized EditorBuildSettings inspection. Unity Editor and
device verification are separate gates, not implied by these assertions.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
CODE = (ROOT / "Assets/AarisWorld/Editor/AarisExplorerBuild.cs").read_text()
BUILDER = (ROOT / "Assets/AarisWorld/Editor/AarisCodespaceAndroidBuild.cs").read_text()
SHELL = (ROOT / "Tools/Codespaces/android_apk.sh").read_text()
SETTINGS = (ROOT / "ProjectSettings/EditorBuildSettings.asset").read_text()
MAIN = ROOT / "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity"


class AndroidNativeChunkBuildManifestTests(unittest.TestCase):
    def test_project_packages_exact_saved_native_partition(self):
        paths = re.findall(r"(?m)^\s*- enabled: 1\s*\n\s+path: (Assets/[^\n]+\.unity)\s*$", SETTINGS)
        self.assertGreaterEqual(len(paths), 2)
        self.assertEqual(paths[0],
                         "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity")
        self.assertEqual(len(paths), len(set(paths)))
        self.assertTrue(all((ROOT / p).is_file() for p in paths))
        if "/Aaris_Cell_" in paths[1]:
            for p in paths[1:]:
                self.assertRegex(p, r"^Assets/AarisWorld/Scenes/Chunks/Aaris_Cell_[mp]\d+_[mp]\d+_Detail\.unity$")
        else:
            self.assertEqual(paths[1],
                             "Assets/AarisWorld/Scenes/Chunks/Aaris_ForestLookout_Detail.unity")
            for p in paths[2:]:
                self.assertRegex(p, r"^Assets/AarisWorld/Scenes/Chunks/Aaris_District_\w+_Detail\.unity$")
        registered = re.findall(r"(?m)^  - scenePath: ([^\r\n]+)$", MAIN.read_text())
        self.assertEqual(len(registered), len(set(registered)))
        self.assertEqual(set(paths[1:]), set(registered))

    def test_common_build_config_preserves_native_scene_manifest(self):
        self.assertIn("internal static string[] VerifiedBuildScenes()", CODE)
        self.assertIn('paths[0] != ScenePath', CODE)
        self.assertIn('paths[1] != forest', CODE)
        self.assertIn("SavedChunkScenePaths()", CODE)
        self.assertIn("SetEquals(paths.Skip(1))", CODE)
        self.assertIn("NATIVE_CHUNK_BUILD_STREAMER_MANIFEST_MISMATCH", CODE)
        self.assertIn("streamerCount != 1 || paths.Count == 0", CODE)
        self.assertIn("File.ReadLines(ScenePath)", CODE)
        self.assertIn("paths.Distinct(StringComparer.Ordinal).Count()", CODE)
        self.assertIn("EditorBuildSettings.scenes.Length == 0", CODE)
        self.assertIn("if (EditorBuildSettings.scenes == null ||", CODE)
        self.assertIn("scenes = VerifiedBuildScenes()", CODE)
        self.assertIn("!options.scenes.SequenceEqual(VerifiedBuildScenes()", CODE)
        self.assertIn("ANDROID_ADDITIVE_SCENE_MANIFEST_NOT_INCLUDED", CODE)
        self.assertNotIn("scenes = new[] { ScenePath }, locationPathName", CODE)

    def test_codespace_native_build_and_apk_archive_match_manifest(self):
        self.assertIn("var enabled = AarisExplorerBuild.VerifiedBuildScenes();", BUILDER)
        self.assertIn("evidence.sceneCount = enabled.Length;", BUILDER)
        self.assertIn("scenes = enabled,", BUILDER)
        self.assertNotIn("enabled.Length != 1", BUILDER)
        self.assertIn("included=re.findall(", SHELL)
        self.assertIn('d["sceneCount"]==len(included)', SHELL)
        self.assertIn("assert len(included)>=2", SHELL)
        self.assertIn("assert all(pathlib.Path(path).is_file() for path in included)", SHELL)
        self.assertNotIn('d["sceneCount"]==1', SHELL)
        self.assertNotIn("git worktree add", SHELL)

    def test_no_quality_degradation_during_native_build(self):
        for forbidden in ("QualitySettings.shadowDistance =",
                          "UniversalRenderPipelineAsset.renderScale",
                          "TerrainData.SetHeights", "treeDistance =",
                          "Screen.SetResolution("):
            self.assertNotIn(forbidden, CODE)


if __name__ == "__main__":
    unittest.main(verbosity=2)
