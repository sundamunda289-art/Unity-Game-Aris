#!/usr/bin/env python3
"""Synthetic parser fixtures only; real bone evidence requires Unity Play Mode."""
import importlib.util
import unittest
from pathlib import Path

SRC = Path(__file__).with_name("audit_motion_matrix_bones.py")
spec = importlib.util.spec_from_file_location("motion_bones", SRC)
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


def synthetic_report(outlier_stage=None, airborne_jump=False, omit_air=False):
    lines = []
    clock = 0.0
    for stage in mod.STATES:
        for frame in range(3):
            clock += 0.125
            is_air = airborne_jump and stage == "10_Jumping"
            right_knee_x = 0.59 if stage == outlier_stage and frame == 1 else 0.20
            if is_air and frame == 1:
                right_knee_x = 0.65
            air_field = "" if omit_air else f"air={is_air} "
            lines.append(
                f"FRAME {stage} {frame} stance=Standing viewDegrees=0 "
                f"{air_field}pos=(0, 0, 0)"
            )
            lines.append(
                f"MOTION_TIMESTAMP stage={stage} frame={frame} "
                f"unscaledSeconds={clock:.6f}"
            )
            locations = {
                "LeftUpperLeg": (-0.18, 0.7, 0.0),
                "RightUpperLeg": (0.18, 0.7, 0.0),
                "LeftLowerLeg": (-0.19, 0.43, 0.1),
                "RightLowerLeg": (right_knee_x, 0.43, 0.1),
                "LeftFoot": (-0.17, 0.1, 0.0),
                "RightFoot": (0.17, 0.1, 0.0),
            }
            for bone, (x, y, z) in locations.items():
                lines.append(
                    f"BONE stance=Standing id={bone} pos=({x}, {y}, {z}) "
                    f"heightAboveTerrain={y:.3f}"
                )
    lines.append("SUCCESS_30_MOTION_MATRIX_FRAMES")
    return "\n".join(lines) + "\n"


class StrictGroundedLowStanceTests(unittest.TestCase):
    def test_neutral_30_frames_pass(self):
        result = mod.evaluate(synthetic_report(), strict_grounded_low_stance=True)
        self.assertEqual(result["sample_count"], 30)
        self.assertEqual(result["grounded_low_stance_blocking_flags"], [])

    def test_unsafe_crouch_walk_requires_rejection(self):
        report = synthetic_report(outlier_stage="06_CrouchWalking")
        telemetry = mod.evaluate(report)
        self.assertEqual(len(telemetry["grounded_low_stance_blocking_flags"]), 1)
        with self.assertRaisesRegex(ValueError, "06_CrouchWalking frame 1"):
            mod.evaluate(report, strict_grounded_low_stance=True)

    def test_other_grounded_low_stances_are_covered(self):
        for stage in ("05_Crouching", "07_Prone", "08_Crawling",
                      "09_GettingUp"):
            with self.subTest(stage=stage):
                with self.assertRaisesRegex(ValueError, "Grounded low-stance"):
                    mod.evaluate(synthetic_report(outlier_stage=stage),
                                 strict_grounded_low_stance=True)

    def test_authored_airborne_jump_is_not_a_low_stance_failure(self):
        result = mod.evaluate(synthetic_report(airborne_jump=True),
                              strict_grounded_low_stance=True)
        self.assertEqual(result["grounded_low_stance_blocking_flags"], [])
        self.assertEqual(len(result["review_flags"]), 1)

    def test_missing_air_state_fails_closed(self):
        with self.assertRaisesRegex(ValueError, "missing air flag"):
            mod.evaluate(synthetic_report(omit_air=True))

    def test_duplicate_clock_fails_closed(self):
        report = synthetic_report()
        report = report.replace("unscaledSeconds=0.250000",
                                "unscaledSeconds=0.125000")
        with self.assertRaisesRegex(ValueError, "non-monotonic game time"):
            mod.evaluate(report)


if __name__ == "__main__":
    unittest.main()
