#!/usr/bin/env python3
"""Synthetic fixtures test the auditor; production evidence comes from Unity."""
import importlib.util
import tempfile
import unittest
from pathlib import Path

SOURCE = Path(__file__).with_name("check_native_pose_frame_evidence.py")
spec = importlib.util.spec_from_file_location("native_pose_evidence", SOURCE)
audit_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit_module)

COUNTS = audit_module.FRAME_COUNTS
STANCES = audit_module.EXPECTED_STANCES


def report_lines():
    data = ["REAL_UNITY_CAMERA_TEST", "EDITOR_ONLY_FIXED_SURVEY_LOCATION"]
    for name, count in COUNTS.items():
        for i in range(count):
            direction = i * 90 if name in (
                "01_Standing", "02_Crouch", "03_Prone") else 0
            data.append(
                "FRAME {} {} stance={} viewDegrees={} air=False pos=(0, 0, 0)"
                .format(name, i, STANCES[name], direction))
    data.extend([
        "LOW_STANCE_SPREAD stance=Crouching metres=0.341",
        "LOW_STANCE_SPREAD stance=Prone metres=0.523",
        "CROUCH_KNEE_ALIGNMENT leftLateral=0.005 rightLateral=0.003 kneeHeightDelta=0.045",
        "PRONE_KNEE_ALIGNMENT leftLateral=-0.115 rightLateral=0.010 kneeHeightDelta=0.091",
        "MEASURE stance=Crouching footGap=0.035 meshMin=-3.149 meshMax=-1.213 meshGap=-0.491 skins=1",
        "MEASURE stance=Prone footGap=0.035 meshMin=-4.041 meshMax=-1.803 meshGap=-1.382 skins=1",
    ])
    common = {
        "LeftFoot": ((-0.2, 0, 0.0), 0.124),
        "RightFoot": ((0.2, 0, 0.0), 0.123),
        "Hips": ((0.0, 0, 0.0), 0.463),
        "Head": ((0.0, 0, 0.0), 1.109),
        "LeftHand": ((-0.2, 0, 0.0), 0.730),
        "RightHand": ((0.15, 0, 0.0), 0.720),
        "LeftUpperLeg": ((0, 0, 0), 0.395),
        "RightUpperLeg": ((0, 0, 0), 0.403),
        "LeftLowerLeg": ((0, 0, 0), 0.522),
        "RightLowerLeg": ((0, 0, 0), 0.476),
    }
    for stance in ("Crouching", "Prone"):
        for bone, (pos, height) in common.items():
            if stance == "Prone":
                height = {
                    "LeftFoot": 0.088, "RightFoot": 0.093,
                    "Hips": 0.185, "Head": 0.399,
                    "LeftHand": 0.275, "RightHand": 0.174,
                }.get(bone, 0.16)
            data.append("BONE stance={} id={} pos=({}, {}, {}) heightAboveTerrain={:.3f}".format(
                stance, bone, *pos, height))
    data.append("SUCCESS_16_ACTOR_MULTIANGLE_PORTRAITS")
    return data


class NativePoseEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="aaris_native_pose_fixture_")
        self.directory = Path(self.temp.name)
        self.lines = report_lines()
        for name, count in COUNTS.items():
            for i in range(count):
                (self.directory / "{}_f{}.jpg".format(name, i)).write_bytes(
                    b"\xff\xd8\xff" + b"\x01" * 4096 + b"\xff\xd9")
        self.write_report()

    def tearDown(self):
        self.temp.cleanup()

    def write_report(self):
        (self.directory / "report.txt").write_text(
            "\n".join(self.lines) + "\n", encoding="utf-8")

    def check_rejected(self, expected_fragment):
        self.write_report()
        with self.assertRaises(audit_module.PoseEvidenceFailure) as fail:
            audit_module.audit_capture(self.directory)
        self.assertIn(expected_fragment, str(fail.exception))

    def test_good_bones_override_misleading_conservative_mesh_bounds(self):
        result = audit_module.audit_capture(self.directory)
        self.assertEqual(result["native_frame_count"], 16)
        self.assertEqual(result["status"], "PASS")
        self.assertEqual(len(result["bounds_not_clipping_proof"]), 2)
        self.assertEqual(result["stances"]["Prone"]["feet_clearance_metres"],
                         [0.088, 0.093])

    def test_missing_jpeg_is_rejected(self):
        (self.directory / "03_Prone_f3.jpg").unlink()
        with self.assertRaises(audit_module.PoseEvidenceFailure):
            audit_module.audit_capture(self.directory)

    def test_truncated_jpeg_is_rejected(self):
        (self.directory / "02_Crouch_f1.jpg").write_bytes(b"\xff\xd8\xff\x00\xff\xd9")
        with self.assertRaises(audit_module.PoseEvidenceFailure):
            audit_module.audit_capture(self.directory)

    def test_wrong_orbit_angle_is_rejected(self):
        self.lines = [l.replace("03_Prone 1 stance=Prone viewDegrees=90",
                                "03_Prone 1 stance=Prone viewDegrees=180")
                      for l in self.lines]
        self.check_rejected("Incorrect orbit")

    def test_missing_frame_telemetry_is_rejected(self):
        self.lines = [l for l in self.lines if not l.startswith("FRAME 05_Sprint")]
        self.check_rejected("Missing native frame telemetry")

    def test_buried_feet_are_rejected(self):
        self.lines = [l.replace("stance=Prone id=LeftFoot pos=(-0.2, 0, 0.0) heightAboveTerrain=0.088",
                                "stance=Prone id=LeftFoot pos=(-0.2, 0, 0.0) heightAboveTerrain=-0.300")
                      for l in self.lines]
        self.check_rejected("Feet floating or buried")

    def test_knee_splay_is_rejected(self):
        self.lines = [l.replace("PRONE_KNEE_ALIGNMENT leftLateral=-0.115",
                                "PRONE_KNEE_ALIGNMENT leftLateral=-0.420")
                      for l in self.lines]
        self.check_rejected("Knee side offset")

    def test_bad_weapon_support_hand_span_is_rejected(self):
        self.lines = [l.replace(
            "stance=Prone id=RightHand pos=(0.15, 0, 0.0)",
            "stance=Prone id=RightHand pos=(1.5, 0, 0.0)")
            for l in self.lines]
        self.check_rejected("Hands detached/overlapping")

    def test_missing_real_capture_success_marker_is_rejected(self):
        self.lines.remove("SUCCESS_16_ACTOR_MULTIANGLE_PORTRAITS")
        self.check_rejected("success marker missing")


if __name__ == "__main__":
    unittest.main()
