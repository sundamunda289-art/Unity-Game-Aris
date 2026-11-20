# Android terrain-audit and exit-report frame pacing — 2026-11-20

## Source-confirmed scheduling issue

Before this patch, Android inspected all four native terrain tiles (TerrainData,
shader/material metadata and control texture counts) and immediately serialized
its diagnostic snapshot immediately after the opening terrain frame. The prior
Android process-exit query also started a profiler/JSON report as soon as its
background Task completed, bypassing the shared optional-stream admission
budget. Those observability tasks can coincide with model/material imports,
physics maintenance and URP startup. This is an identified scheduling risk,
not a measured root cause for a particular physical handset freeze.

## Changes

- Only on Android players, defer terrain metadata inspection until at least
  two seconds after runtime startup **and** the existing global
  `AarisMobileStreamingFrameGate.TryClaimNonessentialDiagnosticFrame`
  admits nonessential work.
- Separate terrain metadata inspection from its profiler/JSON report: the
  scan reserves its frame; the report retries in a later admitted frame.
- Defer the prior-process-exit report after its asynchronous query. Keep the
  result/unknown fallback, do not discard it when optional work is busy.
- Preserve urgent low-memory and long-frame reporting priority, all authored
  terrain, vegetation, physics, water, models, shadows, pixel resolution and
  GPU settings. Unity Editor remains on the existing synchronous audit path.
- Android's existing startup, sample, memory, and stall diagnostics still run.
  No per-frame collections, forced GC, dynamic asset destruction or scene edits.

## Validation and release gate

Run `python Tools/Validation/test_android_diagnostics_frame_pacing.py`,
existing streaming/diagnostics contracts, the production-source C# parser,
and GitHub CI. These prove source scheduling invariants, **not** real
Unity Editor compilation, Play Mode rendering, FPS improvement or Android
thermal stability. Verify an actual Snapdragon 680 APK against the same
baseline route using p50/p95/p99 frame-time, cold-load spikes, memory/PSS
and logcat, plus visual tree/house/terrain comparisons before claiming a
real-device performance win.

Public references: Unity 6 asynchronous texture/mesh upload scheduling,
Android Dynamic Performance Framework, Unity 6 shader warm-up documentation.
