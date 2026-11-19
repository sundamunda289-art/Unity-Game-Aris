#!/usr/bin/env python3
"""Privacy-preserving triage of Android logcat. Evidence, not root-cause proof."""
import argparse
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

DEFAULT_PACKAGE = "com.aaris.game"
CATEGORIES = (
    "lmk_kill", "app_anr", "java_crash", "native_crash",
    "app_oom", "app_process_exit_unknown", "app_low_memory_signal",
    "app_frame_stall", "gpu_driver_signal_unattributed",
    "thermal_signal_unattributed",
)


def analyze(lines, package=DEFAULT_PACKAGE):
    """Stream input, retain only category counts and bounded line references."""
    if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+", package):
        raise ValueError("invalid Android package name")
    boundary = re.compile(r"(?<![A-Za-z0-9_.])" + re.escape(package.casefold())
                          + r"(?![A-Za-z0-9_.])")
    counts = Counter()
    locations = defaultdict(list)
    pending_java = pending_native = None
    read = 0

    def add(category, index):
        counts[category] += 1
        if len(locations[category]) < 12:
            locations[category].append(index)

    for index, line in enumerate(lines, start=1):
        read = index
        low = line.casefold()
        app = bool(boundary.search(low))
        # Android's FATAL EXCEPTION and native tombstone often identify the
        # process a few lines after the generic failure header.
        if "fatal exception:" in low:
            pending_java = index
        if "fatal signal " in low and ("sigsegv" in low or "sigabrt" in low
                                       or "sigbus" in low):
            pending_native = index
        if app and pending_java is not None and index - pending_java <= 12:
            if "process:" in low or "fatal exception:" in low:
                add("java_crash", pending_java)
                pending_java = None
        if app and pending_native is not None and index - pending_native <= 16:
            if ">>>" in low or "fatal signal " in low or "pid:" in low:
                add("native_crash", pending_native)
                pending_native = None
        if pending_java is not None and index - pending_java > 12:
            pending_java = None
        if pending_native is not None and index - pending_native > 16:
            pending_native = None

        if app and ("anr in " in low or "input dispatching timed out" in low
                    or "application not responding" in low):
            add("app_anr", index)
        if app and (("lmkd" in low and ("kill " in low or "killing " in low))
                    or ("lowmemorykiller" in low and "kill" in low)):
            add("lmk_kill", index)
        if app and ("outofmemoryerror" in low or "out of memory" in low
                    and ("alloc" in low or "failed" in low)):
            add("app_oom", index)
        if app and ("has died" in low or "process died" in low):
            add("app_process_exit_unknown", index)
        if app and ("low-memory" in low or "onlowmemory" in low):
            add("app_low_memory_signal", index)
        if app and ("frame-stall" in low or "aaris_frame_stall" in low):
            add("app_frame_stall", index)
        # System-wide driver/thermal messages do NOT establish that this
        # game caused the event, even if close to an app stall timestamp.
        if re.search(r"\b(?:kgsl|adreno|gpu driver)\b", low) and re.search(
                r"\b(?:hang|fault|reset|timeout|deadlock)\b", low):
            add("gpu_driver_signal_unattributed", index)
        if re.search(r"\b(?:thermal|thermalengine|thermald)\b", low) and re.search(
                r"\b(?:throttl|critical|emergency|shutdown)\w*", low):
            add("thermal_signal_unattributed", index)

    hard = ("lmk_kill", "app_anr", "java_crash", "native_crash", "app_oom")
    return {
        "package": package,
        "lines_scanned": read,
        "categories": {kind: {"count": counts[kind], "line_numbers": locations[kind]}
                       for kind in CATEGORIES if counts[kind]},
        "has_explicit_app_failure_signal": any(counts[k] for k in hard),
        "limitations": (
            "A logcat match does not prove the underlying cause; driver/thermal "
            "signals are not attributed to the app. Missing entries do not rule "
            "out LMK, GPU hang, or ANR. Compare with Android process exit "
            "reasons, tombstones, memory profiler, and device captures."
        ),
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", help="Local logcat text file, or - for stdin")
    parser.add_argument("--package", default=DEFAULT_PACKAGE)
    parser.add_argument("--output", help="Write sanitized JSON summary to this path")
    args = parser.parse_args(argv)
    if args.input == "-":
        report = analyze(sys.stdin, args.package)
    else:
        with open(args.input, "r", encoding="utf-8", errors="replace") as handle:
            report = analyze(handle, args.package)
    summary = json.dumps(report, indent=2, sort_keys=True) + "\n"
    if args.output:
        Path(args.output).write_text(summary, encoding="utf-8")
    else:
        sys.stdout.write(summary)


if __name__ == "__main__":
    main()
