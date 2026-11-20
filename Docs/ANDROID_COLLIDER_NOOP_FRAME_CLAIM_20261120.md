# Android collider refresh: claim native frame only on actual state transitions

Date: 2026-11-20. Scope: Unity 6000.3.26f1, budget Android GPUs, unchanged authored map.

## Source-level finding
`AarisNearbyColliders.RefreshNow(bool)` previously called
`AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame()` at the start
of *every* movement refresh, even when every nearby collider was already enabled
and no distant collider needed to be disabled in that call. The gate blocks
one optional model/asset admission in the reserved Android frame. This is a
source-level unnecessary scheduling stall risk during movement/idle checks,
not a measured CPU/GPU frame stall.

## Narrow fix
- Preserve unconditional native-frame reservation for `forcePhysicsSync`
  (teleport/startup), including original `Physics.SyncTransforms()`.
- For ordinary movement refresh, reserve the current frame **before** each
  actual `c.enabled = true` transition, and do not reserve on a read-only
  cached-bounds scan.
- Keep deferred distant collider deactivation / release coroutines and their
  existing per-frame native-work reservations unchanged.
- No changes to 55m activation, 6m hysteresis, 3m refresh movement, solid wall
  coverage, player collision authority, terrain/tree visuals, textures,
  model draw distance, pooling, batching, or source assets.
- Potential tradeoff: a read-only spatial grid scan can still overlap optional
  asset work on budget Android, even if it does not toggle a native collider.
  This is expected to be cheaper, but measure it with a Unity Profiler trace.

## Checks
`python3 Tools/Validation/test_android_collider_claim_on_change.py`,
`test_android_physics_streaming_frame_priority.py`,
`test_android_collider_grid_lifecycle.py`,
`test_android_collider_release_burst.py`,
`test_android_collider_wallclock_budget.py`,
`test_android_shared_streaming_frame_gate.py`.
The CI validates static C# syntax and source contracts. It does **not** prove
Unity editor compile, Android thermal stability, or FPS improvement.

## Device acceptance
Capture Snapdragon 680 / Adreno 610 traces while stationary, walking a
building boundary, teleporting, and rapidly returning to previously
deactivated geometry. Verify nearby cover remains solid and p95/p99 frame
times and newly visible models are not regressed. Undo if measured contention
or physics misses outweigh unnecessary admission deferral reductions.

Unity documentation: async asset integration is main-thread budgeted by
`Application.backgroundLoadingPriority`. Background assets and collider
work should be measured together, not assumed free.
