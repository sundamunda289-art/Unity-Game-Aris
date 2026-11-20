#!/usr/bin/env python3
"""Source and mathematical contract for native Unity locomotion capture safety.

This protects the QA harness; it does not claim a C# compile, real screenshot,
a corrected knee pose, or Android device performance.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
qa = (ROOT / "Assets/AarisWorld/Editor/AarisPoseFrames.cs").read_text(
    encoding="utf-8"
)
runtime = (ROOT / "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text(
    encoding="utf-8"
)

# EditorApplication.update is not a game-frame clock: two screenshot
# callbacks may have distinct Editor ticks yet share a game timestamp.
assert "static int lastMotionCaptureFrame = -1;" in qa
assert "static double lastMotionCaptureTime = double.NegativeInfinity;" in qa
assert qa.count("lastMotionCaptureTime = double.NegativeInfinity;") == 2
assert "Time.frameCount != lastMotionCaptureFrame &&" in qa
assert "Time.unscaledTimeAsDouble > lastMotionCaptureTime + 0.000001d" in qa
assert "lastMotionCaptureFrame = Time.frameCount;" in qa
assert "lastMotionCaptureTime = Time.unscaledTimeAsDouble;" in qa
assert qa.index("lastMotionCaptureTime = Time.unscaledTimeAsDouble;") < qa.index(
    'Log("MOTION_TIMESTAMP stage="'
)


def chronological(previous_frame, previous_time, frame, time):
    return frame != previous_frame and time > previous_time + 0.000001


for previous_frame, previous_time, frame, time, expected in (
    (100, 1.000000, 100, 1.200000, False),  # duplicate game frame
    (100, 1.000000, 101, 1.000000, False),  # newer frame but frozen time
    (100, 1.000000, 101, 1.0000005, False),  # noise, not new pose
    (100, 1.000000, 101, 1.020000, True),
    (100, 1.000000, 102, 1.200000, True),
):
    assert chronological(previous_frame, previous_time, frame, time) is expected


# A missing humanoid or degenerate hip plane cannot silently count as a
# validated image when foot and knee measurements were skipped.
for failure in (
    "MOTION_HUMANOID_AVATAR_MISSING",
    "MOTION_REQUIRED_BONES_MISSING",
    "MOTION_DEGENERATE_HIP_AXIS",
):
    assert f'throw new InvalidOperationException("{failure}");' in qa

# Alert during crouch, crouch-walk, prone, crawl, or getting up only.
# Authored jump frames are not subjected to a grounded knee-angle threshold.
assert "if(!p.IsInAir && motionStage>=4 && motionStage<=8 &&" in qa
assert "Mathf.Max(Mathf.Abs(leftKneeSide),Mathf.Abs(rightKneeSide))>0.30f" in qa
assert 'MOTION_GROUNDED_KNEE_SIDEWAYS_OVER_30CM' in qa
assert qa.index('Log("MOTION_RIG state="') < qa.index(
    "MOTION_GROUNDED_KNEE_SIDEWAYS_OVER_30CM"
)


def reject_knee(stage, airborne, left, right):
    return not airborne and 4 <= stage <= 8 and max(abs(left), abs(right)) > .30


for stage in range(10):
    for airborne in (False, True):
        for side in (.0, .299, .301, .411, -.526):
            expected = (not airborne and stage in (4, 5, 6, 7, 8)
                        and abs(side) > .30)
            assert reject_knee(stage, airborne, side, 0.0) is expected

# This branch must not change the existing gameplay-owned IK/physics design.
assert "private static void PlantLegToTarget(" in runtime
assert "private void PlanBalancedLowStanceFeet(" in runtime
assert "private float SupportedAnkleY(" in runtime

print("PASS: frame chronology and grounded knee QA source contracts")
