#!/usr/bin/env python3
"""Structural safeguards for rival animation prewarm; not an Android FPS test."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()


class RivalAnimationPrewarm(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.boot = SRC.split("private IEnumerator PrewarmRivalVisuals()", 1)[1].split(
            "private static bool IsUsableRivalClip(", 1
        )[0]
        cls.resolve = SRC.split("internal void ResolveRivalAssets(", 1)[1].split(
            "// Prefab assets", 1
        )[0]

    def test_async_idle_then_run_sequential(self):
        self.assertIn("TryStartOptionalLoadAsync<AnimationClip>(idlePath)", self.boot)
        self.assertIn("TryStartOptionalLoadAsync<AnimationClip>(runPath)", self.boot)
        gate = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileStreamingFrameGate.cs").read_text()
        self.assertIn("Resources.LoadAsync<T>(path)", gate)
        self.assertIn("request.priority = -1;", gate)
        self.assertLess(self.boot.index("yield return idleRequest;"),
                        self.boot.index("TryStartOptionalLoadAsync<AnimationClip>(runPath)"))
        self.assertLess(self.boot.index("yield return runRequest;"),
                        self.boot.index("rivalVisualPrewarmComplete = true;"))
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", self.boot)
        self.assertIn("yield return null;", self.boot)
        self.assertNotIn("Resources.LoadAll", self.boot)
        self.assertNotIn("Instantiate(", self.boot)

    def test_prefab_avatar_reused_but_import_fallback_kept(self):
        self.assertIn("rivalPrefab.GetComponentInChildren<Animator>(true)", self.resolve)
        self.assertIn("attachedAvatar.isValid", self.resolve)
        self.assertIn("attachedAvatar.isHuman", self.resolve)
        self.assertIn("Resources.LoadAll<Avatar>(path)", self.resolve)
        self.assertLess(self.resolve.index("attachedAvatar.isHuman"),
                        self.resolve.index("Resources.LoadAll<Avatar>(path)"))

    def test_primary_animation_beat_fallback_without_quality_downgrade(self):
        self.assertIn("IsUsableRivalClip(prewarmedRivalIdle)", self.resolve)
        self.assertIn("IsUsableRivalClip(prewarmedRivalRun)", self.resolve)
        self.assertIn('LoadRivalClip("AarisCharacters/Primary/Animations/idle")',
                      self.resolve)
        self.assertIn('LoadRivalClip("AarisCharacters/Primary/Animations/run")',
                      self.resolve)
        self.assertIn('LoadRivalClip("AarisCharacters/Kenney/idle")', self.resolve)
        self.assertIn('LoadRivalClip("AarisCharacters/Kenney/run")', self.resolve)
        self.assertIn("private const int RivalCount = 6;", SRC)
        self.assertIn("nextSpawn = Time.time + 0.5f;", SRC)

    def test_sanity_predicate_shared_by_runtime_clip_paths(self):
        predicate = SRC.split("private static bool IsUsableRivalClip(AnimationClip clip)", 1)[1].split(
            "internal void ResolveRivalAssets(", 1
        )[0]
        for term in ("clip != null", "!clip.legacy", "clip.humanMotion", "clip.length > .02f"):
            self.assertIn(term, predicate)
        load = SRC.split("private static AnimationClip LoadRivalClip(string path)", 1)[1].split(
            "[RuntimeInitializeOnLoadMethod", 1
        )[0]
        self.assertIn("IsUsableRivalClip(clip)", load)


if __name__ == "__main__":
    unittest.main()
