# Android physics/optional model admission — 20 November 2026

## Source-level bottleneck and change

The collision-grid indexing, distant-collider deactivation and movement-driven
collider-release coroutines already have bounded work counts and wall-clock
limits. However their **native physics maintenance** was independent of the
shared optional model/import/diagnostics admission gate. Consequently another
streamer could initiate a model hierarchy on the same rendered frame as a
collider-maintenance batch; *each subsystem was individually budgeted* but the
aggregate work was not coordinated.

For constrained Android phones, `ReservePhysicsStreamingFrame()` reserves the
current shared slot for native collider maintenance. Each existing per-frame
index/deactivation/release coroutine reserves again immediately after a yield.
The live collision refresh reserves before touching collider state. Later
optional import/visual hierarchy/diagnostic requests now retry on a later
frame using their existing bounded retry paths. A competing operation that
already began earlier in the same frame cannot be undone; actual frame-order
coverage requires Unity Profiler verification.

## Invariants

- No authored collider was removed, simplified or altered. Activation radii,
  hysteresis, native obstacle bounds, near-player collision priority, streamed
  model content, prefab identities, terrain, trees, grass, lighting, shadow and
  URP settings are unchanged.
- No new per-frame collections, allocative LINQ, physics queries, or Unity
  instantiation are introduced. The extra admission signal is a constant-time
  conditional setting one static frame number on constrained Android devices.
- Normal desktop/editor and nonconstrained Android paths are unchanged.
- Delaying *optional* model work cannot guarantee hitch elimination: individual
  native operations, shader compilation, scene activation or system memory
  pressure can still exceed one frame, and earlier same-frame work may overlap.

## Validation

Run `python3 -m unittest Tools/Validation/test_android_physics_streaming_frame_priority.py`
and existing collider and shared-admission source tests. A real Unity 6000.3.26f1
compile plus Android physical-device before/after p50/p95/p99, frame pacing,
thermal and peak memory measurements are still necessary. These source checks
do not establish FPS or crash-free performance.
