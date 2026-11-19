#!/usr/bin/env python3
"""Source-level Android audio loading regression (not Android FPS proof)."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisAudioDirector.cs").read_text()


class AndroidAudioBootTest(unittest.TestCase):
    def test_immediate_drop_cues_remain_ready_at_awake(self):
        awake = SOURCE.split("private void Awake()", 1)[1].split(
            "private void LoadOptionalFoleySynchronously()", 1
        )[0]
        self.assertIn('if (!IsImmediateStartupCue(key)) continue;', awake)
        self.assertIn('clips[key] = Resources.Load<AudioClip>("SFX/" + key);', awake)
        self.assertLess(awake.index('clips[key] = Resources.Load<AudioClip>'),
                        awake.index("StartCoroutine(LoadAndroidAudioAcrossFrames());"))
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", awake)
        self.assertIn("LoadOptionalFoleySynchronously();", awake)
        immediate = SOURCE.split("private static bool IsImmediateStartupCue(", 1)[1].split(
            "private IEnumerator LoadAndroidAudioAcrossFrames()", 1
        )[0]
        for key in ("plane_engine_loop", "jump_whoosh", "wind_rush_loop",
                    "canopy_glide_loop", "parachute_open", "parachute_land"):
            self.assertIn('key == "' + key + '"', immediate)
        self.assertEqual(immediate.count('key == "'), 6)

    def test_android_base_cues_are_serialized_off_first_frame(self):
        body = SOURCE.split("private IEnumerator LoadAndroidAudioAcrossFrames()", 1)[1].split(
            "// Only one optional audio request integrates per frame", 1
        )[0]
        self.assertIn("yield return null; // let URP render the native terrain first", body)
        self.assertIn("foreach (string key in Keys)", body)
        self.assertIn("if (IsImmediateStartupCue(key)) continue;", body)
        self.assertIn('Resources.LoadAsync<AudioClip>(\n                    "SFX/" + key)', body)
        self.assertIn("yield return request;", body)
        self.assertIn("clips[key] = request.asset as AudioClip;", body)
        self.assertIn("yield return null; // bound native audio integration per frame", body)
        self.assertLess(body.index("yield return request;"),
                        body.index("yield return LoadOptionalFoleyAcrossFrames();"))
        self.assertEqual(body.count("Resources.LoadAsync<AudioClip>"), 1)
        self.assertNotIn("Shader.WarmupAllShaders", body)

    def test_optional_loads_are_staggered_after_base(self):
        body = SOURCE.split("private IEnumerator LoadOptionalFoleyAcrossFrames()", 1)[1].split("#endif", 1)[0]
        self.assertEqual(body.count("Resources.LoadAsync<AudioClip>"), 5)
        self.assertEqual(body.count("yield return null;"), 5)
        for name in ("grass", "concrete", "impact", "landing", "metal"):
            self.assertIn("yield return " + name + ";", body)

    def test_not_yet_loaded_variants_have_base_fallback(self):
        get_clip = SOURCE.split("private AudioClip GetClip(string name)", 1)[1].split(
            "private static bool IsLowPriorityCue", 1
        )[0]
        self.assertIn("if (selected != null) return selected;", get_clip)
        self.assertIn("return clips.TryGetValue(name, out AudioClip value) ? value : null;", get_clip)

    def test_no_changes_to_native_voice_pool_or_source_sound_catalog(self):
        self.assertIn("private const int VoiceCount = 12;", SOURCE)
        self.assertIn("private const int LocalVoiceCount = 4;", SOURCE)
        self.assertIn("public static IReadOnlyList<string> RequiredCueKeys => Keys;", SOURCE)
        self.assertIn("source.playOnAwake = false;", SOURCE)


if __name__ == "__main__":
    unittest.main(verbosity=2)
