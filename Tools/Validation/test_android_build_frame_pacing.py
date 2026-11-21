#!/usr/bin/env python3
"""Source-level regression for Unity 6 Android-only frame pacing; not an APK benchmark."""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PATH = ROOT / "Assets/AarisWorld/Editor/AarisAndroidFramePacingBuild.cs"
META = ROOT / "Assets/AarisWorld/Editor/AarisAndroidFramePacingBuild.cs.meta"


class AndroidBuildFramePacingTests(unittest.TestCase):
    def test_callback_exists_and_is_editor_only(self):
        src = PATH.read_text()
        self.assertTrue(src.startswith("#if UNITY_EDITOR\n"))
        self.assertIn("IPreprocessBuildWithReport", src)
        self.assertIn("OnPreprocessBuild(BuildReport report)", src)
        self.assertIn("callbackOrder", src)
        self.assertTrue(src.rstrip().endswith("#endif"))

    def test_android_only_and_idempotent(self):
        src = PATH.read_text()
        body = src.split("public void OnPreprocessBuild", 1)[1]
        self.assertRegex(body, r"report\.summary\.platform\s*!=\s*BuildTarget\.Android")
        self.assertLess(body.index("return;"), body.index("PlayerSettings.Android.optimizedFramePacing"))
        self.assertIn("if (!PlayerSettings.Android.optimizedFramePacing)", body)
        self.assertEqual(body.count("PlayerSettings.Android.optimizedFramePacing = true;"), 1)

    def test_preserves_authored_visual_settings(self):
        src = PATH.read_text()
        for forbidden in ("renderScale", "shadowDistance =", "msaaSampleCount =",
                          "treeDistance =", "detailObjectDistance =", "lodBias =",
                          "Screen.SetResolution", "Application.targetFrameRate ="):
            self.assertNotIn(forbidden, src)

    def test_unity_meta_has_stable_guid(self):
        self.assertRegex(META.read_text(), r"(?m)^guid: [0-9a-f]{32}$")


if __name__ == "__main__":
    unittest.main(verbosity=2)
