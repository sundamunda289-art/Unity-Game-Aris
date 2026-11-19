#!/usr/bin/env python3
"""Static regression checks for visual-only Android prefab prewarming.

These checks cannot establish phone FPS, Android stability or Unity compilation.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()


class RivalVisualPrewarm(unittest.TestCase):
    def test_async_loading_sequential_and_delayed(self):
        body = SOURCE.split("private IEnumerator PrewarmRivalVisuals()", 1)[1].split(
            "internal void ResolveRivalAssets", 1
        )[0]
        self.assertIn("new WaitForSecondsRealtime(2f)", body)
        self.assertIn(
            "AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync<GameObject>(",
            body)
        self.assertIn("RivalVisualResourcePaths[i])) == null)", body)
        gate = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileStreamingFrameGate.cs").read_text()
        self.assertIn("Resources.LoadAsync<T>(path)", gate)
        self.assertIn("yield return request;", body)
        self.assertIn("yield return null;", body)
        self.assertIn("rivalVisualPrewarmComplete = true;", body)
        self.assertNotIn("Resources.LoadAll", body)
        self.assertNotIn("Instantiate(", body)

    def test_start_waits_without_advancing_match_clock(self):
        update = SOURCE.split("private void Update()", 1)[1].split(
            "private void Begin()", 1
        )[0]
        self.assertLess(update.index("if (drop != null && !drop.HasLanded) return;"),
                        update.index("if (!rivalVisualPrewarmComplete) return;"))
        self.assertLess(update.index("if (!rivalVisualPrewarmComplete) return;"),
                        update.index("Begin();"))

    def test_character_quality_and_gameplay_rules_unchanged(self):
        self.assertIn("private const int RivalCount = 6;", SOURCE)
        self.assertIn("private const int MaximumSpawnPasses = 4;", SOURCE)
        self.assertIn("Resources.LoadAll<Avatar>(path)", SOURCE)
        self.assertIn("HasRenderableRivalPrefab(candidate)", SOURCE)
        self.assertIn("prewarmedRivalPrefab != null", SOURCE)
        self.assertIn("StartCoroutine(PrewarmRivalVisuals());", SOURCE)
        for path in (
            "AarisCharacters/Primary/RealisticSoldier/RealisticSoldier",
            "AarisCharacters/Primary/Player",
            "AarisCharacters/Kenney/characterMedium",
        ):
            self.assertIn(path, SOURCE)


if __name__ == "__main__":
    unittest.main()
