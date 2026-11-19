# Android frame stall / memory incident report (2026-11-19)

## Why this exists

The repo now contains real in-player JSON diagnostics for long frames,
critical-memory cleanup, plane creation stage timings and prior process-exit
history, as well as a separate privacy-safe logcat scanner. Without an
attached phone, the project's actual 3–30 second freezes, launch locks and
Home-screen exits remain unclassified. Successful static CI checks do not
establish Unity compilation, FPS stability or crash freedom.

## Usage

After testing the **actual APK on a real device**, copy its
`AarisDiagnostics/current.jsonl` file to the investigation machine. Then:

```bash
python3 Tools/Validation/analyze_android_runtime_diagnostics.py current.jsonl \
  --output /tmp/aaris-runtime-incident-summary.json
python3 Tools/Validation/analyze_android_logcat.py aaris-android-logcat.txt \
  --output /tmp/aaris-logcat-summary.json
```

Read the two sanitized JSON summaries together. They intentionally do not
include raw exception stack traces, device IDs, model names, source lines,
timestamps, tokens or package-private data.

The runtime report categorizes:
- explicit `frame-stall` records, rolling max/p95 frame time;
- low-memory warnings and unload lifecycle counts, duration and overlap;
- first aircraft Resources lookup vs native Instantiate vs total visual setup;
- CPU/GPU-dominant sample windows only when enough timing samples exist;
- historical Android process-exit numeric reason codes where available.

*Wall-clock completion is not native CPU time. An unload-in-progress
record alongside a stall is a correlation, not proof of cause.* A missing
event cannot exclude ANR, LMK or native/GPU crashes; Android may terminate
the process before Unity can write. Diagnose with device logcat, native
tombstones, Android process-exit reasons and frame profiler captures.

No Unity C#, graphics assets, shader/terrain quality, physics or gameplay
are changed by this analyzer.
