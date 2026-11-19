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
        self.assertEqual(body.count("yield return LoadOptionalFoleyAsset("), 5)
        self.assertIn("yield return null;", body)
        self.assertIn("for (int i = 0; i < grassFoley.Length; i++)", body)
        self.assertIn("for (int i = 0; i < punchFoley.Length; i++)", body)
        for path in ("footstep_grass_", "footstep_concrete_", "impactPunch_medium_",
                     "impactSoft_heavy_000", "impactMetal_light_000"):
            self.assertIn(path, body)

    def test_only_optional_variants_use_shared_gate(self):
        method = SOURCE.split("private IEnumerator LoadOptionalFoleyAsset(", 1)[1].split(
            "private IEnumerator LoadOptionalFoleyAcrossFrames()", 1)[0]
        self.assertIn("AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<AudioClip>(", method)
        self.assertIn("while (isActiveAndEnabled && request == null)", method)
        self.assertIn("if (request == null) yield return null;", method)
        self.assertLess(method.index("yield return request;"), method.index("AudioClip clip = request.asset as AudioClip;"))
        self.assertIn("if (!isActiveAndEnabled) yield break;", method)
        self.assertIn("yield return null;", method)
        self.assertIn("case FoleyGroup.Grass: grassFoley[index] = clip; break;", method)
        self.assertIn("case FoleyGroup.Concrete: concreteFoley[index] = clip; break;", method)
        self.assertIn("case FoleyGroup.Punch: punchFoley[index] = clip; break;", method)
        self.assertIn("case FoleyGroup.SoftLanding: softLandingFoley = clip; break;", method)
        self.assertIn("case FoleyGroup.MetalImpact: metalImpactFoley = clip; break;", method)
        self.assertNotIn("Resources.LoadAsync<AudioClip>", method)

    def test_gameplay_base_cues_remain_outside_optional_gate(self):
        essential = SOURCE.split("private IEnumerator LoadAndroidAudioAcrossFrames()", 1)[1].split(
            "private enum FoleyGroup", 1)[0]
        self.assertIn("ResourceRequest request = Resources.LoadAsync<AudioClip>(", essential)
        self.assertNotIn("TryStartOptionalLoadAsync", essential)
        self.assertIn("if (IsImmediateStartupCue(key)) continue;", essential)
        self.assertIn("clips[key] = request.asset as AudioClip;", essential)
        self.assertIn("yield return LoadOptionalFoleyAcrossFrames();", essential)
        gate = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileStreamingFrameGate.cs").read_text()
        self.assertIn("if (IsAssetCleanupPending())", gate)
        self.assertIn("if (!optionalLoadInFlight.isDone)", gate)
        self.assertIn("if (IsOptionalImportBusy()) return false;", gate)
        self.assertIn("request.priority = -1;", gate)

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
