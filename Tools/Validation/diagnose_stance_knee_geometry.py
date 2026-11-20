#!/usr/bin/env python3
"""Analyze native Unity transition leg telemetry with exact two-bone geometry.

The knee is constrained to a circle perpendicular to hip->ankle. This tool
computes the minimum possible absolute lateral knee offset given the logged
bone lengths and ankle displacement. It deliberately does NOT infer 3D floor
clearance, blend quality, foot sliding, or Android performance.

Usage: python Tools/Validation/diagnose_stance_knee_geometry.py native_pose.log
       python Tools/Validation/diagnose_stance_knee_geometry.py --self-test
"""
from __future__ import annotations

import argparse
import json
import math
import re
from pathlib import Path

CONTACT = re.compile(r"TRANSITION_CONTACT\s+stance=(\w+)\s+frame=(\d+).*?\bblending=(\w+)")
REACH = re.compile(r"TRANSITION_LEG_REACH\s+side=([LR])\s+(.+)")
FIELDS = ("upper", "lower", "hipAnkle", "reachRatio", "minimumReach",
          "ankleSide", "kneeSide", "kneeAboveAnkle")
VALUE = re.compile(r"([A-Za-z]+)=([-+]?\d+(?:\.\d+)?)")


def analyze_leg(metrics: dict[str, float], max_knee_side_m: float = 0.30) -> dict:
    """Closest possible lateral knee on the exact two-bone circle (ignores floor)."""
    upper = metrics["upper"]
    lower = metrics["lower"]
    hip_ankle = metrics["hipAnkle"]
    foot_side = metrics["ankleSide"]
    observed = metrics["kneeSide"]
    if not all(math.isfinite(x) for x in (upper, lower, hip_ankle, foot_side, observed)):
        raise ValueError("Non-finite bone telemetry")
    if upper <= 0 or lower <= 0 or hip_ankle <= 0:
        raise ValueError("Invalid bone length")
    if abs(foot_side) > hip_ankle + 0.002:
        raise ValueError("Ankle lateral offset exceeds hip-ankle distance")
    if hip_ankle > upper + lower + 0.002 or hip_ankle < abs(upper - lower) - 0.002:
        raise ValueError("Triangle inequality violated; inspect live rig/telemetry")

    # The engine clamps reach by 5mm; use the same reachable envelope.
    distance = max(abs(upper - lower) + 0.005,
                   min(hip_ankle, upper + lower - 0.005))
    if distance <= 0:
        raise ValueError("Invalid solver reach")
    along = (upper * upper - lower * lower + distance * distance) / (2 * distance)
    radius = math.sqrt(max(0.0, upper * upper - along * along))
    side_cosine = max(-1.0, min(1.0, foot_side / hip_ankle))
    # The bend circle is perpendicular to hip->ankle. Its largest projection
    # onto anatomical right is radius * |right projected onto that plane|.
    baseline_side = along * side_cosine
    transverse_side = radius * math.sqrt(max(0.0, 1 - side_cosine * side_cosine))
    minimum_abs_side = max(0.0, abs(baseline_side) - transverse_side)
    geometry_forces_flare = minimum_abs_side > max_knee_side_m + 0.001
    measured_flare = abs(observed) > max_knee_side_m
    if geometry_forces_flare:
        diagnosis = "FOOT_TARGET_OR_PELVIS_GEOMETRY_REQUIRED"
    elif measured_flare:
        diagnosis = "BEND_PLANE_OR_FLOOR_CONSTRAINT_CHECK"
    else:
        diagnosis = "WITHIN_LATERAL_LIMIT"
    return {
        "upper_m": round(upper, 4),
        "lower_m": round(lower, 4),
        "hip_ankle_m": round(hip_ankle, 4),
        "ankle_lateral_m": round(foot_side, 4),
        "observed_knee_lateral_m": round(observed, 4),
        "bend_circle_radius_m": round(radius, 4),
        "theoretical_min_abs_knee_lateral_m": round(minimum_abs_side, 4),
        "lateral_limit_m": max_knee_side_m,
        "diagnosis": diagnosis,
    }


def analyze_log(log: str, max_knee_side_m: float = 0.30) -> dict:
    samples: dict[str, dict] = {}
    current_key = None
    for line in log.splitlines():
        match = CONTACT.search(line)
        if match:
            stance, frame, blend = match.groups()
            current_key = f"{stance}:frame{frame}"
            samples[current_key] = {"blending": blend.lower() == "true", "legs": {}}
            continue
        match = REACH.search(line)
        if match and current_key is not None:
            side, tail = match.groups()
            values = dict((k, float(v)) for k, v in VALUE.findall(tail))
            missing = set(FIELDS) - values.keys()
            if missing:
                raise ValueError(f"{current_key} {side}: missing telemetry {sorted(missing)}")
            if side in samples[current_key]["legs"]:
                raise ValueError(f"{current_key}: repeated {side} reach telemetry")
            samples[current_key]["legs"][side] = analyze_leg(values, max_knee_side_m)

    if not samples:
        raise ValueError("No TRANSITION_CONTACT records")
    incomplete = [k for k, v in samples.items() if v["blending"] and set(v["legs"]) != {"L", "R"}]
    if incomplete:
        raise ValueError(f"Missing measured legs in blending frames: {incomplete}")
    classifications = sorted({leg["diagnosis"] for sample in samples.values()
                              for leg in sample["legs"].values()})
    return {"samples": samples, "diagnoses": classifications,
            "limitations": "Lateral reach only. Floor-safe knee, heel contact, animation frame timing, visuals, and mobile FPS require native tests."}


def self_test() -> None:
    def m(u: float, l: float, d: float, foot_side: float, knee_side: float) -> dict:
        return dict(upper=u, lower=l, hipAnkle=d, reachRatio=d / (u + l),
                    minimumReach=abs(u - l), ankleSide=foot_side,
                    kneeSide=knee_side, kneeAboveAnkle=0.10)

    assert analyze_leg(m(.5, .5, .85, .20, .41))["diagnosis"] == "BEND_PLANE_OR_FLOOR_CONSTRAINT_CHECK"
    impossible = analyze_leg(m(.5, .5, .98, .84, .41))
    assert impossible["theoretical_min_abs_knee_lateral_m"] > .30
    assert impossible["diagnosis"] == "FOOT_TARGET_OR_PELVIS_GEOMETRY_REQUIRED"
    assert analyze_leg(m(.5, .5, .85, .20, .10))["diagnosis"] == "WITHIN_LATERAL_LIMIT"
    # Lateral projection on a straight leg's axis is not a free knee correction.
    straight = analyze_leg(m(.5, .5, .99, .88, .45))
    assert straight["bend_circle_radius_m"] < .10
    fixture = '\n'.join([
        "AARIS_POSE_FRAME TRANSITION_CONTACT stance=Prone frame=1 crouch=0.500 prone=0.445 leftLocked=False rightLocked=False blending=True",
        "TRANSITION_LEG_REACH side=L upper=0.5000 lower=0.5000 hipAnkle=0.8500 reachRatio=0.8500 minimumReach=0.0000 ankleSide=0.2000 kneeSide=-0.0410 kneeAboveAnkle=0.1000",
        "TRANSITION_LEG_REACH side=R upper=0.5000 lower=0.5000 hipAnkle=0.9800 reachRatio=0.9800 minimumReach=0.0000 ankleSide=0.8400 kneeSide=0.4110 kneeAboveAnkle=0.1000",
    ])
    result = analyze_log(fixture)
    assert result["samples"]["Prone:frame1"]["legs"]["R"]["diagnosis"] == "FOOT_TARGET_OR_PELVIS_GEOMETRY_REQUIRED"
    try:
        analyze_log(fixture.rsplit('\n', 1)[0])
    except ValueError as e:
        assert "Missing measured legs" in str(e)
    else:
        raise AssertionError("Missing second-leg rejection regressed")
    try:
        analyze_leg(m(.5, .5, 1.50, .2, .41))
    except ValueError:
        pass
    else:
        raise AssertionError("Invalid rig geometry was accepted")
    print("PASS: exact two-bone lateral feasibility, log parsing, and fail-closed synthetic fixtures")
    print("NOT TESTED: Unity C# compilation, physical contact, native screenshots, Android frame time")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("log", nargs="?", type=Path)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--max-knee-side-m", type=float, default=0.30)
    options = parser.parse_args()
    if options.self_test:
        self_test()
    elif options.log is not None:
        print(json.dumps(analyze_log(options.log.read_text(encoding="utf-8"),
                                     options.max_knee_side_m), indent=2, sort_keys=True))
    else:
        parser.error("Supply a native QA log file or --self-test")


if __name__ == "__main__":
    main()
