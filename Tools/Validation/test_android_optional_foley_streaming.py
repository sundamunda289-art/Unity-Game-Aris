#!/usr/bin/env python3
"""Static regression for Android optional SFX startup (not a Unity runtime test)."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisAudioDirector.cs").read_text()

class AndroidAudioBootTest(unittest.TestCase):
    def test_base_cues_are_ready_first(self):
        awake = SOURCE.split("private void Awake()", 1)[1].split(
            "private void LoadOptionalFoleySynchronously()", 1)[0]
        self.assertLess(awake.index('clips[key] = Resources.Load<AudioClip>'),
                        awake.index("StartCoroutine(LoadOptionalFoleyAcrossFrames());"))
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", awake)
        self.assertIn("LoadOptionalFoleySynchronously();", awake)

    def test_optional_loads_are_staggered(self):
        body = SOURCE.split("private IEnumerator LoadOptionalFoleyAcrossFrames()", 1)[1].split("#endif", 1)[0]
        self.assertEqual(body.count("Resources.LoadAsync<AudioClip>"), 5)
        self.assertEqual(body.count("yield return null;"), 5)
        for name in ("grass", "concrete", "impact", "landing", "metal"):
            self.assertIn("yield return " + name + ";", body)

    def test_not_yet_loaded_variants_have_base_fallback(self):
        get_clip = SOURCE.split("private AudioClip GetClip(string name)", 1)[1].split(
            "private static bool IsLowPriorityCue", 1)[0]
        self.assertIn("if (selected != null) return selected;", get_clip)
        self.assertIn("return clips.TryGetValue(name, out AudioClip value) ? value : null;", get_clip)

if __name__ == "__main__":
    unittest.main(verbosity=2)
