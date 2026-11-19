#!/usr/bin/env python3
"""Summarize AarisDiagnostics JSONL without echoing device IDs or raw logs.

Offline source evidence, not a diagnosis of Android ANRs, LMKs or GPU faults.
"""
import argparse
from collections import Counter
import json
import math
from pathlib import Path
import sys

MAX_LINE_CHARS = 262144
MAX_EVENTS = 500000
KNOWN_REASONS = {
    "start", "startup", "frame-stall", "sample", "low-memory",
    "asset-unload-complete", "pause", "quit", "terrain-render-audit",
    "previous-process-exit"
}
# Stable Android 11+ ApplicationExitInfo getReason() values. Treat SIGNALED
# as ambiguous: some devices report low-memory kills this way.
# Names are sanitized classifications, not device logs or proof of a driver bug.
EXIT_REASON_LABELS = {
    2: "signaled",
    3: "low-memory-kill",
    4: "java-crash",
    5: "native-crash",
    6: "anr",
}
LIMITATIONS = (
    "Statistics summarize Unity's emitted records, not full frame traces, "
    "Android PSS, native crash logs or a proof of causation. Repeated rolling "
    "frame maxima can refer to the same hitch. Absence of a signal does not "
    "rule out an ANR, LMK or graphics-driver failure."
)


def number(record, key, *, minimum=0.0):
    value = record.get(key)
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    if not math.isfinite(value) or value < minimum:
        return None
    return round(float(value), 3)


def analyze(lines):
    reasons = Counter()
    invalid = valid = oversized = 0
    truncated = False
    max_frame = max_p95 = None
    max_unload_ms = None
    max_plane_resolve = max_plane_instantiate = max_plane_total = None
    active_unload_stall_records = 0
    cpu_limited_windows = gpu_limited_windows = 0
    plane_high_detail = None
    previous_exit_reason_codes = set()
    started = completed = suppressed = 0
    optional_constrained = None
    optional_counters = {
        "optionalStreamImportDeferrals": None,
        "optionalStreamImportSettleDeferrals": None,
        "optionalStreamPressureDeferredFrames": None,
    }

    def maximum(old, new):
        return old if new is None else new if old is None else max(old, new)

    for index, line in enumerate(lines):
        if index >= MAX_EVENTS:
            truncated = True
            break
        if len(line) > MAX_LINE_CHARS:
            oversized += 1
            continue
        try:
            row = json.loads(line)
        except (ValueError, TypeError):
            invalid += 1
            continue
        if not isinstance(row, dict):
            invalid += 1
            continue
        valid += 1
        if isinstance(row.get("optionalStreamConstrained"), bool):
            optional_constrained = row["optionalStreamConstrained"]
        for key in optional_counters:
            optional_counters[key] = maximum(optional_counters[key], number(row, key))
        reason = row.get("reason")
        reason = reason if isinstance(reason, str) and reason in KNOWN_REASONS else "other"
        reasons[reason] += 1
        max_frame = maximum(max_frame, number(row, "maxFrameMs"))
        max_p95 = maximum(max_p95, number(row, "p95FrameMs"))
        max_unload_ms = maximum(max_unload_ms, number(row, "lastAssetUnloadElapsedMs"))
        max_plane_resolve = maximum(max_plane_resolve, number(row, "planeModelResolveMs"))
        max_plane_instantiate = maximum(max_plane_instantiate,
                                        number(row, "planeInstantiateMs"))
        max_plane_total = maximum(max_plane_total, number(row, "planeTotalCreationMs"))
        if number(row, "planeModelResolveMs") is not None and isinstance(
                row.get("planeHighDetailSelected"), bool):
            plane_high_detail = row["planeHighDetailSelected"]
        if reason == "frame-stall" and row.get("assetUnloadInProgress") is True:
            active_unload_stall_records += 1
        started = max(started, int(number(row, "assetUnloadStartedCount") or 0))
        completed = max(completed, int(number(row, "assetUnloadCompletedCount") or 0))
        suppressed = max(suppressed, int(number(row, "assetUnloadSuppressedCount") or 0))
        if reason == "sample" and (number(row, "gpuTimingSamples") or 0) >= 5:
            gpu = number(row, "meanGpuFrameMs")
            cpu = number(row, "meanCpuMainMs")
            if gpu is not None and cpu is not None:
                if cpu >= 16.7 and cpu > gpu * 1.35:
                    cpu_limited_windows += 1
                elif gpu >= 16.7 and gpu > cpu * 1.35:
                    gpu_limited_windows += 1
        code = number(row, "previousExitReasonCode")
        if code is not None and code <= 1000:
            previous_exit_reason_codes.add(int(code))

    signals = []
    if reasons["frame-stall"]:
        signals.append("Unity emitted explicit long-frame records; inspect matching frame timings")
    if reasons["low-memory"]:
        signals.append("Unity received low-memory notifications; check Android LMK/PSS evidence")
    if active_unload_stall_records:
        signals.append(
            "Some long-frame records overlap an in-flight asset unload; association is not causation"
        )
    if max_plane_total is not None and max_plane_total >= 100:
        signals.append(
            "Aircraft creation wall time reached >=100 ms; compare resolve, instantiate and setup"
        )
    if cpu_limited_windows:
        signals.append(
            "At least one sampled window was CPU-main-time dominant; Unity profiler required"
        )
    if gpu_limited_windows:
        signals.append(
            "At least one sampled window was GPU-time dominant; Android GPU profiler required"
        )
    if 3 in previous_exit_reason_codes:
        signals.append(
            "Android exit history reports a previous low-memory kill; compare PSS "
            "and memory-pressure evidence")
    if 6 in previous_exit_reason_codes:
        signals.append(
            "Android exit history reports a previous ANR; inspect ANR and "
            "main-thread traces before assigning its cause")
    if 5 in previous_exit_reason_codes:
        signals.append(
            "Android exit history reports a previous native crash; inspect the "
            "native tombstone before inferring a graphics-driver fault")
    if 4 in previous_exit_reason_codes:
        signals.append(
            "Android exit history reports a previous Java crash; inspect the "
            "redacted exception trace")
    if 2 in previous_exit_reason_codes:
        signals.append(
            "Android exit history reports a signal; some Android devices report "
            "low-memory kills as SIGNALED, so this reason alone is ambiguous")
    if not valid:
        signals.append("No valid Aaris JSON telemetry was provided")
    return {
        "valid_records": valid,
        "invalid_records": invalid,
        "oversized_records_skipped": oversized,
        "input_truncated_at_record_limit": truncated,
        "event_counts": dict(sorted(reasons.items())),
        "frame_windows": {
            "explicit_stall_reports": reasons["frame-stall"],
            "max_recorded_rolling_frame_ms": max_frame,
            "max_recorded_p95_ms": max_p95,
            "cpu_dominant_sample_windows": cpu_limited_windows,
            "gpu_dominant_sample_windows": gpu_limited_windows
        },
        "memory_recovery": {
            "low_memory_reports": reasons["low-memory"],
            "asset_unloads_started_cumulative_max": started,
            "asset_unloads_completed_cumulative_max": completed,
            "asset_unloads_suppressed_cumulative_max": suppressed,
            "max_reported_unload_wall_ms": max_unload_ms,
            "stall_records_during_asset_unload": active_unload_stall_records
        },
        "aircraft_startup": {
            "model_resolve_ms_max": max_plane_resolve,
            "prefab_instantiate_ms_max": max_plane_instantiate,
            "total_visual_create_ms_max": max_plane_total,
            "high_detail_selected_when_observed": plane_high_detail
        },
        "optional_streaming": {
            "constrained_policy_when_observed": optional_constrained,
            "import_busy_attempts_cumulative_max": optional_counters["optionalStreamImportDeferrals"],
            "integration_frame_attempts_cumulative_max": optional_counters["optionalStreamImportSettleDeferrals"],
            "pressure_recovery_frames_cumulative_max": optional_counters["optionalStreamPressureDeferredFrames"],
        },
        "previous_process_exit_reason_codes_observed": sorted(previous_exit_reason_codes),
        "previous_process_exit_reason_labels_observed": [
            EXIT_REASON_LABELS.get(code, "other")
            for code in sorted(previous_exit_reason_codes)
        ],
        "signals_to_investigate": signals,
        "limitations": LIMITATIONS
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", help="AarisDiagnostics/current.jsonl, or - for stdin")
    parser.add_argument("--output", help="Optional sanitized JSON output destination")
    args = parser.parse_args(argv)
    if args.input == "-":
        result = analyze(sys.stdin)
    else:
        with open(args.input, "r", encoding="utf-8", errors="replace") as source:
            result = analyze(source)
    result_json = json.dumps(result, sort_keys=True, indent=2) + "\n"
    if args.output:
        Path(args.output).write_text(result_json, encoding="utf-8")
    else:
        sys.stdout.write(result_json)


if __name__ == "__main__":
    main()
