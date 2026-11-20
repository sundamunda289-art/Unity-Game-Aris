# Android opt-in performance telemetry shares the streaming frame budget — 2026-11-20

## Source-level issue
The on-demand `AarisMobilePerfOverlay` captures bounded per-frame intervals and
every second sorts the ring, queries optional profiler counters, formats labels
and may enqueue CSV output. Its snapshot used to be independent of the
`AarisMobileStreamingFrameGate` used by model imports, native object
construction and core Android runtime diagnostics. When diagnostics were
enabled during traversal, a report could therefore coincide with an expensive
native import or a cleanup frame.

## Scoped fix
After collecting the existing allocation-free frame interval, an Android
overlay/CSV snapshot must claim the existing nonessential diagnostic frame.
If busy, it retries on the next rendered frame without advancing its due
deadline. The frame sample continues; the report may be delayed and is not a
precisely fixed one-second telemetry interval during contention.

The overlay retains its opt-in controls, CSV schema, p50/p95/p99 arithmetic,
async writer, and bounded buffers. No new Update-time allocation, physics
query, asset, culling flag, world object, tree, vehicle placement, shader,
texture, shadow, render scale or quality setting is added or changed.

## Evidence and limitations
- Static regression: `python3 Tools/Validation/test_android_perf_overlay.py`.
- Existing targeted CI validates that script, C# syntax and whitespace.
- This is intentionally a **small opt-in diagnostic contention fix**, not a
  full dynamic chunk streaming, thermal/PSO/HLOD implementation or a measured
  gameplay FPS gain. It does not promise that a slow underlying shader/mesh
  upload can finish within a frame budget.
- Required follow-up before production certification: Unity 6000.3.26f1
  compile, Android development build, Snapdragon 680 before/after p95/p99
  and memory traces, and screenshots comparing native-resolution visuals.
- Do not reset/rebase/stash a shared dirty Codespace or claim it was synced
  merely because the canonical GitHub PR merged.

## Public engine references
- https://docs.unity3d.com/ja/current/ScriptReference/QualitySettings-asyncUploadTimeSlice.html
- https://docs.unity3d.com/ja/current/ScriptReference/QualitySettings-asyncUploadBufferSize.html
- https://developer.android.com/games/engines/unity/unity-adpf
