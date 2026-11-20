#!/usr/bin/env python3
"""Source contract + deterministic scheduler model for 4GB hierarchy admission.

This is a CPU-side simulation of frame ordering, not a Unity/Android frame time
measurement. The original authored scenes, meshes and assets are untouched.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets/AarisWorld/Scripts"
SETTLEMENT = (SRC / "AarisSettlementExpansion.cs").read_text()
MEDICAL = (SRC / "AarisOfflineTacticalHUD.cs").read_text()
GATE = (SRC / "AarisMobileStreamingFrameGate.cs").read_text()


def simulate_gate(frame_jobs):
    """One optional build frame admission, retrying denied jobs in later frames."""
    pending = list(frame_jobs)
    accepted = []
    frame = 0
    while pending:
        frame += 1
        claimed = False
        survivors = []
        for name, ready in pending:
            if ready > frame:
                survivors.append((name, ready))
            elif claimed:
                survivors.append((name, ready))
            else:
                accepted.append((frame, name))
                claimed = True
        pending = survivors
        assert frame < 64
    return accepted


class HierarchyAdmission(unittest.TestCase):
    def test_4gb_frame_simulation_preserves_every_optional_build(self):
        # Before the global gate, these independently budgeted systems could
        # all construct hierarchy roots in the same frame.
        requests = [(name, 1) for name in
                    ("vehicle", "weapon", "medical", "settlement", "motorcycle")]
        accepted = simulate_gate(requests)
        self.assertEqual(len(accepted), len(requests))
        self.assertEqual([name for _, name in accepted],
                         [name for name, _ in requests])
        counts = {}
        for frame, _ in accepted:
            counts[frame] = counts.get(frame, 0) + 1
        self.assertEqual(max(counts.values()), 1)

    def test_budget_denial_revisits_same_medical_candidate(self):
        # Busy frame is NOT a failed floor/collider validation. A retry must
        # return to the exact candidate without imposing a 5-second cooldown.
        planned = ["medkit-1", "helmet-2", "medkit-3"]
        cursor = 0
        completed = []
        denied_frames = {1, 2}
        for frame in range(1, 10):
            if cursor == len(planned):
                break
            site = planned[cursor]
            cursor += 1
            denied = frame in denied_frames
            if denied:
                cursor -= 1
                continue
            completed.append(site)
        self.assertEqual(completed, planned)
        self.assertEqual(cursor, len(planned))

    def test_medical_uses_gate_only_after_physics_and_pool_reuse(self):
        body = MEDICAL.split("private bool TrySpawnMedicalCase(", 1)[1].split(
            "private static void HelmetPart(", 1)[0]
        self.assertIn("out bool deferredByBudget", MEDICAL)
        self.assertIn("deferredByBudget = false;", body)
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", body)
        self.assertEqual(body.count(
            "AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()"), 2)
        helmet_pool = body.index("helmetVisualPool.Pop()")
        helmet_gate = body.index("if (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())")
        med_pool = body.index("medicalVisualPool.Pop()")
        med_gate = body.rindex("if (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())")
        self.assertLess(helmet_pool, helmet_gate)
        self.assertLess(med_pool, med_gate)
        self.assertLess(body.index("Physics.OverlapBoxNonAlloc"), helmet_gate)
        self.assertLess(helmet_gate, body.index('new GameObject("Aaris Streamed Helmet")'))
        self.assertLess(med_gate, body.index('new GameObject("Aaris Streamed Medkit")'))

    def test_denied_medical_admission_does_not_set_invalid_spot_cooldown(self):
        scan = MEDICAL.split("private void StreamMedicalSites()", 1)[1].split(
            "private void RecycleMedicalCase(", 1)[0]
        self.assertIn("TrySpawnMedicalCase(site, out bool deferredByBudget)", scan)
        self.assertIn("else if (deferredByBudget)", scan)
        self.assertIn("medicalSpawnCursor--;", scan)
        self.assertLess(scan.index("medicalSpawnCursor--;"),
                        scan.index("else site.retry = now + 5f;"))
        self.assertIn("private const int MaxMedicalVisuals = 8;", MEDICAL)

    def test_settlement_gates_placement_not_native_tree_scans(self):
        body = SETTLEMENT.split("private IEnumerator PlaceSitesAcrossFrames(", 1)[1].split(
            "private bool TryRelocateInteractiveSite(", 1)[0]
        self.assertIn("int attemptsPerFrame = mobile ? 1 : 2;", body)
        self.assertIn("data.GetTreeInstance(i)", body)
        # Original house build and optional facade mesh upload now claim
        # different Android frames; no second independent scheduler exists.
        self.assertEqual(body.count(
            "AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild()"), 2)
        self.assertIn(
            "while (!AarisMobileStreamingFrameGate.TryClaimOptionalModelBuild())\n"
            "                        yield return null;", body)
        self.assertLess(body.index("if (!TryGround(site, terrains"),
                        body.index("TryClaimOptionalModelBuild()"))
        self.assertLess(body.index("TryClaimOptionalModelBuild()"),
                        body.index("AarisModularHouse.Create("))
        self.assertLess(body.index("TryClaimOptionalModelBuild()"),
                        body.index("Instantiate(prefab, position"))
        self.assertIn("placed.Add(instance);", body)
        self.assertLess(body.index("placed.Add(instance);"),
                        body.index("AarisHouseFacadeTrim.Install(instance"))
        self.assertLess(body.index("Instantiate(prefab, position"),
                        body.rindex("TryClaimOptionalModelBuild()"))
        self.assertLess(body.rindex("TryClaimOptionalModelBuild()"),
                        body.index("AarisHouseFacadeTrim.Install(instance"))
        self.assertIn("treeCache.Clear();", body)

    def test_zero_render_detail_changes(self):
        for source in (MEDICAL, SETTLEMENT, GATE):
            self.assertNotIn("QualitySettings.lodBias =", source)
            self.assertNotIn("textureMipmapLimit =", source)
            self.assertNotIn("renderScale =", source)
        self.assertIn("private const int LowMemoryFullLodTreeLimit", (
            SRC / "AarisMobileRuntime.cs").read_text())


if __name__ == "__main__":
    unittest.main(verbosity=2)
