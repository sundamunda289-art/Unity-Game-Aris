#!/usr/bin/env python3
"""Bounded mobile Humanoid rig reuse contracts; not a device FPS benchmark."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SRC = (ROOT / "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
DIRECTOR, RIVAL = SRC.split("internal sealed class AarisOfflineRival", 1)


class RivalRigPoolBudget(unittest.TestCase):
    def test_pool_is_bounded_and_low_memory_is_non_pooled(self):
        for token in (
            "new GameObject[2]",
            "memoryMB >= 6144 ? 2 : memoryMB >= 3072 ? 1 : 0",
            "retiredRigPoolingDisabledByMemoryPressure",
            "Application.lowMemory += ClearRetiredRivalRigPool;",
            "Application.lowMemory -= ClearRetiredRivalRigPool;",
            "ClearRetiredRivalRigPool();",
        ):
            self.assertIn(token, DIRECTOR)

    def test_only_passive_3d_rigs_are_recycled(self):
        method = DIRECTOR.split("internal bool RecycleRetiredRivalRig(", 1)[1].split(
            "private void ClearRetiredRivalRigPool()", 1)[0]
        for token in ("SkinnedMeshRenderer", "MonoBehaviour", "Rigidbody",
                      "Collider", "rig.SetActive(false);",
                      "retiredRivalRigPool[retiredRivalRigCount++] = rig;"):
            self.assertIn(token, method)

    def test_reentry_keeps_authored_scale_and_renderer_state(self):
        build = RIVAL.split("private void BuildVisual()", 1)[1].split(
            "private void RetireHiddenRig(", 1)[0]
        retire = RIVAL.split("private void RetireHiddenRig(", 1)[1].split(
            "// Built only once per domain", 1)[0]
        self.assertIn("owner.TakeRetiredRivalRig(transform);", build)
        self.assertIn("if (!reusedRig) visual = Instantiate(prefab, transform, false);", build)
        self.assertIn("bool boneFitted = reusedRig || TryFitHumanoidModel(", build)
        self.assertIn("if (reusedRig && animator.enabled) animator.Rebind();", build)
        self.assertIn("renderer.forceRenderingOff =", retire)
        self.assertIn("authoredRendererForcedOff[i];", retire)
        self.assertIn("detailedAnimator.enabled = authoredAnimatorEnabled;", retire)
        self.assertIn("owner.RecycleRetiredRivalRig(retired)", retire)
        self.assertIn("Destroy(retired);", retire)
        self.assertLess(retire.index("renderer.forceRenderingOff ="),
                        retire.index("owner.RecycleRetiredRivalRig(retired)"))

    def test_simulation_and_attack_rules_remain_intact(self):
        for token in (
            "private const int RivalCount = 100;",
            "private const float FarSimulationInterval = 0.35f;",
            "return bot == attackerOne || bot == attackerTwo;",
            "TickDormantRivals(now, playerPosition);",
            "targetHealth.ApplyDamage(damage, gameObject);",
            "owner.IsVirtualShotBlocked(selfPosition, targetPosition)",
        ):
            self.assertIn(token, SRC)

if __name__ == "__main__":
    unittest.main()
