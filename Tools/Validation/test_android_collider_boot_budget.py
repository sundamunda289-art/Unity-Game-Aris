#!/usr/bin/env python3
"""Source-level startup regressions; not native Play Mode verification."""
import unittest
from pathlib import Path

CODE = (Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts"
        / "AarisNearbyColliders.cs").read_text(encoding="utf-8")

class AndroidColliderBudget(unittest.TestCase):
    def test_build_full_grid_before_gating(self):
        a = CODE[CODE.index("private void Awake()"):
                 CODE.index("private void BeginDeferredDeactivation()")]
        self.assertIn("entries.Add(i);", a)
        self.assertIn("streamed[i] = true;", a)
        self.assertNotIn("c.enabled = false;", a)
        self.assertLess(a.index("RefreshNow();"), a.index("BeginDeferredDeactivation();"))

    def test_bounded_gating_rechecks_live_player_position(self):
        task = CODE[CODE.index("private IEnumerator DeactivateDistantAcrossFrames()"):
                    CODE.index("private int Cell")]
        self.assertIn("DeferredColliderUpdatesPerFrame = 24", CODE)
        self.assertIn("if (++processed >= DeferredColliderUpdatesPerFrame)", task)
        self.assertIn("yield return null;", task)
        self.assertIn("Vector3 position = target.position;", task)
        self.assertIn("ColliderReleaseMargin;", task)
        self.assertLess(task.index("safeDistance * safeDistance"),
                        task.index("c.enabled = false;"))

    def test_disable_reenable_restores_then_restarts_budget(self):
        on = CODE[CODE.index("private void OnEnable()"):
                  CODE.index("private void OnDisable()")]
        off = CODE[CODE.index("private void OnDisable()"):]
        self.assertLess(on.index("RefreshNow();"), on.index("BeginDeferredDeactivation();"))
        self.assertIn("StopCoroutine(deferredDeactivation);", off)
        self.assertIn("obstacles[i].enabled = true;", off)

if __name__ == "__main__":
    unittest.main()
