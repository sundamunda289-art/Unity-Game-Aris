#!/usr/bin/env python3
"""Static guards for reversible, no-blur Android texture IO pacing.

These source-level checks are not device profiling, Unity compilation or proof
that a particular Android OS crash has been fixed.
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RUNTIME = (ROOT / "Assets/AarisWorld/Scripts/AarisMobileRuntime.cs").read_text(encoding="utf-8")
QUALITY = (ROOT / "ProjectSettings/QualitySettings.asset").read_text(encoding="utf-8")


def limited_requests(authored_limit, constrained_gpu):
    return min(authored_limit, 8) if constrained_gpu else authored_limit


class AndroidMipmapIOLimit(unittest.TestCase):
    def test_authored_quality_remains_full_resolution(self):
        self.assertRegex(QUALITY, r"streamingMipmapsActive: 1")
        self.assertRegex(QUALITY, r"globalTextureMipmapLimit: 0")
        self.assertRegex(QUALITY, r"streamingMipmapsMaxFileIORequests: 16")

    def test_android_only_and_entry_level_gpu_only(self):
        setup = RUNTIME.split("private void ConfigureAndroidQuality()", 1)[1]
        setup = setup.split("private void SampleMobileFrameTiming()", 1)[0]
        self.assertIn("if (constrainedGraphics)", setup)
        io = setup.split("originalMipFileRequests = ", 1)[1]
        self.assertIn("QualitySettings.streamingMipmapsMaxFileIORequests;", io)
        self.assertIn("Mathf.Min(originalMipFileRequests, 8)", io)
        self.assertIn("if (originalMipFileRequests > 8)", io)
        self.assertIn("QualitySettings.streamingMipmapsMaxFileIORequests = appliedMipFileRequests;", io)
        self.assertNotIn("globalTextureMipmapLimit =", io)
        self.assertIn("#if UNITY_ANDROID && !UNITY_EDITOR", RUNTIME)
        self.assertIn("IsBudgetAndroidGpu()", setup)

    def test_does_not_change_native_mip_sizes_or_upgrade_custom_lower_limit(self):
        self.assertEqual(limited_requests(16, True), 8)
        self.assertEqual(limited_requests(16, False), 16)
        self.assertEqual(limited_requests(4, True), 4)
        self.assertEqual(limited_requests(8, True), 8)
        self.assertIn("originalMipFileRequests > 8", RUNTIME)

    def test_restore_only_if_still_owned(self):
        cleanup = RUNTIME.split("private void OnDestroy()", 1)[1]
        self.assertRegex(cleanup, re.compile(
            r"if \(modifiedMipFileRequests &&\s*"
            r"QualitySettings\.streamingMipmapsMaxFileIORequests == appliedMipFileRequests\)\s*"
            r"QualitySettings\.streamingMipmapsMaxFileIORequests = originalMipFileRequests;"
        ))


if __name__ == "__main__":
    unittest.main()
