#!/usr/bin/env python3
"""Synthetic frame guards, not proof of an actual Unity render."""
import json
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from io import StringIO
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "Tools" / "CI"))
from photo_audit import audit


class ScreenshotFailureGuards(unittest.TestCase):
    def check(self, paint, should_fail):
        with tempfile.TemporaryDirectory(prefix="aaris-photo-") as folder:
            directory = Path(folder)
            image = Image.new("RGB", (1920, 1080))
            paint(image)
            image.save(directory / "synthetic.png")
            with redirect_stdout(StringIO()):
                if should_fail:
                    with self.assertRaisesRegex(RuntimeError, "Potential black/white/flat frame"):
                        audit(directory)
                else:
                    audit(directory)
            return json.loads((directory / "photo-audit.json").read_text())["frames"][0]

    def test_noisy_navy_failure(self):
        def paint(image):
            draw = ImageDraw.Draw(image)
            for y in range(1080):
                draw.line((0, y, 1919, y), fill=(9+y%7, 18+y%11, 59+y%18))
        self.assertGreater(self.check(paint, True)["blue_black_fraction"], 0.85)

    def test_missing_shader_magenta(self):
        def paint(image):
            draw = ImageDraw.Draw(image)
            draw.rectangle((0, 0, 1400, 1079), fill=(255, 0, 255))
            draw.rectangle((1401, 0, 1919, 1079), fill=(100, 140, 70))
        self.assertGreater(self.check(paint, True)["error_magenta_fraction"], 0.18)

    def test_daylight_sky_and_ground(self):
        def paint(image):
            draw = ImageDraw.Draw(image)
            draw.rectangle((0, 0, 1919, 430), fill=(80, 158, 210))
            draw.rectangle((0, 431, 1919, 1079), fill=(87, 103, 67))
            draw.rectangle((500, 620, 1200, 880), fill=(147, 128, 99))
        self.assertLess(self.check(paint, False)["blue_black_fraction"], 0.85)

    def test_original_black_frame(self):
        self.check(lambda image: ImageDraw.Draw(image).rectangle(
            (0, 0, 1919, 1079), fill=(0, 0, 0)), True)


if __name__ == "__main__":
    unittest.main(verbosity=2)
