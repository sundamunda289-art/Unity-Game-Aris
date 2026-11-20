#!/usr/bin/env python3
"""Source-level regression checks for Android settlement relocation pacing.

These checks do not replace Unity C# compilation or handset profiling.
"""
import os
import re
import unittest
from pathlib import Path

DEFAULT_SOURCE = (
    Path(__file__).resolve().parents[2]
    / "Assets/AarisWorld/Scripts/AarisSettlementExpansion.cs"
)
SOURCE = Path(os.environ.get("AARIS_SETTLEMENT_SOURCE", DEFAULT_SOURCE))


class AndroidSettlementRelocationFrameSlice(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = SOURCE.read_text(encoding="utf-8")
        cls.paced = cls.source.split(
            "private IEnumerator RelocateInteractiveSiteAcrossFrames(", 1
        )[1].split("private static bool CoordinatesValid(", 1)[0]
        cls.sync = cls.source.split(
            "private bool TryRelocateInteractiveSite(", 1
        )[1].split(
            "private IEnumerator RelocateInteractiveSiteAcrossFrames(", 1
        )[0]

    def test_identical_twelve_fallback_offsets(self):
        match = re.search(
            r"private static readonly Vector2\[\] RelocationOffsets =\s*\{(.*?)\};",
            self.source, re.S
        )
        self.assertIsNotNone(match)
        offsets = re.findall(r"new Vector2\((-?\d+)f, (-?\d+)f\)", match.group(1))
        self.assertEqual(offsets, [
            ("0", "18"), ("18", "0"), ("0", "-18"), ("-18", "0"),
            ("18", "18"), ("-18", "18"), ("18", "-18"), ("-18", "-18"),
            ("0", "26"), ("26", "0"), ("0", "-26"), ("-26", "0"),
        ])
        self.assertIn("foreach (Vector2 offset in RelocationOffsets)", self.sync)
        self.assertIn("foreach (Vector2 offset in RelocationOffsets)", self.paced)

    def test_android_pacing_only_and_editor_original_path(self):
        self.assertRegex(self.source, re.compile(
            r"#if UNITY_ANDROID && !UNITY_EDITOR\s+"
            r".*?yield return RelocateInteractiveSiteAcrossFrames\(site, terrains\);"
            r".*?#else\s+"
            r"relocated = TryRelocateInteractiveSite\(site, terrains, out y\);"
            r"\s+#endif", re.S
        ))

    def test_each_candidate_yields_before_next_probe(self):
        self.assertEqual(self.paced.count("yield return null;"), 1)
        self.assertIn("foreach (Vector2 offset in RelocationOffsets)", self.paced)
        self.assertIn("yield break;", self.paced)
        for predicate in (
            "CoordinatesValid(site)", "TryGround(site, terrains",
            "TreesBlock(site, terrains)", "CollidersBlock(site, candidateY)"
        ):
            self.assertIn(predicate, self.sync)
            self.assertIn(predicate, self.paced)
        self.assertLess(self.paced.index("!CollidersBlock(site, candidateY)"),
                        self.paced.index("yield return null;"))

    def test_cancelled_search_restores_coordinates(self):
        self.assertIn("finally", self.paced)
        self.assertIn("if (!committed)", self.paced)
        self.assertIn("site.x = originalX;", self.paced)
        self.assertIn("site.z = originalZ;", self.paced)
        self.assertIn("relocationSucceeded = false;", self.paced)
        self.assertIn("relocationSucceeded = true;", self.paced)
        self.assertIn("relocationElevation = candidateY;", self.paced)


if __name__ == "__main__":
    unittest.main()
