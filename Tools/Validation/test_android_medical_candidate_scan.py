#!/usr/bin/env python3
"""Android medical pickup candidate scanning: source and deterministic parity.

This is not a Unity compilation, Android FPS measurement, or runtime test.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineTacticalHUD.cs").read_text()


def run_scanner(slots, limit=None):
    """Model the existing ordered scan with unchanged two-expensive-probe cap."""
    cursor = 0
    active = []
    scanned_per_frame = []
    probed_per_frame = []
    while cursor < len(slots) and len(active) < 8:
        visited = 0
        probed = 0
        while cursor < len(slots) and len(active) < 8:
            if limit is not None and visited >= limit:
                break
            index = cursor
            cursor += 1
            visited += 1
            eligible, nearby, spawn_ok = slots[index]
            if not eligible or not nearby:
                continue
            probed += 1
            if spawn_ok:
                active.append(index)
            if probed == 2 and cursor < len(slots) and len(active) < 8:
                break
        scanned_per_frame.append(visited)
        probed_per_frame.append(probed)
        assert visited > 0
    return active, cursor, scanned_per_frame, probed_per_frame


class MedicalCandidateScanBudget(unittest.TestCase):
    def test_android_only_bounded_candidate_visit(self):
        section = SOURCE.split("private void StreamMedicalSites()", 1)[1].split(
            "private void RecycleMedicalCase", 1)[0]
        self.assertIn("private const int MaxMobileMedicalCandidateChecksPerFrame = 48;", SOURCE)
        self.assertIn("Application.platform == RuntimePlatform.Android", section)
        self.assertIn("candidateChecks >= MaxMobileMedicalCandidateChecksPerFrame", section)
        self.assertLess(section.index("candidateChecks >= MaxMobileMedicalCandidateChecksPerFrame"),
                        section.index("var site = medicalSites[medicalSpawnCursor++];"))
        self.assertLess(section.index("var site = medicalSites[medicalSpawnCursor++];"),
                        section.index("candidateChecks++;"))
        self.assertLess(section.index("candidateChecks++;"),
                        section.index("if (site.taken || site.visual != null"))
        self.assertIn("if (++probes == MaxMedicalSpawnProbesPerFrame &&", section)
        self.assertIn("medicalSpawnCursor = -1;", section)

    def test_all_slots_are_visited_and_nearby_order_preserved(self):
        slots = []
        for i in range(284):
            eligible = i % 11 != 0
            nearby = i in {1, 50, 51, 91, 93, 149, 177, 181, 220, 241, 269}
            spawn_ok = i not in {91, 220}
            slots.append((eligible, nearby, spawn_ok))
        original = run_scanner(slots)
        budgeted = run_scanner(slots, 48)
        self.assertEqual(original[0], budgeted[0])
        self.assertEqual(original[1], budgeted[1])
        self.assertLessEqual(max(budgeted[2]), 48)
        self.assertLessEqual(max(budgeted[3]), 2)
        self.assertGreater(len(budgeted[2]), len(original[2]))

    def test_all_distant_candidates_are_bounded_too(self):
        slots = [(False, False, False)] * 284
        selected, cursor, per_frame, probes = run_scanner(slots, 48)
        self.assertEqual(selected, [])
        self.assertEqual(cursor, 284)
        self.assertEqual(per_frame, [48, 48, 48, 48, 48, 44])
        self.assertEqual(probes, [0] * 6)


if __name__ == "__main__":
    unittest.main(verbosity=2)
