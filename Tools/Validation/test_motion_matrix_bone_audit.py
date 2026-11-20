#!/usr/bin/env python3
"""Fixtures for reliable full 30-frame Unity locomotion bone evidence."""
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
from audit_motion_matrix_bones import STATES, evaluate


def fixture(knee_flare=False, missing_time=None):
    lines = ["REAL_UNITY_CAMERA_TEST"]
    for state_number, name in enumerate(STATES):
        for frame in range(3):
            lines.append(
                f"FRAME {name} {frame} stance=Standing viewDegrees={90*frame} "
                "air=False pos=(0.000, 0.000, 0.000)"
            )
            if (name, frame) != missing_time:
                time = 1 + 2*state_number + frame*0.2
                lines.append(
                    f"MOTION_TIMESTAMP stage={name} frame={frame} "
                    f"unscaledSeconds={time:.3f}"
                )
            x_left = -0.15
            if knee_flare and name == "06_CrouchWalking" and frame == 2:
                x_left = -0.53
            coordinates = {
                "LeftUpperLeg": (-0.15, 1.10, 0.00),
                "RightUpperLeg": (0.15, 1.10, 0.00),
                "LeftLowerLeg": (x_left, 0.55, 0.06),
                "RightLowerLeg": (0.15, 0.55, 0.06),
                "LeftFoot": (-0.16, 0.10, 0.02),
                "RightFoot": (0.16, 0.10, 0.02),
            }
            for bone, (x, y, z) in coordinates.items():
                lines.append(
                    f"BONE stance=Standing id={bone} "
                    f"pos=({x:.3f}, {y:.3f}, {z:.3f}) "
                    f"heightAboveTerrain={y:.3f}"
                )
    lines.append("SUCCESS_30_MOTION_MATRIX_FRAMES")
    return "\n".join(lines)


class MotionMatrixBoneAuditTests(unittest.TestCase):
    def test_all_30_samples_are_measured(self):
        result = evaluate(fixture())
        self.assertEqual(result["status"], "PASS_TELEMETRY_COMPLETE")
        self.assertEqual(result["sample_count"], 30)
        self.assertEqual(len(result["states"]), 10)
        self.assertEqual(len(result["review_flags"]), 0)
        self.assertEqual(
            result["states"]["06_CrouchWalking"][2]["ankle_spread_m"], 0.32
        )

    def test_suspicious_back_view_knee_flagged_without_faking_failure(self):
        result = evaluate(fixture(knee_flare=True))
        self.assertEqual(result["status"], "PASS_TELEMETRY_COMPLETE")
        self.assertEqual(len(result["review_flags"]), 1)
        self.assertEqual(result["review_flags"][0]["state"], "06_CrouchWalking")
        self.assertEqual(result["review_flags"][0]["frame"], 2)
        self.assertAlmostEqual(
            result["states"]["06_CrouchWalking"][2]["knee_lateral_m"][0],
            -0.38,
        )

    def test_duplicate_game_timestamp_rejected(self):
        duplicate = fixture().replace(
            "unscaledSeconds=1.200", "unscaledSeconds=1.000", 1
        )
        with self.assertRaisesRegex(ValueError, "Missing/non-monotonic"):
            evaluate(duplicate)

    def test_missing_middle_bone_timestamp_rejected(self):
        with self.assertRaisesRegex(ValueError, "Missing/non-monotonic"):
            evaluate(fixture(missing_time=("06_CrouchWalking", 1)))

    def test_false_success_marker_rejected(self):
        with self.assertRaisesRegex(ValueError, "success marker"):
            evaluate(fixture().replace("SUCCESS_30_MOTION_MATRIX_FRAMES", ""))

    def test_editor_transition_gate_fails_grounded_knee_flare(self):
        editor = (ROOT / "Assets/AarisWorld/Editor/AarisPoseFrames.cs").read_text()
        start = editor.index("static void LogTransitionContact(")
        end = editor.index("static void Finish(string reason)", start)
        transition = editor[start:end]
        self.assertIn("TRANSITION_KNEE_SIDEWAYS_OVER_30CM", transition)
        self.assertIn("transitioning && !p.IsInAir", transition)
        self.assertIn("Mathf.Max(Mathf.Abs(lSide),Mathf.Abs(rSide)) > 0.30f", transition)

    def test_editor_contract_is_only_in_motion_matrix_mode(self):
        editor = (ROOT / "Assets/AarisWorld/Editor/AarisPoseFrames.cs").read_text()
        self.assertIn("if(motionMatrix)", editor)
        self.assertIn("MOTION_TIMESTAMP stage=", editor)
        self.assertIn("static int lastMotionCaptureFrame = -1;", editor)
        self.assertIn("lastMotionCaptureFrame = -1;", editor)
        self.assertIn("Time.frameCount != lastMotionCaptureFrame", editor)
        self.assertIn("lastMotionCaptureFrame = Time.frameCount;", editor)
        self.assertIn("Time.unscaledTimeAsDouble.ToString(\"F6\"", editor)
        self.assertIn(
            "if(motionMatrix || count==0 || (orbitStance && count==needed-1))",
            editor,
        )
        self.assertIn("LogPhysicalHeight(p,b);", editor)


if __name__ == "__main__":
    unittest.main()
