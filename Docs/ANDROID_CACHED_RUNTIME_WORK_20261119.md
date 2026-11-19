# Avoid repeated native runtime work — 19 November 2026

Source baseline: main `ad9775b`. This change addresses two verified source
patterns. Their share of frame time on the owner's handset remains unmeasured.

## Collider candidates: managed rejection before engine state

The existing 250 m spatial buckets can include many shapes that are far from
an individual player/placement/rival-cover probe. Previously, candidate loops
read Unity collider state before making their final cached-bounds decision.

The activation pass now rejects any cached bounds outside the existing 61 m
retention envelope before reading the collider's engine state. This is safe
for both enabled and disabled candidates: the 55 m entry radius is smaller
than that retention envelope. Near candidates still use the exact existing
entry/retention rule and read live `Collider.enabled`; no enabled-state cache
can go stale. Both the grid and oversized-structure fallback take this path.

Inactive-cover probes now test cached bounds intersection before reading
`Collider.enabled`. Deduplication, disabled-cover filtering, large bridge/wall
fallbacks, explicit teleport sync and native collision shapes remain intact.

## Zone boundary: redraw only when geometry changes

`DrawBoundary` runs on the existing two-second cadence. Before this change it
resampled terrain at 65 points and uploaded 65 individual LineRenderer points
even while a zone's center/radius had not changed.

The director now retains one 65-point array and remembers the renderer,
center and radius of its last completed upload. Unchanged geometry reuses the
existing line. A center/radius change or a replacement renderer rebuilds all
65 original points and submits them with one `SetPositions` call. Exact float
comparisons preserve even small real movement. Hidden/out-of-map rings stay
hidden; restoring a cached visible ring restores its enabled state.

This uses the project's existing static TerrainData assumption. Future runtime
terrain deformation would need explicit ring-cache invalidation; no such edit
path is introduced here. Original color, material, width, shadows, point count,
draw cadence, world heights, zone authority and damage rules are preserved.

## Executed evidence

The C# unit runner extracts the actual changed production loop bodies,
inactive-cover predicate and DrawBoundary method into controlled API fixtures.
It does not execute Unity Editor, PhysX or an Android player.

- **474,086 checks passed** across randomized bounds and collider states,
  destroyed/null objects, XZ versus elevated structures, exact 55/61 m edges,
  inactive-cover truth, ring coordinates/heights and redraw state transitions.
- **42,776 far activation candidates** required zero collider-enabled reads
  in the controlled backend.
- **1,000 stationary ring refreshes** caused no additional terrain sampling
  or point uploads after the first complete draw. Changes/replacement still
  caused full redraws, and tiny center changes were not suppressed.
- All 27 current gameplay CI source checks passed locally. Existing collider
  lifecycle/oversized-cover, settlement, loot-grid, vehicle and rival-distance
  checks passed. All 38 gameplay C# scripts parsed.

CI executes the same production-body tests with `mono-mcs`. Reproduce with:

```bash
python3 Tools/Validation/run_runtime_native_work_csharp.py
```

Alternatively set `AARIS_UNITY_EDITOR_DATA` to an installed Editor's `Data`
directory; the runner uses its standalone Mono toolchain, without launching
Unity. No APK build, module install, license operation, asset replacement or
render-quality reduction was performed for this work. Native scene behavior,
Android FPS and crash/freeze attribution still require real-device evidence.

Primary API reference: [Unity LineRenderer.SetPositions](https://docs.unity.com/en-us/engine/6000.0/script-reference/unityengine/linerenderer/setpositions).
