#!/usr/bin/env python3
"""Opt-in Android AI census is 1Hz, allocation-free per frame, and read-only."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2] / "Assets" / "AarisWorld" / "Scripts"
DIRECTOR = (ROOT / "AarisOfflineBRDirector.cs").read_text()
OVERLAY = (ROOT / "AarisMobilePerfOverlay.cs").read_text()
UPDATE = OVERLAY.split("private void Update()", 1)[1].split(
    "private float Percentile(", 1)[0]
SAMPLE = DIRECTOR.split("internal static bool TrySampleRivalPerf(", 1)[1].split(
    "private static RaycastHit[] assistHits", 1)[0]


class AIBotCensus(unittest.TestCase):
    def test_sampling_is_after_opt_in_short_circuit(self):
        gate = UPDATE.index("if (!overlayVisible && !recording)")
        sample = UPDATE.index("AarisOfflineBRDirector.TrySampleRivalPerf(out ai)")
        report = UPDATE.index("if (Time.unscaledTime < nextReportAt) return;")
        self.assertLess(gate, report)
        self.assertLess(report, sample)
        self.assertEqual(UPDATE.count("TrySampleRivalPerf(out ai)"), 1)
        self.assertNotIn("TrySampleRivalPerf(", OVERLAY.split("private void Update()", 1)[0])

    def test_all_100_logical_slots_and_visible_rigs_are_counted(self):
        for token in (
            "for (int i = 0; i < RivalCount; i++)",
            "if (rival == null || !rival.IsAlive) continue;",
            "sample.spawned = director.spawnedRivals;",
            "sample.alive++;",
            "rival.AccumulatePerfSample(ref sample);",
            "sample.attackers++;",
            "if (centrallyDormant) sample.dormant++;",
            "if (virtualBody) sample.virtualBodies++;",
            "visualRoot != null && visualRoot.activeInHierarchy",
            "farVisualRoot != null && farVisualRoot.activeInHierarchy",
            "capsule != null && capsule.enabled",
        ):
            self.assertIn(token, DIRECTOR)
        self.assertIn("if (director == null || !director.started) return false;", SAMPLE)

    def test_csv_matches_display_and_reports_unavailable_explicitly(self):
        self.assertIn('string aiLabel = aiReady', UPDATE)
        self.assertIn('AI {13}', UPDATE)
        fields = ("ai_spawned", "ai_alive", "ai_dormant", "ai_virtual",
                  "ai_detailed_rigs", "ai_far_meshes", "ai_physical_receivers",
                  "ai_attackers")
        for field in fields:
            self.assertIn(field, OVERLAY)
        for field in ("spawned", "alive", "dormant", "virtualBodies",
                      "detailedRigs", "farMeshes", "physicalReceivers", "attackers"):
            self.assertIn(f".Append(aiReady ? ai.{field} : -1)", UPDATE)
        # Read-only counters must never toggle physics, animator or renderers.
        read = DIRECTOR.split("internal void AccumulatePerfSample(", 1)[1].split(
            "internal void Initialize(", 1)[0]
        self.assertNotIn(".SetActive(", read)
        self.assertNotIn(".enabled =", read)
        self.assertNotIn("new ", read)

if __name__ == "__main__":
    unittest.main()
