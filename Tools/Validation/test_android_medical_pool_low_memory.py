#!/usr/bin/env python3
"""Android medical pickup cache cleanup: static + deterministic behavior model.

Does not execute Unity, profile Android memory, or prove native crash freedom.
"""
from pathlib import Path
import unittest

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisOfflineTacticalHUD.cs").read_text()


def emulate_release(medical, helmets, active, max_per_frame, now, allow_pool_at):
    """Drain the same inactive pool priority while preserving active references."""
    destroyed = []
    if now < allow_pool_at:
        for _ in range(max_per_frame):
            if medical:
                destroyed.append(medical.pop())
            elif helmets:
                destroyed.append(helmets.pop())
            else:
                break
    return destroyed, list(active)


class MedicalLowMemoryPool(unittest.TestCase):
    def test_notification_only_queues_work_for_update(self):
        handler = SOURCE.split("private void OnLowMemory()", 1)[1].split(
            "private void ReleaseInactiveMedicalPoolAcrossFrames()", 1)[0]
        self.assertIn("lowMemoryPending = true;", handler)
        self.assertNotIn("Destroy(", handler)
        self.assertNotIn("UnloadUnusedAssets", handler)
        self.assertNotIn("GC.Collect", handler)
        update = SOURCE.split("private void Update()", 1)[1].split(
            "private void ThrowFrag()", 1)[0]
        self.assertLess(update.index("ReleaseInactiveMedicalPoolAcrossFrames();"),
                        update.index("if (!initialized) return;"))

    def test_clears_inactive_only_with_two_roots_max_per_frame(self):
        body = SOURCE.split("private void ReleaseInactiveMedicalPoolAcrossFrames()", 1)[1].split(
            "private bool CanCollectMedical(", 1)[0]
        self.assertIn("MaxMedicalPoolReleasesPerFrame = 2;", SOURCE)
        self.assertIn("medicalPoolAllowedAt = Time.unscaledTime + 60f;", body)
        self.assertIn("released < MaxMedicalPoolReleasesPerFrame", body)
        self.assertIn("medicalVisualPool.Pop()", body)
        self.assertIn("helmetVisualPool.Pop()", body)
        self.assertIn("if (inactive != null) Destroy(inactive);", body)
        self.assertNotIn("activeMedicalSites.RemoveAt", body)
        self.assertNotIn("site.visual", body)
        self.assertNotIn("while (medicalVisualPool.Count > 0)", body)

    def test_pool_eviction_does_not_modify_gameplay_or_collectible_sites(self):
        medical = list(range(4))
        helmets = list(range(10, 14))
        active = ["med-active-1", "helmet-active-1"]
        releases = []
        for _ in range(4):
            destroyed, surviving = emulate_release(medical, helmets, active, 2, 7, 67)
            self.assertLessEqual(len(destroyed), 2)
            self.assertEqual(surviving, active)
            releases.extend(destroyed)
        self.assertEqual(sorted(releases), [0, 1, 2, 3, 10, 11, 12, 13])
        self.assertEqual(medical, [])
        self.assertEqual(helmets, [])

    def test_regular_pooling_returns_after_recovery_window(self):
        recycle = SOURCE.split("private void RecycleMedicalCase(MedicalSite site)", 1)[1].split(
            "private void OnLowMemory()", 1)[0]
        self.assertIn("if (Time.unscaledTime < medicalPoolAllowedAt)", recycle)
        self.assertIn("Destroy(visual);", recycle)
        self.assertIn("medicalVisualPool.Push(visual);", recycle)
        self.assertIn("helmetVisualPool.Push(visual);", recycle)
        self.assertLess(recycle.index("Destroy(visual);"),
                        recycle.index("medicalVisualPool.Push(visual);"))

    def test_subscribe_lifetime_and_original_scene_cleanup(self):
        self.assertIn("private void OnEnable() { Application.lowMemory += OnLowMemory; }", SOURCE)
        disable = SOURCE.split("private void OnDisable()", 1)[1].split(
            "private void OnApplicationPause", 1)[0]
        self.assertIn("Application.lowMemory -= OnLowMemory;", disable)
        self.assertIn("CancelTacticalInput();", disable)
        teardown = SOURCE.split("private void OnDestroy()", 1)[1].split(
            "public sealed class AarisGlooTouchButton", 1)[0]
        self.assertIn("foreach (var site in medicalSites)", teardown)
        self.assertIn("while (medicalVisualPool.Count > 0)", teardown)
        self.assertIn("while (helmetVisualPool.Count > 0)", teardown)
        self.assertIn("if (medcasePaint != null) Destroy(medcasePaint);", teardown)


if __name__ == "__main__":
    unittest.main(verbosity=2)
