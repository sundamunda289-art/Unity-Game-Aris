#!/usr/bin/env python3
"""Conservative proof of identical procedural HUD glyph edge/filled pixels.

The code mirrors the exact authored primitive envelopes and 128px pixel
centers. This is a source-level mathematical check, not a Unity render test.
"""
import math
import re
import unittest
from pathlib import Path

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisHudGlyphs.cs").read_text()
GLYPHS = ("Fire", "Jump", "Crouch", "Prone", "Sprint")
FLOAT = r"([0-9]*\.?[0-9]+)f"
CIRCLE_RE = re.compile(r"Circle\(p,\s*" + r",\s*".join([FLOAT] * 3) + r"\)")
LINE_RE = re.compile(r"Line\(p,\s*" + r",\s*".join([FLOAT] * 5) + r"\)")


def primitive_source(glyph):
    if glyph == "Fire":
        return SOURCE.split("private static float Fire(Vector2 p)", 1)[1].split(
            "private static void BuildAtlas()", 1)[0]
    human = SOURCE.split("private static float Human(", 1)[1].split(
        "private static float Fire(", 1)[0]
    return human.split("id == Glyph." + glyph, 1)[1].split(
        "else if (id == Glyph.", 1)[0]


def shapes(glyph):
    text = primitive_source(glyph)
    circles = [tuple(map(float, found)) for found in CIRCLE_RE.findall(text)]
    lines = [tuple(map(float, found)) for found in LINE_RE.findall(text)]
    assert circles or lines, glyph
    return circles, lines


def cull_rect(glyph):
    body = SOURCE.split("private static bool OutsideGlyphInk(", 1)[1].split(
        "private static float Circle(", 1)[0]
    pat = (r"case Glyph\." + glyph + r":\s*return p\.x < ([0-9.]+)f "
           r"\|\| p\.x > ([0-9.]+)f \|\| p\.y < ([0-9.]+)f "
           r"\|\| p\.y > ([0-9.]+)f;")
    match = re.search(pat, body)
    assert match, glyph
    return tuple(map(float, match.groups()))


def primitive_nonzero_at(x, y, circles, lines):
    # The source antialiasing has exactly zero coverage when SDF >= +0.008.
    for cx, cy, radius in circles:
        if math.hypot(x - cx, y - cy) - radius < .008 - 1e-8:
            return True
    for ax, ay, bx, by, width in lines:
        dx, dy = bx - ax, by - ay
        length_sq = dx * dx + dy * dy
        t = max(0., min(1., ((x - ax) * dx + (y - ay) * dy) /
                         max(.0001, length_sq)))
        if math.hypot(x - (ax + dx * t), y - (ay + dy * t)) - width / 2 < .008 - 1e-8:
            return True
    return False


class AndroidHudGlyphInkCulling(unittest.TestCase):
    def test_every_original_circle_and_line_fits_cull_envelope(self):
        for glyph in GLYPHS:
            with self.subTest(glyph=glyph):
                left, right, bottom, top = cull_rect(glyph)
                circles, lines = shapes(glyph)
                bounds = []
                for x, y, r in circles:
                    bounds.append((x-r-.008, x+r+.008,
                                   y-r-.008, y+r+.008))
                for ax, ay, bx, by, w in lines:
                    margin = w / 2 + .008
                    bounds.append((min(ax,bx)-margin, max(ax,bx)+margin,
                                   min(ay,by)-margin, max(ay,by)+margin))
                self.assertGreater(len(bounds), 5)
                for minx, maxx, miny, maxy in bounds:
                    self.assertLess(left, minx)
                    self.assertGreater(right, maxx)
                    self.assertLess(bottom, miny)
                    self.assertGreater(top, maxy)

    def test_all_culled_pixels_were_originally_fully_transparent(self):
        total_skipped, total_ink_calls = 0, 0
        for glyph in GLYPHS:
            left, right, bottom, top = cull_rect(glyph)
            circles, lines = shapes(glyph)
            skipped = 0
            for y in range(1, 127):
                fy = (y + 0.5) / 128
                for x in range(1, 127):
                    fx = (x + 0.5) / 128
                    if left <= fx <= right and bottom <= fy <= top:
                        total_ink_calls += 1
                        continue
                    skipped += 1
                    self.assertFalse(primitive_nonzero_at(fx, fy, circles, lines),
                                     (glyph, x, y))
            total_skipped += skipped
            self.assertGreater(skipped, 126 * 126 * .28, glyph)
        self.assertGreater(total_skipped, 126 * 126 * len(GLYPHS) * .44)
        self.assertLess(total_ink_calls, 126 * 126 * len(GLYPHS) * .56)

    def test_one_time_atlas_timing_is_reported_without_per_pixel_clock_calls(self):
        runtime = (Path(__file__).resolve().parents[2] /
                   "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs").read_text()
        self.assertIn("internal static float AtlasBuildMs { get; private set; } = -1f;", SOURCE)
        self.assertIn("private static void ResetAtlasTiming() { AtlasBuildMs = -1f; }", SOURCE)
        atlas = SOURCE.split("private static void BuildAtlas()", 1)[1]
        self.assertEqual(atlas.count("Time.realtimeSinceStartupAsDouble"), 2)
        self.assertLess(atlas.index("double startedAt = Time.realtimeSinceStartupAsDouble;"),
                        atlas.index("const int width = Size * Columns"))
        self.assertGreater(atlas.index("AtlasBuildMs = (float)("),
                           atlas.index("Sprites[i] = Sprite.Create(texture,"))
        self.assertIn("public float hudAtlasBuildMs = -1f;", runtime)
        self.assertIn("hudAtlasBuildMs = AarisHudGlyphs.AtlasBuildMs", runtime)
        self.assertNotIn("AtlasBuildMs", SOURCE.split("private static float Human(", 1)[1].split(
            "private static void BuildAtlas()", 1)[0])

    def test_no_hud_appearance_or_texture_resolution_changes(self):
        self.assertIn("private const int Size = 128;", SOURCE)
        self.assertIn("private const int Columns = 4;", SOURCE)
        self.assertIn("private static readonly Sprite[] Sprites = new Sprite[7];", SOURCE)
        self.assertIn("new Texture2D(width, height, TextureFormat.RGBA32, false)", SOURCE)
        self.assertIn("texture.SetPixels32(pixels);", SOURCE)
        self.assertIn("texture.Apply(false, true);", SOURCE)
        self.assertIn("Sprite.Create(texture,", SOURCE)
        self.assertIn("if (OutsideGlyphInk(id, p)) return 0f;", SOURCE)
        self.assertIn("if (OutsideGlyphInk(Glyph.Fire, p)) return 0f;", SOURCE)
        self.assertEqual(SOURCE.count("OutsideGlyphInk("), 3)
        self.assertNotIn("QualitySettings", SOURCE)


if __name__ == "__main__":
    unittest.main(verbosity=2)
