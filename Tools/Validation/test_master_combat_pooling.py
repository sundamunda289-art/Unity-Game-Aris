from pathlib import Path
import unittest
ROOT = Path(__file__).resolve().parents[2]
D = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
C = (ROOT / "Assets/AarisWorld/Scripts/AarisDeathCrateWorld.cs").read_text()

class MasterCombatPooling(unittest.TestCase):
    def test_feedback_prebuilt_before_elimination(self):
        create = D.split("private void CreateStatus()", 1)[1].split("private void CreateBoundary()", 1)[0]
        self.assertIn("EnsureRecentKillFeed();", create)
        self.assertIn("EnsurePlayerKillBanner();", create)
        self.assertIn("recentKillFeed.gameObject.SetActive(false);", D)
        self.assertIn("playerKillBanner.SetActive(false);", D)
    def test_logical_crates_preallocated_and_recycled(self):
        self.assertIn("for (int i = 0; i < 100; i++) unusedDrops.Push(new Drop());", C)
        self.assertIn("unusedDrops.Count > 0 ? unusedDrops.Pop() : new Drop()", C)
        self.assertIn("instance.drops.Remove(drop);\n                    instance.RecycleDrop(drop);", C)
        self.assertIn("drops.RemoveAt(i--);\n                    RecycleDrop(drop);", C)
        self.assertIn("drop.looseAmmo = drop.medkits = drop.lastPickupProbe = 0;", C)
        self.assertIn("drop.gun = null;", C)
