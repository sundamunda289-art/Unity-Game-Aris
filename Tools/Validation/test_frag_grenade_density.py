#!/usr/bin/env python3
"""Static regression gate for rare crowded offline frag explosions.

Reads the exact Unity C# source, not a synthetic runtime. This verifies branch
contracts; only Unity Play Mode and on-device QA can prove physics behavior.
Use --stdin to audit a fetched Git blob without switching a dirty worktree.
"""
from pathlib import Path
import sys
import unittest


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "Assets/AarisWorld/Scripts/AarisOfflineFragGrenade.cs"
TEXT = sys.stdin.read() if "--stdin" in sys.argv else SOURCE.read_text(encoding="utf-8")
if "--stdin" in sys.argv:
    sys.argv.remove("--stdin")


class FragDensityContracts(unittest.TestCase):
    def test_first_frame_imported_colliders_cannot_bounce(self):
        start = TEXT.index("foreach (var collider in visual.GetComponentsInChildren<Collider>(true))")
        end = TEXT.index("if (grenadeMaterial == null)", start)
        body = TEXT[start:end]
        self.assertIn("collider.enabled = false;", body)
        self.assertIn("Destroy(collider);", body)
        self.assertLess(body.index("collider.enabled = false;"), body.index("Destroy(collider);"))
        self.assertIn("var sphere = grenade.AddComponent<SphereCollider>();", TEXT)

    def test_full_explosion_query_only_on_overflow(self):
        start = TEXT.index("private void Detonate()")
        end = TEXT.index("private bool HasBlastLineOfSight(", start)
        body = TEXT[start:end]
        self.assertIn("Physics.OverlapSphereNonAlloc(blast, blastRadius,", body)
        self.assertIn("Collider[] candidates = overlaps;", body)
        self.assertIn("if (count >= overlaps.Length)", body)
        overflow = body.split("if (count >= overlaps.Length)", 1)[1]
        self.assertIn("candidates = Physics.OverlapSphere(blast, blastRadius,", overflow)
        self.assertIn("damageMask, QueryTriggerInteraction.Ignore);", overflow)
        self.assertIn("count = candidates.Length;", overflow)
        self.assertIn("var collider = candidates[i];", overflow)
        self.assertNotIn("explosion damage safely cancelled", body)
        self.assertNotIn("if (count >= overlaps.Length) return;", body)
        self.assertLess(body.index("Physics.OverlapSphereNonAlloc"), body.index("Physics.OverlapSphere(blast"))

    def test_full_cover_query_never_assumes_missing_hits(self):
        start = TEXT.index("private bool HasBlastLineOfSight(")
        end = TEXT.index("private static IAarisDamageable FindDamageable", start)
        body = TEXT[start:end]
        self.assertIn("Physics.RaycastNonAlloc(ray, rays, length,", body)
        self.assertIn("RaycastHit[] candidates = rays;", body)
        self.assertIn("if (hits >= rays.Length)", body)
        self.assertIn("candidates = Physics.RaycastAll(ray, length,", body)
        self.assertIn("hits = candidates.Length;", body)
        self.assertIn("RaycastHit hit = candidates[i];", body)
        self.assertIn("if (hit.distance < length - 0.02f) return false;", body)
        self.assertNotIn("if (hits >= rays.Length) return false;", body)

    def test_single_damage_and_solid_cover_guards_preserved(self):
        self.assertIn("private readonly HashSet<IAarisDamageable> damaged", TEXT)
        self.assertIn("damaged.Contains(target)", TEXT)
        self.assertIn("damaged.Add(target);", TEXT)
        self.assertIn("HasBlastLineOfSight(blast, near, targetComponent.transform)", TEXT)
        self.assertIn("target.ApplyDamage(amount, owner);", TEXT)
        self.assertIn("Physics.DefaultRaycastLayers", TEXT)
        self.assertIn("QueryTriggerInteraction.Ignore", TEXT)


if __name__ == "__main__":
    unittest.main()
