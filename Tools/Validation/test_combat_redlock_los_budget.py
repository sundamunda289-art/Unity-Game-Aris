#!/usr/bin/env python3
"""Source contracts for frame-cadenced Red-Lock presentation LOS checks."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
HUD = (ROOT / "Assets/AarisWorld/Scripts/AarisCombatFeedbackHUD.cs").read_text()
DIRECTOR = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
GUN = (ROOT / "Assets/AarisWorld/Scripts/AarisEquippedGun.cs").read_text()
QA = (ROOT / "Assets/AarisWorld/Editor/AarisBotCombatIntegrationQA.cs").read_text()

class RedLockFrameBudgetContracts(unittest.TestCase):
    def test_45ms_scheduler_skips_early_frames_without_allocations(self):
        method = HUD.split("internal static bool ScheduleLockedSightCheck(", 1)[1].split(
            "private static string[] CreateDamageLabels()", 1)[0]
        self.assertIn("if (now < nextCheck) return false;", method)
        self.assertIn("nextCheck = now + LockedSightCheckInterval;", method)
        self.assertIn("private const float LockedSightCheckInterval = .045f;", HUD)
        self.assertNotIn("new ", method)

    def test_los_gate_is_before_expensive_cover_raycast(self):
        update = HUD.split("private void LateUpdate()", 1)[1].split("private void OnDestroy()", 1)[0]
        self.assertLess(update.index("ScheduleLockedSightCheck(now, ref nextLockedSightCheck)"),
                        update.index("!AarisOfflineBRDirector.CanKeepAimLock("))
        self.assertEqual(update.count("CanKeepAimLock("), 1)
        self.assertIn("nextLockedSightCheck = now + LockedSightCheckInterval;", update)

    def test_arm_camera_scope_changes_and_invalidations_are_immediate(self):
        update = HUD.split("private void LateUpdate()", 1)[1].split("private void OnDestroy()", 1)[0]
        self.assertIn("if (gun != lastAimGun || aimCamera != lastAimCamera || activeScope != lastAimScope)", update)
        self.assertIn("nextAimCheck = now;", update)
        self.assertIn("nextLockedSightCheck = now;", update)
        self.assertIn("if (!armed || aimCamera == null || !aimCamera.isActiveAndEnabled)", update)
        self.assertIn("lockedRival = null;", update)
        self.assertIn("AarisOfflineBRDirector.TryAcquireAimLock(aimCamera, transform,", update)
        self.assertIn("nextAimCheck = now;", update.split("!AarisOfflineBRDirector.CanKeepAimLock(", 1)[1])

    def test_weapons_still_query_authoritative_cover_each_shot(self):
        firing = GUN.split("public bool TryFire(Camera camera, GameObject owner)", 1)[1].split(
            "private static bool IsAuthoredHeadHit", 1)[0]
        self.assertIn("AarisOfflineBRDirector.TryAcquireAimTarget", firing)
        self.assertIn("FirstExternalHit(", firing)
        self.assertIn("TryGetInactiveCoverHit(", firing)
        aim = DIRECTOR.split("internal static bool CanKeepAimLock(", 1)[1].split(
            "private static bool AimRayReachesRival(", 1)[0]
        self.assertIn("AimRayReachesRival(", aim)
        self.assertIn("MuzzlePathOpenTo(", DIRECTOR)

    def test_native_qa_covers_frame_count_and_deadline(self):
        section = QA.split("private static void VerifyHudLockSightBudget(", 1)[1].split(
            "private static void VerifyRivalCameraWake(", 1)[0]
        for phrase in (
            '"ScheduleLockedSightCheck"',
            "frame < 120",
            "frame / 60f",
            "checks >= 35 && checks <= 42",
            '"Early frame never repeats the same costly LOS query"',
            '"Forty-five millisecond lock refresh admits new cover"',
        ):
            self.assertIn(phrase, section)
        self.assertIn("public static void RunHudLockSightBudgetQA()", QA)

if __name__ == "__main__":
    unittest.main()
