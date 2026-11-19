#!/usr/bin/env python3
"""Android gloo ice preview warmup guards; no Unity compile or phone FPS proof."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineGlooWall.cs").read_text()


def between(first, second):
    return SRC.split(first, 1)[1].split(second, 1)[0]


class DeferredGlooArt(unittest.TestCase):
    def test_android_moves_preview_from_first_start_frame(self):
        start = between("private void Start()", "private void Update()")
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", start)
        self.assertIn("StartCoroutine(PrepareGhostAcrossFrames());", start)
        self.assertIn("EnsureGhostCreated();", start)
        self.assertIn("yield return null;", start)
        self.assertIn("yield return AarisGlooWallVisuals.PrewarmAuthoredArt();", start)
        # Prewarm only the original assets; no transient preview hierarchy
        # or imported mesh instance until the first valid BeginAim call.
        preload = between("private IEnumerator PrepareGhostAcrossFrames()", "#endif")
        self.assertNotIn("EnsureGhostCreated();", preload)
        self.assertNotIn("AarisGlooWallVisuals.Create(", preload)
        self.assertNotIn("Instantiate(", preload)
        self.assertIn('AarisGlooWallVisuals.Create("GlooWall_Ghost_Local", true)', start)
        self.assertIn("ghost.SetActive(false);", start)

    def test_early_wall_input_still_has_hologram(self):
        aim = between("public void BeginAim()", "public void CancelAim()")
        self.assertLess(aim.index("EnsureGhostCreated();"), aim.index("aiming = true;"))
        self.assertLess(aim.index("aiming = true;"), aim.index("UpdatePlacement();"))
        self.assertIn("if (!CanUse || Time.time < nextDeployment) return;", aim)
        self.assertIn("ghost.transform.SetPositionAndRotation(proposedPoint, proposedRotation);", SRC)
        self.assertIn("ghostTintValid != placementValid", SRC)

    def test_one_at_a_time_android_requests(self):
        prewarm = between("internal static IEnumerator PrewarmAuthoredArt()", "#endif")
        self.assertIn('Resources.LoadAsync<GameObject>("AarisCombat/GlooWall")', prewarm)
        self.assertIn('Resources.LoadAsync<Material>(', prewarm)
        self.assertIn('"AarisCombat/Materials/GlooWall-ArcticBlue"', prewarm)
        self.assertIn('"AarisCombat/Materials/GlooWall-FrostHighlight"', prewarm)
        self.assertLess(prewarm.index("yield return model;"),
                        prewarm.index("ResourceRequest ice ="))
        self.assertLess(prewarm.index("yield return ice;"),
                        prewarm.index("ResourceRequest frost ="))
        self.assertLess(prewarm.index("yield return frost;"),
                        prewarm.index("finishesResolved = true;"))
        self.assertIn("model.priority = -1;", prewarm)
        self.assertIn("ice.priority = -1;", prewarm)
        self.assertIn("frost.priority = -1;", prewarm)
        self.assertNotIn("Instantiate(", prewarm)
        self.assertNotIn("new GameObject(", prewarm)

    def test_fallback_and_visual_quality_unchanged(self):
        create = between("public static GameObject Create(string name, bool ghost)", "private static void Segment(")
        self.assertIn("AuthoredPrefab();", create)
        self.assertIn("if (!ghost) AuthoredFinishes(out ice, out frost);", create)
        self.assertIn("Object.Instantiate(authoredModel, visual.transform, false)", create)
        self.assertIn("new GameObject(\"VisualRoot\")", create)
        self.assertIn('art.name = "LocalCurvedIceBarrier";', create)
        self.assertIn("AarisGlooWallVisuals", SRC)
        self.assertIn("col.center = CoverCenter;", create)
        self.assertIn("col.size = CoverSize;", create)
        self.assertIn("Segment(visual.transform, \"Center\"", create)
        self.assertIn("var root = AarisGlooWallVisuals.Create(\"GlooWall_Deployed\", false);", SRC)
        self.assertIn("cover.Initialize(wallHealth, wallLifetime);", SRC)

    def test_separate_model_and_material_caches(self):
        prefab = between("private static GameObject AuthoredPrefab()", "private static void AuthoredFinishes(")
        finishes = between("private static void AuthoredFinishes(", "private static Material Material(")
        self.assertIn('Resources.Load<GameObject>("AarisCombat/GlooWall")', prefab)
        self.assertIn("prefabResolved = true;", prefab)
        self.assertIn("finishesResolved = true;", finishes)
        self.assertIn("cachedIce = Resources.Load<Material>", finishes)
        self.assertIn("cachedFrost = Resources.Load<Material>", finishes)
        self.assertIn("RuntimeInitializeLoadType.SubsystemRegistration", SRC)
        self.assertIn("prefabResolved = finishesResolved = false;", SRC)


if __name__ == "__main__":
    unittest.main()
