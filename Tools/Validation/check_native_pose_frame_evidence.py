#!/usr/bin/env python3
"""Audit *native Unity* 16-angle character captures and pose/bone telemetry.

This deliberately checks feet, hips, hands and knees against terrain instead
of treating the conservative SkinnedMeshRenderer.bounds.min as visible mesh
penetration. A negative renderer-bound gap is a warning, NOT a clipping proof.

Usage:
    python3 -B Tools/Validation/check_native_pose_frame_evidence.py \
      /tmp/aaris-codespaces/motion-frames

Only runs on existing native Unity evidence; never generates synthetic images.
"""
import argparse
import json
import math
import re
from pathlib import Path


FRAME_COUNTS = {
    "01_Standing": 4,
    "02_Crouch": 4,
    "03_Prone": 4,
    "04_Walk": 1,
    "05_Sprint": 1,
    "06_Jump": 2,
}
EXPECTED_STANCES = {
    "01_Standing": "Standing",
    "02_Crouch": "Crouching",
    "03_Prone": "Prone",
    "04_Walk": "Standing",
    "05_Sprint": "Standing",
    "06_Jump": "Standing",
}
FRAME_RE = re.compile(
    r"^FRAME (\S+) (\d+) stance=(\S+) viewDegrees=(\d+) air=(True|False)\b"
)
BONE_RE = re.compile(
    r"^BONE stance=(\w+) id=(\w+) pos=\(([^)]+)\) "
    r"heightAboveTerrain=([-+]?\d+(?:\.\d+)?)"
)
SPREAD_RE = re.compile(
    r"^LOW_STANCE_SPREAD stance=(\w+) metres=([-+]?\d+(?:\.\d+)?)"
)
KNEE_RE = re.compile(
    r"^(CROUCH|PRONE)_KNEE_ALIGNMENT leftLateral=([-+]?\d+(?:\.\d+)?) "
    r"rightLateral=([-+]?\d+(?:\.\d+)?) "
    r"kneeHeightDelta=([-+]?\d+(?:\.\d+)?)"
)
MEASURE_RE = re.compile(
    r"^MEASURE stance=(\w+).*?footGap=([-+]?\d+(?:\.\d+)?)"
    r".*?meshGap=([-+]?\d+(?:\.\d+)?)"
)


class PoseEvidenceFailure(AssertionError):
    pass


def require(condition, detail):
    if not condition:
        raise PoseEvidenceFailure(detail)


def read_bones(lines):
    poses = {}
    for line in lines:
        found = BONE_RE.match(line)
        if not found:
            continue
        stance, name, values, clearance = found.groups()
        if stance not in ("Crouching", "Prone"):
            continue
        xyz = tuple(float(v.strip()) for v in values.split(","))
        require(len(xyz) == 3 and all(math.isfinite(v) for v in xyz),
                "Invalid bone position " + stance + "/" + name)
        height = float(clearance)
        require(math.isfinite(height), "Invalid bone clearance " + stance + "/" + name)
        # Real native orbit reports include entry and fully settled samples.
        # The final record for each bone is the settled (frame 3) posture,
        # not the first transition frame (frame 0). Synthetic fixtures have
        # one record per bone and remain compatible.
        poses.setdefault(stance, {})[name] = (xyz, height)
    return poses


def audit_capture(directory):
    directory = Path(directory)
    require(directory.is_dir(), "Capture directory missing: " + str(directory))
    report_path = directory / "report.txt"
    require(report_path.is_file(), "Native Unity pose report missing")
    lines = report_path.read_text(encoding="utf-8").splitlines()
    require("REAL_UNITY_CAMERA_TEST" in lines, "Not a native Unity camera report")
    require("SUCCESS_16_ACTOR_MULTIANGLE_PORTRAITS" in lines,
            "Native 16-angle Play Mode success marker missing")
    require(any(s.startswith("SETTLED_PRONE_CONTACT ") for s in lines),
            "Missing settled prone contact sample")

    expected = {
        "{}_f{}.jpg".format(name, frame)
        for name, count in FRAME_COUNTS.items() for frame in range(count)
    }
    photos = {p.name for p in directory.glob("*.jpg")}
    require(photos == expected,
            "Missing/extra native photos: missing={} extra={}".format(
                sorted(expected - photos), sorted(photos - expected)))
    for name in sorted(expected):
        image_bytes = (directory / name).read_bytes()
        require(len(image_bytes) >= 4000 and
                image_bytes.startswith(b"\xff\xd8\xff") and
                image_bytes.endswith(b"\xff\xd9"),
                "Native JPEG missing/truncated: " + name)

    seen = {}
    for line in lines:
        found = FRAME_RE.match(line)
        if not found:
            continue
        name, index, stance, degrees, air = found.groups()
        key = (name, int(index))
        require(name in FRAME_COUNTS and int(index) < FRAME_COUNTS[name],
                "Unexpected native frame record: " + line)
        require(key not in seen, "Duplicate native frame record: " + str(key))
        require(stance == EXPECTED_STANCES[name],
                "Wrong stance for native frame: " + line)
        expected_degrees = int(index) * 90 if name in (
            "01_Standing", "02_Crouch", "03_Prone") else 0
        require(int(degrees) == expected_degrees,
                "Incorrect orbit direction for native frame: " + line)
        seen[key] = air
    expected_keys = {
        (name, frame)
        for name, count in FRAME_COUNTS.items() for frame in range(count)
    }
    require(set(seen) == expected_keys, "Missing native frame telemetry")

    bones = read_bones(lines)
    spreads = {}
    knees = {}
    measures = {}
    for line in lines:
        found = SPREAD_RE.match(line)
        if found:
            spreads.setdefault(found.group(1), float(found.group(2)))
        found = KNEE_RE.match(line)
        if found:
            knees.setdefault("Crouching" if found.group(1) == "CROUCH" else "Prone",
                             tuple(float(v) for v in found.groups()[1:]))
        found = MEASURE_RE.match(line)
        if found:
            measures.setdefault(found.group(1),
                                (float(found.group(2)), float(found.group(3))))

    stance_report = {}
    warnings = []
    for stance in ("Crouching", "Prone"):
        require(stance in bones and stance in spreads and stance in knees
                and stance in measures, "Missing low-stance native telemetry: " + stance)
        required = ("Hips", "Head", "LeftFoot", "RightFoot",
                    "LeftHand", "RightHand", "LeftUpperLeg", "RightUpperLeg",
                    "LeftLowerLeg", "RightLowerLeg")
        require(all(b in bones[stance] for b in required),
                "Missing bone(s) for " + stance)
        pose = bones[stance]
        left_foot, right_foot = pose["LeftFoot"][1], pose["RightFoot"][1]
        require(all(-0.06 <= f <= 0.26 for f in (left_foot, right_foot)),
                "Feet floating or buried in " + stance)
        hip = pose["Hips"][1]
        hip_limits = (0.20, 0.95) if stance == "Crouching" else (0.04, 0.55)
        require(hip_limits[0] <= hip <= hip_limits[1],
                "Pelvis height invalid in " + stance)
        require(all(pose[b][1] >= -0.06 for b in required),
                "Humanoid bone below terrain in " + stance)
        hand_gap = math.dist(pose["LeftHand"][0], pose["RightHand"][0])
        require(0.11 <= hand_gap <= 0.90, "Hands detached/overlapping in " + stance)
        spread = spreads[stance]
        require(0.10 <= spread <= 0.62, "Leg/foot spread misaligned in " + stance)
        left_knee, right_knee, knee_delta = knees[stance]
        require(max(abs(left_knee), abs(right_knee)) <= 0.25
                and knee_delta <= 0.12,
                "Knee side offset or height mismatch in " + stance)
        foot_gap, conservative_mesh_gap = measures[stance]
        require(-0.06 <= foot_gap <= 0.16,
                "Controller foot placement invalid in " + stance)
        if conservative_mesh_gap < -0.25:
            warnings.append(
                "{}: SkinnedMeshRenderer.bounds extends {:.3f}m below terrain; "
                "conservative bounds alone do not prove visible mesh clipping."
                .format(stance, abs(conservative_mesh_gap)))
        stance_report[stance] = {
            "feet_clearance_metres": [round(left_foot, 3), round(right_foot, 3)],
            "pelvis_clearance_metres": round(hip, 3),
            "hands_separation_metres": round(hand_gap, 3),
            "foot_spread_metres": round(spread, 3),
            "knee_lateral_metres": [round(left_knee, 3), round(right_knee, 3)],
            "knee_height_difference_metres": round(knee_delta, 3),
        }
    return {
        "status": "PASS",
        "native_frame_count": len(expected),
        "orbit_angles": [0, 90, 180, 270],
        "stances": stance_report,
        "bounds_not_clipping_proof": warnings,
        "limitations": "JPEG envelope and bone telemetry only; no vertex-level clipping, grip-contact or Android FPS certification.",
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path, help="Native Unity motion-frames directory")
    args = parser.parse_args()
    try:
        result = audit_capture(args.capture)
    except (PoseEvidenceFailure, OSError, ValueError) as exc:
        print(json.dumps({"status": "FAIL", "reason": str(exc)}))
        raise SystemExit(1)
    print(json.dumps(result, sort_keys=True, indent=2))


if __name__ == "__main__":
    main()
