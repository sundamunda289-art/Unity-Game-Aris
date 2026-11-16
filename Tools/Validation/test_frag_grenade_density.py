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
    def test_successful_frag_throw_cancels_medkit_channel(self):
        # Real player: while standing still, throw used to proceed without
        # stopping the in-progress 3s medkit and allowed a free heal.
        tactical = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineTacticalHUD.cs").read_text(
            encoding="utf-8")
        throw = tactical[tactical.index("public void ThrowFrag()"):
                         tactical.index("private void Build()", tactical.index("public void ThrowFrag()"))]
        self.assertIn("Time.time < nextThrow) return;", throw)
        self.assertIn("fragGrenades <= 0", throw)
        self.assertIn("vehicle != null && vehicle.IsDriving", throw)
        self.assertIn("if (healing) StopHealing();", throw)
        self.assertLess(throw.index("AarisOfflineFragGrenade.Throw("),
                        throw.index("if (healing) StopHealing();"))
        self.assertLess(throw.index("if (healing) StopHealing();"),
                        throw.index("fragGrenades--;"))
        # Never burn a medkit as a side effect of throwing a grenade.
        self.assertIn("medkits--;", tactical[tactical.index("private void UpdateHealing()"):
                                            tactical.index("private void StreamMedicalSites()")])
        self.assertIn("shownSeconds = shownMedkits = -1;", tactical)

    def test_first_frame_imported_colliders_cannot_bounce(self):
        start = TEXT.index("foreach (var collider in visual.GetComponentsInChildren<Collider>(true))")
        end = TEXT.index("if (grenadeMaterial == null)", start)
        body = TEXT[start:end]
        self.assertIn("collider.enabled = false;", body)
        self.assertIn("Destroy(collider);", body)
        self.assertLess(body.index("collider.enabled = false;"), body.index("Destroy(collider);"))
        self.assertIn("var sphere = grenade.AddComponent<SphereCollider>();", TEXT)

    def test_authored_grenade_finishes_are_not_flattened(self):
        for name in ("OliveShell", "OliveLight", "DarkMetal"):
            self.assertIn(f'AarisCombat/Materials/FragGrenade-{name}', TEXT)
            self.assertIn(f'case "{name}":', TEXT)
            material = ROOT / "Assets" / "Resources" / "AarisCombat" / "Materials" / f"FragGrenade-{name}.mat"
            self.assertTrue(material.is_file())
        self.assertIn("if (imported != null && !grenadeFinishesLoaded)", TEXT)
        self.assertIn("slots[i] = imported != null ? ResolveGrenadeFinish(slots[i]) : grenadeMaterial;", TEXT)
        self.assertIn("if (IsUrpFinish(saved)) return saved;", TEXT)

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

    def test_damageable_lookup_reuses_component_storage(self):
        self.assertIn(
            "private readonly List<MonoBehaviour> damageableScratch = new List<MonoBehaviour>(8);",
            TEXT)
        self.assertIn("private IAarisDamageable FindDamageable(Collider collider)", TEXT)
        self.assertIn("damageableScratch.Clear();", TEXT)
        self.assertIn("t.GetComponents(damageableScratch);", TEXT)
        self.assertIn("i < damageableScratch.Count", TEXT)
        self.assertNotIn("GetComponents<MonoBehaviour>()", TEXT)

    def test_single_damage_and_solid_cover_guards_preserved(self):
        self.assertIn("private readonly Dictionary<IAarisDamageable,", TEXT)
        self.assertIn("blastTargets.TryGetValue(target, out var previous)", TEXT)
        self.assertIn("amount < previous.amount", TEXT)
        self.assertIn("collider.GetInstanceID() >= previous.collider.GetInstanceID()", TEXT)
        self.assertIn("blastTargets[target] = (amount, collider);", TEXT)
        self.assertIn("HasBlastLineOfSight(blast, near, targetComponent.transform)", TEXT)
        self.assertIn("foreach (var entry in blastTargets)", TEXT)
        self.assertEqual(TEXT.count(".ApplyDamage("), 1)
        self.assertIn("entry.Key.ApplyDamage(entry.Value.amount, owner);", TEXT)
        self.assertLess(TEXT.index("if (!HasBlastLineOfSight(blast, near, targetComponent.transform))"),
                        TEXT.index("blastTargets[target] = (amount, collider);"))
        self.assertLess(TEXT.index("blastTargets[target] = (amount, collider);"),
                        TEXT.index("foreach (var entry in blastTargets)"))
        self.assertLess(TEXT.index("foreach (var entry in blastTargets)"),
                        TEXT.index("entry.Key.ApplyDamage(entry.Value.amount, owner);"))
        self.assertIn("Physics.DefaultRaycastLayers", TEXT)
        self.assertIn("QueryTriggerInteraction.Ignore", TEXT)


if __name__ == "__main__":
    unittest.main()
