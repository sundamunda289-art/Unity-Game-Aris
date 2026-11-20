#!/usr/bin/env python3
"""Static regression gate for distant authored-collider settlement overlap.

Requires separate Unity Play Mode + on-device testing for visual confirmation.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts"
EXPANSION = (ROOT / "AarisSettlementExpansion.cs").read_text(encoding="utf-8")
STREAMER = (ROOT / "AarisNearbyColliders.cs").read_text(encoding="utf-8")


class StreamedSettlementSafety(unittest.TestCase):
    def test_cache_ready_before_site_probes(self):
        self.assertIn("DefaultExecutionOrder(-250)", STREAMER)
        start = EXPANSION[EXPANSION.index("private void Start()"):
                          EXPANSION.index("private bool TryRelocateInteractiveSite(")]
        self.assertIn("nearbyColliderBounds = FindFirstObjectByType<AarisNearbyColliders>();", start)
        self.assertLess(start.index("nearbyColliderBounds = FindFirstObjectByType"),
                        start.index("foreach (var site in plan.sites)"))

    def test_streamed_disabled_colliders_guard_physics_gap(self):
        block = EXPANSION[EXPANSION.index("private bool CollidersBlock("):]
        self.assertIn("nearbyColliderBounds.HasPotentialInactiveObstacle(center, half, rotation)", block)
        self.assertLess(block.index("HasPotentialInactiveObstacle("),
                        block.index("Physics.OverlapBoxNonAlloc("))
        self.assertIn("if (n >= obstacles.Length)", block)
        self.assertIn("candidates = Physics.OverlapBox(center, half, rotation,", block)
        self.assertIn("if (collider.bounds.max.y < y + 0.1f) continue;", block)
        self.assertNotIn(".enabled = true", block)
        self.assertNotIn("Physics.SyncTransforms()", block)

    def test_streamer_preserves_authored_streaming_contract(self):
        probe = STREAMER[STREAMER.index("internal bool HasPotentialInactiveObstacle("):
                         STREAMER.index("private void OnEnable()")]
        self.assertIn("streamed[i] && c != null && !c.enabled", probe)
        self.assertIn("worldBounds[i].Intersects(probe)", probe)
        self.assertIn("if (probeVisited[i] == probeStamp) continue;", probe)
        self.assertNotIn(".enabled = true", probe)
        # The oversized-collider safety guard now uses long-index iteration
        # and a non-grid fallback. Preserve both physical cover paths.
        self.assertIn("for (long xCell = firstX; xCell <= lastX; xCell++)", STREAMER)
        self.assertIn("width * depth > MaxCellsPerCollider", STREAMER)
        self.assertIn("foreach (int i in oversizedIndices)", probe)

    def test_relocation_remains_bounded_and_validated(self):
        code = EXPANSION[EXPANSION.index("private bool TryRelocateInteractiveSite("):
                         EXPANSION.index("private static bool CoordinatesValid(")]
        for phrase in ("TryGround(site, terrains,", "TreesBlock(site, terrains)",
                       "CollidersBlock(site, candidateY)", "site.x = originalX;",
                       "site.z = originalZ;"):
            self.assertIn(phrase, code)


    def test_tree_blocked_interactive_homes_try_safe_relocation(self):
        setup = EXPANSION[EXPANSION.index("foreach (var site in plan.sites)"):
                          EXPANSION.index("private bool TryRelocateInteractiveSite(")]
        self.assertIn("bool blockedByTrees = TreesBlock(site, terrains);", setup)
        self.assertIn("bool blockedByCollider = !blockedByTrees && CollidersBlock(site, y);", setup)
        self.assertIn("if (blockedByTrees || blockedByCollider)", setup)
        self.assertIn("yield return RelocateInteractiveSiteAcrossFrames(site, terrains);", setup)
        self.assertIn("relocated = TryRelocateInteractiveSite(site, terrains, out y);", setup)
        self.assertIn("if (!relocated)", setup)
        # No early unconditional tree skip before the relocation decision.
        self.assertNotIn("if (TreesBlock(site, terrains))", setup)
        self.assertLess(setup.index("bool blockedByTrees ="),
                        setup.index("yield return RelocateInteractiveSiteAcrossFrames("))
        # Noninteractive sites must never be moved.
        self.assertIn("if (site.interactive)", setup)
        self.assertIn("if (!relocated)", setup)

    def test_obstacle_classification_only_counts_abandoned_sites(self):
        setup = EXPANSION[EXPANSION.index("bool blockedByTrees ="):
                          EXPANSION.index("Vector3 position = new Vector3(site.x, y - 0.025f")]
        self.assertIn("if (blockedByTrees) skippedTrees++;", setup)
        self.assertIn("skippedObstacle++;", setup)
        self.assertIn('Debug.LogWarning("AARIS_EXPANSION_OBSTACLE_SITE "', setup)
        self.assertLess(setup.index("if (!relocated)"),
                        setup.index("if (blockedByTrees) skippedTrees++;"))
        # Accepting a relocated candidate still requires independent terrain,
        # foliage and collider verification. Invalid sites retain old values.
        relocate = EXPANSION[EXPANSION.index("private bool TryRelocateInteractiveSite("):
                             EXPANSION.index("private static bool CoordinatesValid(")]
        self.assertIn("!TryGround(site, terrains, out _, out float candidateY)", relocate)
        self.assertIn("TreesBlock(site, terrains) || CollidersBlock(site, candidateY)", relocate)
        self.assertIn("site.x = originalX;", relocate)
        self.assertIn("site.z = originalZ;", relocate)


if __name__ == "__main__":
    unittest.main(verbosity=2)
