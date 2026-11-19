#!/usr/bin/env python3
"""Critical-memory grenade-pool cleanup regression contracts.

Structural source checks, not Unity or device PSS proof.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts"
FRAG = (ROOT / "AarisOfflineFragGrenade.cs").read_text("utf-8")
RUNTIME = (ROOT / "AarisMobileRuntime.cs").read_text("utf-8")


class CriticalMemoryFragPool(unittest.TestCase):
    def test_releases_only_inactive_cached_hierarchies(self):
        cleanup = FRAG.split("internal static int ReleaseInactivePooledGrenades()", 1)[1].split(
            "internal static IEnumerator PrewarmGrenadeAssets()", 1
        )[0]
        self.assertIn("poolingAllowedAt = Time.unscaledTime + 60f;", cleanup)
        self.assertIn("while (idleGrenades.Count > 0)", cleanup)
        self.assertIn("idleGrenades.Pop()", cleanup)
        self.assertIn("inactive == null || inactive.gameObject.activeInHierarchy", cleanup)
        self.assertIn("UnityEngine.Object.Destroy(inactive.gameObject);", cleanup)
        self.assertIn("released++;", cleanup)
        self.assertIn("return released;", cleanup)
        self.assertNotIn("FindObjectsByType", cleanup)
        self.assertNotIn("Resources.UnloadUnusedAssets()", cleanup)
        self.assertNotIn("GC.Collect()", cleanup)
        self.assertNotIn("DestroyImmediate", cleanup)

    def test_critical_memory_does_not_reaccumulate_pool_immediately(self):
        detonation = FRAG.split("private void Detonate()", 1)[1].split(
            "private bool HasBlastLineOfSight(", 1
        )[0]
        self.assertIn("Time.unscaledTime >= poolingAllowedAt", detonation)
        self.assertIn("idleGrenades.Count < MaxPooledGrenades", detonation)
        self.assertLess(detonation.index("Time.unscaledTime >= poolingAllowedAt"),
                        detonation.index("idleGrenades.Push(this);"))
        self.assertIn("Destroy(gameObject);", detonation)
        self.assertIn("private const int MaxPooledGrenades = 6;", FRAG)
        self.assertIn("poolingAllowedAt = 0f;", FRAG)
        self.assertIn("RuntimeInitializeLoadType.SubsystemRegistration", FRAG)

    def test_android_low_memory_event_cleanup_precedes_unload(self):
        low = RUNTIME.split("if (lowMemoryPending)", 1)[1].split(
            'Record("low-memory");', 1
        )[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", low)
        self.assertIn("AarisOfflineFragGrenade.ReleaseInactivePooledGrenades();", low)
        self.assertIn("Resources.UnloadUnusedAssets();", low)
        self.assertLess(low.index("ReleaseInactivePooledGrenades();"),
                        low.index("Resources.UnloadUnusedAssets();"))
        self.assertIn("unloading == null || unloading.isDone", low)
        self.assertIn("unloadAfter = Time.unscaledTime + 30;", low)
        self.assertNotIn("WaitForCompletion", low)
        self.assertNotIn("GC.Collect()", low)

    def test_active_gameplay_and_physics_reuse_remain_untouched(self):
        throw = FRAG.split("public static AarisOfflineFragGrenade Throw(", 1)[1].split(
            "private static bool IsUrpFinish(", 1
        )[0]
        self.assertIn("reused.gameObject.SetActive(true);", throw)
        self.assertIn("reused.Launch(reused.body, velocity, thrower);", throw)
        self.assertIn('new GameObject("Aaris_FragGrenade");', throw)
        self.assertIn('Resources.Load<GameObject>("AarisCombat/FragGrenade")', throw)
        self.assertIn("Physics.OverlapSphereNonAlloc(blast, blastRadius,", FRAG)
        self.assertIn("AarisAudioDirector.GrenadeExplosion(transform.position);", FRAG)
        self.assertIn("body.isKinematic = true;", FRAG)
        self.assertIn("owner = null;", FRAG)


if __name__ == "__main__":
    unittest.main()
