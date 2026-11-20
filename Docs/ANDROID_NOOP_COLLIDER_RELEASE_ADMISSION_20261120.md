# Android deferred collider retirement: no-op frames must not starve streaming

Date: 2026-11-20. Unity 6000.3.26f1, URP 17.3.0.

## Source-level bottleneck

`AarisNearbyColliders.ReleaseDistantAcrossFrames()` preserves nearby physics
and slowly disables distant authored colliders in capped batches. The old coroutine
reserved `AarisMobileStreamingFrameGate` **unconditionally** after each yield,
even when every pending entry had since been reacquired or had moved back within
the 55 m activation + 6 m release hysteresis.

That reservation could block an otherwise eligible optional model/material
integration frame on low-RAM Android despite no actual PhysX collider change.
The specific handset freeze origin is **not** established by a static audit.

## Focused change

- Keep the original one-frame initial delay, 12-entry / 1 ms soft retirement
  batch budgets, queue ownership, live player-position check and safety margin.
- Reserve the shared physics admission frame **only before** the first actual
  `Collider.enabled = false` transition within each coroutine frame.
  A local stack Boolean avoids repeating the native scheduling check for other
  colliders in the same slice; reset it after each `yield return null`.
- When all queued retirements were reentered/stale/no-op, spend no optional
  model integration reservation. Essential colliders and explicit
  teleport/forced `Physics.SyncTransforms()` remain unchanged.
- Do not change any authored wall/collider shape, terrain, foliage, house,
  render scale, textures, camera, lighting, shader or streaming radius.

## Verification scope

- Source regression verifies the claim is inside the real state transition,
  pre-disable ordering, per-frame reset and original clearance behavior.
- The actual production C# iterator is compiled with Mono and run against
  deterministic Unity type stubs. Cases: 21/21 reacquired entries (no
  reservation), 20 real retirements across two bounded frames (one reservation
  each), and mixed stale/real entries.
- Existing broad collider and cross-streamer tests and full C# syntax parser
  run in GitHub Actions.
- These checks **do not** establish actual Unity Editor assembly compilation,
  Android player safety, measured Snapdragon 680 frame-time improvements, or
  device thermal performance. Confirm on a real phone with the current
  p95/p99 CSV overlay and CPU/physics streaming Profiler timeline.

Authoritative methodology:
- Unity 6 asynchronous upload and streaming budget guidance:
  https://docs.unity.com/en-us/engine/6000.3/manual/materials-and-shaders/textures/texture-loading/loading-textureand-mesh-data-introduction
- Android Dynamic Performance Framework and frame-time profiling:
  https://developer.android.com/games/optimize/adpf/best-practices-adpf

Current live Codespace has unrelated uncommitted multi-agent edits.
This change is authored directly through a separate GitHub PR. No new
worktree, reset, checkout, stash, or tracked/untracked workspace mutation.
