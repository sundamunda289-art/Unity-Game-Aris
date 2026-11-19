#!/usr/bin/env python3
"""Regression tests for privacy-safe, evidence-only Unity diagnostics analysis."""
from io import StringIO
import importlib.util
import json
from pathlib import Path
import unittest

PATH = Path(__file__).with_name("analyze_android_runtime_diagnostics.py")
SPEC = importlib.util.spec_from_file_location("aaris_runtime_diagnostics", PATH)
MOD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MOD)


def report(*items):
    return MOD.analyze(StringIO("".join(
        json.dumps(i) + "\n" if isinstance(i, dict) else i + "\n" for i in items)))


class RuntimeDiagnosticEvidence(unittest.TestCase):
    def test_streaming_policy_and_counters_require_valid_evidence(self):
        empty = report({"reason": "sample"})["optional_streaming"]
        self.assertTrue(all(value is None for value in empty.values()))
        result = report(
            {"reason": "sample", "optionalStreamConstrained": True,
             "optionalStreamImportDeferrals": 4,
             "optionalStreamImportSettleDeferrals": 3,
             "optionalStreamPressureDeferredFrames": 2},
            {"reason": "sample", "optionalStreamConstrained": "private-device-name",
             "optionalStreamImportDeferrals": -1,
             "optionalStreamImportSettleDeferrals": True,
             "optionalStreamPressureDeferredFrames": float("inf")})
        self.assertEqual(result["optional_streaming"], {
            "constrained_policy_when_observed": True,
            "import_busy_attempts_cumulative_max": 4,
            "integration_frame_attempts_cumulative_max": 3,
            "pressure_recovery_frames_cumulative_max": 2,
        })
        self.assertNotIn("private-device-name", json.dumps(result))

    def test_empty_log_does_not_invent_crash_cause(self):
        result = MOD.analyze([])
        self.assertEqual(result["valid_records"], 0)
        self.assertIsNone(result["frame_windows"]["max_recorded_rolling_frame_ms"])
        self.assertIsNone(result["aircraft_startup"]["total_visual_create_ms_max"])
        self.assertIn("No valid Aaris JSON telemetry was provided",
                      result["signals_to_investigate"])
        self.assertNotIn("driver deadlock", json.dumps(result).lower())

    def test_reports_stall_and_nearby_unload_without_causation(self):
        result = report(
            {"reason": "low-memory", "assetUnloadStartedCount": 1,
             "assetUnloadInProgress": True},
            {"reason": "frame-stall", "assetUnloadInProgress": True,
             "maxFrameMs": 3100, "p95FrameMs": 45},
            {"reason": "asset-unload-complete", "assetUnloadCompletedCount": 1,
             "lastAssetUnloadElapsedMs": 1500},
            {"reason": "sample", "p95FrameMs": 70, "maxFrameMs": 3100})
        self.assertEqual(result["frame_windows"]["explicit_stall_reports"], 1)
        self.assertEqual(result["memory_recovery"]["low_memory_reports"], 1)
        self.assertEqual(result["memory_recovery"]["stall_records_during_asset_unload"], 1)
        self.assertEqual(result["memory_recovery"]["max_reported_unload_wall_ms"], 1500)
        self.assertEqual(result["frame_windows"]["max_recorded_p95_ms"], 70)
        self.assertIn("association is not causation", json.dumps(result))

    def test_plane_stages_remain_independent_and_unknown_is_null(self):
        result = report(
            {"reason": "start", "planeModelResolveMs": -1, "planeInstantiateMs": -1,
             "planeTotalCreationMs": -1},
            {"reason": "startup", "planeModelResolveMs": 140,
             "planeInstantiateMs": 30, "planeTotalCreationMs": 205,
             "planeHighDetailSelected": True})
        self.assertEqual(result["aircraft_startup"]["model_resolve_ms_max"], 140)
        self.assertEqual(result["aircraft_startup"]["prefab_instantiate_ms_max"], 30)
        self.assertEqual(result["aircraft_startup"]["total_visual_create_ms_max"], 205)
        self.assertTrue(result["aircraft_startup"]["high_detail_selected_when_observed"])

    def test_gpu_and_cpu_labels_require_real_frame_samples(self):
        result = report(
            {"reason": "sample", "gpuTimingSamples": 4, "meanCpuMainMs": 35,
             "meanGpuFrameMs": 4},
            {"reason": "sample", "gpuTimingSamples": 10, "meanCpuMainMs": 34,
             "meanGpuFrameMs": 8},
            {"reason": "sample", "gpuTimingSamples": 6, "meanCpuMainMs": 7,
             "meanGpuFrameMs": 28})
        self.assertEqual(result["frame_windows"]["cpu_dominant_sample_windows"], 1)
        self.assertEqual(result["frame_windows"]["gpu_dominant_sample_windows"], 1)

    def test_malformed_oversized_and_nonfinite_data_do_not_crash(self):
        result = report(
            "bad-json",
            "q" * (MOD.MAX_LINE_CHARS + 1),
            {"reason": "sample", "maxFrameMs": float("inf"),
             "p95FrameMs": -1, "planeInstantiateMs": None})
        self.assertEqual(result["invalid_records"], 1)
        self.assertEqual(result["oversized_records_skipped"], 1)
        self.assertIsNone(result["frame_windows"]["max_recorded_rolling_frame_ms"])

    def test_no_personal_identifiers_or_error_stacks_leak_to_report(self):
        secret = "PRIVATE_ANDROID_SERIAL_123"
        result = report({"reason": "sample", "device": secret,
                         "lastError": secret, "gpu": secret,
                         "version": secret, "frameSamples": 9,
                         "previousExitReasonCode": 3})
        self.assertNotIn(secret, json.dumps(result))
        self.assertEqual(result["previous_process_exit_reason_codes_observed"], [3])

    def test_android_exit_reason_classes_distinguish_anr_memory_and_native_crash(self):
        result = report(
            {"reason": "previous-process-exit", "previousExitReasonCode": 3},
            {"reason": "sample", "previousExitReasonCode": 5},
            {"reason": "sample", "previousExitReasonCode": 6},
            {"reason": "sample", "previousExitReasonCode": 99})
        self.assertEqual(result["previous_process_exit_reason_codes_observed"],
                         [3, 5, 6, 99])
        self.assertEqual(result["previous_process_exit_reason_labels_observed"],
                         ["low-memory-kill", "native-crash", "anr", "other"])
        signals = " ".join(result["signals_to_investigate"]).lower()
        self.assertIn("low-memory kill", signals)
        self.assertIn("anr", signals)
        self.assertIn("native crash", signals)
        self.assertIn("before inferring a graphics-driver fault", signals)
        self.assertNotIn("confirmed gpu driver deadlock", signals)

    def test_signaled_exit_cannot_be_misidentified_as_low_memory(self):
        result = report(
            {"reason": "previous-process-exit", "previousExitReasonCode": 2},
            {"reason": "sample", "previousExitReasonCode": -1},
            {"reason": "sample", "previousExitReasonCode": float("inf")})
        self.assertEqual(result["previous_process_exit_reason_codes_observed"], [2])
        self.assertEqual(result["previous_process_exit_reason_labels_observed"],
                         ["signaled"])
        signals = " ".join(result["signals_to_investigate"]).lower()
        self.assertIn("ambiguous", signals)
        self.assertNotIn("previous low-memory kill", signals)

    def test_input_limit_is_reported_not_silently_ignored(self):
        original_limit = MOD.MAX_EVENTS
        try:
            MOD.MAX_EVENTS = 2
            result = report({"reason": "start"}, {"reason": "sample"},
                            {"reason": "frame-stall"})
        finally:
            MOD.MAX_EVENTS = original_limit
        self.assertTrue(result["input_truncated_at_record_limit"])
        self.assertEqual(result["valid_records"], 2)
        self.assertEqual(result["frame_windows"]["explicit_stall_reports"], 0)

    def test_unknown_reason_not_echoed_and_rollups_are_bounded(self):
        result = report({"reason": "my-secret-marker", "warningCount": 1,
                         "assetUnloadStartedCount": 1_000_000})
        self.assertEqual(result["event_counts"], {"other": 1})
        self.assertNotIn("my-secret-marker", json.dumps(result))
        self.assertEqual(
            result["memory_recovery"]["asset_unloads_started_cumulative_max"], 1_000_000)


if __name__ == "__main__":
    unittest.main(verbosity=2)
