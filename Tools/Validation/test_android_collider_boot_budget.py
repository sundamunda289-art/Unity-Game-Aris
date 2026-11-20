#!/usr/bin/env python3
"""Source-level startup regressions; not native Play Mode verification."""
import unittest
from pathlib import Path

CODE = (Path(__file__).resolve().parents[2] / "Assets/AarisWorld/Scripts"
        / "AarisNearbyColliders.cs").read_text(encoding="utf-8")

class AndroidColliderBudget(unittest.TestCase):
    def test_complete_grid_required_before_any_gating(self):
        awake = CODE[CODE.index("private void Awake()"):
                     CODE.index("private void BeginDeferredGridBuild()")]
        index = CODE[CODE.index("private void IndexCollider(int i)"):
                     CODE.index("private void FinishIndexBuild()")]
        finish = CODE[CODE.index("private void FinishIndexBuild()"):
                      CODE.index("private void BeginDeferredDeactivation()")]
        self.assertIn("BeginDeferredGridBuild();", awake)
        self.assertIn("IndexCollider(i);", awake)
        self.assertIn("entries.Add(i);", index)
        self.assertIn("streamed[i] = true;", index)
        self.assertNotIn("c.enabled = false;", awake + index)
        self.assertLess(finish.index("indexReady = true;"),
                        finish.index("RefreshNow();"))
        self.assertLess(finish.index("RefreshNow();"),
                        finish.index("BeginDeferredDeactivation();"))

    def test_android_index_budget_has_safe_resume_cursor(self):
        routine = CODE[CODE.index("private IEnumerator BuildGridAcrossFrames()"):
                       CODE.index("private void IndexCollider(int i)")]
        self.assertIn("AndroidIndexUpdatesPerFrame = 96", CODE)
        self.assertIn("yield return null;", routine)
        self.assertIn("while (indexCursor < obstacles.Length)", routine)
        self.assertIn("IndexCollider(indexCursor);", routine)
        self.assertIn("indexCursor++;", routine)
        self.assertLess(routine.index("indexCursor++;"),
                        routine.index("if (processed >= AndroidIndexUpdatesPerFrame ||"))
        self.assertLess(routine.index("yield return null;"),
                        routine.index("IndexCollider(indexCursor);"))
        self.assertIn("FinishIndexBuild();", routine)
        self.assertIn("if (!indexReady) return;", CODE)
        self.assertIn("if (!indexReady) return false;", CODE)

    def test_bounded_gating_rechecks_live_player_position(self):
        task = CODE[CODE.index("private IEnumerator DeactivateDistantAcrossFrames()"):
                    CODE.index("private int Cell")]
        self.assertIn("DeferredColliderUpdatesPerFrame = 24", CODE)
        self.assertIn("if (processed >= DeferredColliderUpdatesPerFrame ||", task)
        self.assertIn("yield return null;", task)
        self.assertIn("Vector3 framePosition = target != null ? target.position : default(Vector3);", task)
        self.assertEqual(task.count("target.position"), 2)
        self.assertIn("ColliderReleaseMargin;", task)
        self.assertLess(task.index("safeDistance * safeDistance"),
                        task.index("c.enabled = false;"))

    def test_disable_reenable_restores_then_restarts_budget(self):
        on = CODE[CODE.index("private void OnEnable()"):
                  CODE.index("private void OnDisable()")]
        off = CODE[CODE.index("private void OnDisable()"):]
        self.assertIn("if (!indexReady)", on)
        self.assertIn("BeginDeferredGridBuild();", on)
        self.assertLess(on.index("RefreshNow();"), on.index("BeginDeferredDeactivation();"))
        self.assertIn("StopCoroutine(deferredIndexBuild);", off)
        self.assertIn("StopCoroutine(deferredDeactivation);", off)
        self.assertIn("obstacles[i].enabled = true;", off)

if __name__ == "__main__":
    unittest.main()
