#!/usr/bin/env python3
"""Android humanoid animation staging contracts; not a Unity editor/device test."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = (ROOT / "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text()
ANIM = ROOT / "Assets/Resources/AarisCharacters/Primary/Animations"


class AndroidTimeSlicedAnimations(unittest.TestCase):
    def test_prewarms_exact_saved_clip_groups_one_per_frame(self):
        names = SRC.split("private static readonly string[] AndroidAnimationNames =", 1)[1].split("};", 1)[0]
        all_names = re.findall(r'"([a-z_]+)"', names)
        self.assertEqual(len(all_names), 14)
        self.assertEqual(len(set(all_names)), len(all_names))
        # The original "fall" source may be absent: the saved graph uses
        # the existing jump fallback. Other 13 reviewed FBXs must be retained.
        missing = {name for name in all_names if not (ANIM / (name + ".fbx")).exists()}
        self.assertTrue(missing.issubset({"fall"}), missing)
        self.assertIn('AnimationClip fall = FindClip("fall", "fall");', SRC)
        self.assertIn("if (fall == null) fall = jump;", SRC)
        staged = SRC.split("private IEnumerator PrewarmAndroidAnimationGroups(", 1)[1].split("#endif", 1)[0]
        self.assertIn("yield return null;\n            // The two calibrated rigs", staged)
        self.assertIn("bool naturalSetComplete = false;", staged)
        self.assertIn("if (candidate == PrimaryModel || candidate == PreviousPrimaryModel)", staged)
        for motion in ("walk", "jog", "run", "crouch"):
            self.assertIn('FindMocapClip(androidNaturalClips, "mocap_' + motion + '")', staged)
        self.assertIn("if (naturalSetComplete &&", staged)
        for name in ("run", "walk", "sprint", "crouch", "crouch_walk"):
            self.assertIn('clipName == "' + name + '"', staged)
        self.assertLess(staged.index("Resources.LoadAll<AnimationClip>(MocapLocomotion)"),
                        staged.index("foreach (string clipName in AndroidAnimationNames)"))
        self.assertIn("foreach (string clipName in AndroidAnimationNames)", staged)
        self.assertIn("Resources.LoadAll<AnimationClip>(location);\n                yield return null;", staged)
        self.assertIn('string prefix = candidate == FallbackModel ? KenneyClips : PrimaryClips;', staged)
        self.assertIn("if (!isActiveAndEnabled) yield break;", staged)
        self.assertEqual(staged.count("Resources.LoadAll<AnimationClip>"), 2)

    def test_async_model_is_complete_before_clip_staging_and_graph(self):
        routine = SRC.split("private IEnumerator LoadCharacterAsynchronously()", 1)[1].split(
            "private IEnumerator PrewarmAndroidAnimationGroups", 1
        )[0]
        for token in ("Resources.LoadAsync<GameObject>(candidate)", "yield return request;",
                      "GameObject source = request.asset as GameObject;",
                      "if (source == null) continue;",
                      "yield return PrewarmAndroidAnimationGroups(candidate);",
                      "if (TryInstantiateCharacter(candidate, source))",
                      "FinishVisualSetup();", "androidClipGroups.Clear();", "yield break;"):
            self.assertIn(token, routine)
        self.assertLess(routine.index("yield return request;"),
                        routine.index("yield return PrewarmAndroidAnimationGroups(candidate);"))
        self.assertLess(routine.index("yield return PrewarmAndroidAnimationGroups(candidate);"),
                        routine.index("TryInstantiateCharacter(candidate, source)"))
        self.assertLess(routine.index("TryInstantiateCharacter(candidate, source)"),
                        routine.index("FinishVisualSetup();"))

    def test_original_authored_clips_and_fail_safe_are_preserved(self):
        find = SRC.split("private AnimationClip FindClip(string name, string preferred)", 1)[1].split(
            "private static bool MatchesMocapClipName", 1
        )[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", find)
        self.assertIn("androidClipGroups.TryGetValue(", find)
        self.assertIn("? staged : Resources.LoadAll<AnimationClip>(location);", find)
        self.assertIn("var clips = Resources.LoadAll<AnimationClip>(location);", find)
        setup = SRC.split("private void SetupAnimations()", 1)[1].split(
            "AnimationClip naturalWalk =", 1
        )[0]
        self.assertIn("androidNaturalClips ?? Resources.LoadAll<AnimationClip>(MocapLocomotion)", setup)
        self.assertIn("#else", setup)
        self.assertIn("Resources.LoadAll<AnimationClip>(MocapLocomotion)", setup)
        self.assertIn("naturalMocapLoaded = naturalWalk != null && naturalJog != null", SRC)
        self.assertIn("if (!naturalMocapLoaded && primaryLoaded && walkClip != runClip)", SRC)
        self.assertIn("graph = PlayableGraph.Create(\"AarisHumanoidLocomotion\");", SRC)

    def test_android_only_and_memory_cleanup(self):
        fields = SRC.split('private const float TargetHeightMetres = 1.78f;', 1)[1].split(
            '[SerializeField', 1
        )[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", fields)
        self.assertIn("Dictionary<string, AnimationClip[]> androidClipGroups", fields)
        self.assertIn("private AnimationClip[] androidNaturalClips;", fields)
        cleanup = SRC.split("private void OnDestroy()", 1)[1].split(
            "ReleaseAnimationGraph();", 1
        )[0]
        self.assertIn("androidClipGroups.Clear();", cleanup)
        self.assertIn("androidNaturalClips = null;", cleanup)
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", cleanup)
        self.assertNotIn("UnityWebRequest", SRC)


if __name__ == "__main__":
    unittest.main(verbosity=2)
