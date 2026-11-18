#!/usr/bin/env python3
"""Static parity contract for native Unity camera capture, not a rendered-frame test."""
import os
import unittest
from pathlib import Path

DEFAULT = Path(__file__).resolve().parents[2] / (
    "Assets/AarisWorld/Editor/AarisCodespaceSceneProbe.cs")
SOURCE = Path(os.environ.get("AARIS_SCENE_PROBE_SOURCE", str(DEFAULT))).read_text("utf-8")
BODY = SOURCE.split("private static float Render(", 1)[1].split(
    '[MenuItem("Aaris/CI/Native 2km overhead screenshot")]', 1)[0]


class NativeSceneScreenshotGuard(unittest.TestCase):
    def test_dark_navy_map_is_not_a_successful_render(self):
        for token in ("navyBlack", "p.r < 70", "p.g < 95",
                      "p.b > 25", "p.b < 165", "p.b > p.r + 18",
                      "p.b > p.g + 12", "navyBlack > count * 0.85f",
                      "FRAME_NAVY_BLACK_TERRAIN"):
            self.assertIn(token, BODY)
        self.assertLess(BODY.index("FRAME_NAVY_BLACK_TERRAIN"),
                        BODY.index("File.WriteAllBytes(file, png)"))

    def test_blank_white_and_black_camera_are_rejected(self):
        for token in ("black > count * 0.93f", "white > count * 0.93f",
                      "FRAME_BLACK_CAMERA", "FRAME_WHITE_CAMERA"):
            self.assertIn(token, BODY)
        self.assertLess(BODY.index("FRAME_BLACK_CAMERA"),
                        BODY.index("File.WriteAllBytes(file, png)"))

    def test_magenta_and_variance_guards_are_preserved(self):
        for token in ("FRAME_MAGENTA_SHADER_FAILURE", "FRAME_NEARLY_UNIFORM",
                      "magenta >= Math.Max(4, count / 1000)"):
            self.assertIn(token, BODY)


if __name__ == "__main__":
    unittest.main()
