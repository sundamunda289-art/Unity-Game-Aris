#!/usr/bin/env python3
"""Audit 30 real Unity Editor locomotion frames from measured humanoid bones.

The three camera angles are captured at DIFFERENT time samples. This report
detects outlying knee spread for review; it does not infer foot sliding,
mesh clipping, or mobile FPS from a still image.
"""
import argparse
import json
import math
import re
from pathlib import Path

STATES = (
    "01_Standing", "02_Walking", "03_Running", "04_Sprinting",
    "05_Crouching", "06_CrouchWalking", "07_Prone", "08_Crawling",
    "09_GettingUp", "10_Jumping",
)
REQUIRED_BONES = (
    "LeftUpperLeg", "RightUpperLeg", "LeftLowerLeg", "RightLowerLeg",
    "LeftFoot", "RightFoot",
)
FRAME = re.compile(r"^FRAME (\S+) ([012]) stance=\w+ ")
TIME = re.compile(
    r"^MOTION_TIMESTAMP stage=(\S+) frame=([012]) "
    r"unscaledSeconds=([0-9.]+)$"
)
BONE = re.compile(
    r"^BONE stance=\w+ id=(\w+) "
    r"pos=\(([-0-9.]+), ([-0-9.]+), ([-0-9.]+)\) "
    r"heightAboveTerrain=([-0-9.]+)$"
)


def evaluate(report: str):
    if "SUCCESS_30_MOTION_MATRIX_FRAMES" not in report.splitlines():
        raise ValueError("Missing native Unity 30-frame success marker")
    samples = {}
    current = None
    for line in report.splitlines():
        frame = FRAME.match(line)
        if frame:
            current = (frame.group(1), int(frame.group(2)))
            if current in samples:
                raise ValueError(f"Duplicate native frame: {current}")
            samples[current] = {"bones": {}, "seconds": None}
            continue
        stamp = TIME.match(line)
        if stamp:
            key = (stamp.group(1), int(stamp.group(2)))
            if key not in samples:
                raise ValueError(f"Orphan frame timestamp: {key}")
            samples[key]["seconds"] = float(stamp.group(3))
            continue
        bone = BONE.match(line)
        if bone and current is not None:
            name = bone.group(1)
            position = tuple(float(bone.group(i)) for i in (2, 3, 4))
            samples[current]["bones"][name] = (
                position, float(bone.group(5))
            )

    expected = {(name, i) for name in STATES for i in range(3)}
    if set(samples) != expected:
        missing = sorted(expected - set(samples))
        extra = sorted(set(samples) - expected)
        raise ValueError(f"Expected 30 frames: missing={missing}, extra={extra}")

    summary = {}
    review_flags = []
    last_time = -float("inf")
    for state in STATES:
        rows = []
        for frame in range(3):
            key = state, frame
            obj = samples[key]
            bones = obj["bones"]
            missing = set(REQUIRED_BONES) - bones.keys()
            if missing:
                raise ValueError(f"Missing humanoid bones at {key}: {missing}")
            t = obj["seconds"]
            if t is None or not math.isfinite(t) or t <= last_time:
                raise ValueError(f"Missing/non-monotonic game time at {key}")
            last_time = t

            def xy(name):
                pos = bones[name][0]
                return pos[0], pos[2]

            def delta(a, b):
                aa, bb = xy(a), xy(b)
                return aa[0] - bb[0], aa[1] - bb[1]

            right = delta("RightUpperLeg", "LeftUpperLeg")
            hip_width = math.hypot(*right)
            if hip_width <= 0.05:
                raise ValueError(f"Invalid left/right hip axis at {key}")
            right = right[0] / hip_width, right[1] / hip_width
            left_knee = delta("LeftLowerLeg", "LeftUpperLeg")
            right_knee = delta("RightLowerLeg", "RightUpperLeg")
            left_lateral = left_knee[0] * right[0] + left_knee[1] * right[1]
            right_lateral = right_knee[0] * right[0] + right_knee[1] * right[1]
            spread = math.hypot(*delta("LeftFoot", "RightFoot"))
            row = {
                "frame": frame, "time_s": round(t, 3),
                "knee_lateral_m": [round(left_lateral, 3),
                                   round(right_lateral, 3)],
                "ankle_spread_m": round(spread, 3),
                "ankle_height_vs_actor_terrain_m": [
                    round(bones[name][1], 3)
                    for name in ("LeftFoot", "RightFoot")
                ],
            }
            rows.append(row)
            if max(abs(left_lateral), abs(right_lateral)) > 0.30:
                review_flags.append({
                    "state": state, "frame": frame,
                    "reason": "Knee lateral offset >0.30m (needs visual review)",
                    "knee_lateral_m": row["knee_lateral_m"],
                })
        summary[state] = rows
    return {
        "status": "PASS_TELEMETRY_COMPLETE",
        "sample_count": 30,
        "camera_angles_are_sequential": True,
        "states": summary,
        "review_flags": review_flags,
        "limitations": (
            "Terrain height is sampled at actor position, not each shoe. "
            "Still frames cannot prove stance-foot sliding or continuous "
            "interpolation. Android performance/thermal not evaluated."
        ),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("report", type=Path)
    args = parser.parse_args()
    result = evaluate(args.report.read_text(encoding="utf-8"))
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
