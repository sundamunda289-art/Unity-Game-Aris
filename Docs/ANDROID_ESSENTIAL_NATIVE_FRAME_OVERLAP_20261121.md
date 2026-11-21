# Android essential native frame overlap guard (2026-11-21)

## Source-audited issue

On constrained Android devices, `AarisMobileStreamingFrameGate` reserves the next rendered frame for essential player-rig and aircraft integration. A same-kind reservation made on that reserved frame previously replaced its frame number. For example, a rig reserve on frame 40 fenced frame 41, but another rig reserve on frame 41 replaced the fence with 42, leaving frame 41 eligible for an unrelated optional hierarchy/import. Separate rig/aircraft fields protected mixed-kind overlap, not same-kind overlap.

This is a scheduling hazard, **not measured proof** of the owner's multi-second freezes.

## Scoped change

Retain the previous and latest due frame separately for each of the two essential visual categories. Duplicate requests for the same target frame do not erase either slot. Existing optional-work claims, diagnostics and native asset imports see both protected frames through the pre-existing `IsEssentialRigFrame` check. No new collections, allocations, per-frame scans, loading threads, changed authored meshes, textures, shadows, terrain, movement, or original vegetation. Only constrained Android reserves these frames; Editor, desktop and fast Android devices keep their original policy.

The existing production C# admission fixture now exercises consecutive same-kind rig/aircraft reservations, duplicate same-frame calls, expiration, and high-end bypass. Existing cross-kind cases remain. CI runs the full production gate with mock Unity API under Android, Editor and desktop compile symbols and C# parsing.

## Acceptance limits

A pass in the mocked fixture is not a native Unity 6000.3.26f1 compile, Play Mode or real Snapdragon 680 improvement. On a running Editor, verify animation and aircraft imports. On-device collect the same cold/warm route, frame-time histogram, native memory, GPU/CPU timing, first-use hitches and Android logcat. No quantitative FPS improvement is claimed without comparable device measurements.

Official rendering context:
- https://docs.unity.com/en-us/engine/6000.3/manual/analysis/graphics-performance-profiling/reduce-draw-calls/optimizing-draw-calls-choose-method
- https://docs.unity.com/en-us/engine/6000.3/manual/analysis/graphics-performance-profiling/optimizing-graphics-performance
