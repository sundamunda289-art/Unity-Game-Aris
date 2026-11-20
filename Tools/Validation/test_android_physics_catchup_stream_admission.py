#!/usr/bin/env python3
"""Regression contracts for optional-work isolation during Android physics catch-up.

These tests verify checked-in source and a bounded policy model. The separate
StreamingAdmissionTests.cs exercises the actual gate using compiled Unity stubs.
Neither replaces native Unity/Android profiling.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT_DIR = ROOT / "Assets" / "AarisWorld" / "Scripts"
RUNTIME = (SCRIPT_DIR / "AarisMobileRuntime.cs").read_text(encoding="utf-8")
GATE = (SCRIPT_DIR / "AarisMobileStreamingFrameGate.cs").read_text(encoding="utf-8")
FIXTURE = (ROOT / "Tools" / "Validation" / "StreamingAdmissionTests.cs").read_text(encoding="utf-8")


def catchup_fence(steps):
    """Reference model for consecutive 3+ step frames; list is one frame per entry."""
    streak = 0
    admission = []
    for count in steps:
        if count < 3:
            streak = 0
            admission.append(True)
        elif streak >= 2:
            streak = 0
            admission.append(True)
        else:
            streak += 1
            admission.append(False)
    return admission


class PhysicsCatchupAdmissionRegression(unittest.TestCase):
    def test_runtime_calls_gate_before_optional_streamers_and_sampling(self):
        self.assertIn("DefaultExecutionOrder(-300)", RUNTIME)
        update = RUNTIME.split("private void Update()", 1)[1].split(
            "float dt = Time.unscaledDeltaTime;", 1
        )[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", update)
        self.assertLess(
            update.index("int physicsTicks = physicsStepsBeforeRender;"),
            update.index("AarisMobileStreamingFrameGate.ReservePhysicsCatchupFrame(physicsTicks);"),
        )
        self.assertLess(
            update.index("ReservePhysicsCatchupFrame(physicsTicks);"),
            update.index("SampleMobileFrameTiming();"),
        )

    def test_gate_is_android_only_and_leaves_essential_work_untouched(self):
        method = GATE.split(
            "internal static void ReservePhysicsCatchupFrame(int fixedSteps)", 1
        )[1].split("internal static void ReservePhysicsStreamingFrame()", 1)[0]
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", method)
        self.assertIn("if (!IsConstrainedDevice()) return;", method)
        self.assertIn("if (lastPhysicsCatchupFrame == frame) return;", method)
        self.assertIn("fixedSteps < PhysicsCatchupStepThreshold", method)
        self.assertIn("consecutivePhysicsCatchupDeferrals = 0;", method)
        self.assertIn("claimedFrame = frame;", method)
        self.assertNotIn("Time.fixedDeltaTime =", method)
        self.assertNotIn("Time.maximumDeltaTime =", method)
        self.assertNotIn("Instantiate(", method)
        self.assertNotIn("Destroy(", method)
        self.assertIn("private const int PhysicsCatchupStepThreshold = 3;", GATE)
        self.assertIn("private const int MaximumPhysicsCatchupDeferrals = 2;", GATE)

    def test_reset_and_actual_compiled_fixture_coverage(self):
        reset = GATE.split("private static void Reset()", 1)[1].split(
            "internal static void ReservePhysicsStreamingFrame()", 1
        )[0]
        self.assertIn("lastPhysicsCatchupFrame = -1;", reset)
        self.assertIn("consecutivePhysicsCatchupDeferrals = 0;", reset)
        self.assertIn("PhysicsCatchupDeferredFrames = 0;", reset)
        self.assertIn("private static void PhysicsCatchupAdmission()", FIXTURE)
        self.assertIn("PhysicsCatchupAdmission();", FIXTURE)
        self.assertIn("Sustained catchup starved streaming on its third frame", FIXTURE)

    def test_reference_policy_limits_starvation_and_ignores_normal_cadence(self):
        self.assertEqual(catchup_fence([0, 1, 2, 0]), [True] * 4)
        self.assertEqual(catchup_fence([3] * 9), [False, False, True] * 3)
        self.assertEqual(
            catchup_fence([3, 4, 2, 5, 3, 6, 1, 3]),
            [False, False, True, False, False, True, True, False],
        )


if __name__ == "__main__":
    unittest.main(verbosity=2)
