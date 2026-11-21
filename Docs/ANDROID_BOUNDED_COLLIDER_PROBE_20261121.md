# Android bounded inactive-collider proximity probes — 2026-11-21

## Exact source-level risk

`AarisNearbyColliders.HasPotentialInactiveObstacle` is used by the existing parked vehicle, safe vehicle-exit, tactical bot and settlement placement checks. Original grid iteration enumerated every XZ cell from a rotated candidate AABB's min to max. Normal vehicle/capsule volumes cover a handful of cells, but a wrongly imported, oversized or corrupted probe could cover tens of thousands of cells per axis. The previous nested loops could become a main-thread stall even though collider indexing itself already limited oversized authored shapes to 64 cells.

This is a defensive hotpath fix; no real phone trace currently proves that an oversized probe caused the reported freezes.

## Change

- Compute query cell bounds once after an oriented box's conservative AABB is built.
- When the XZ footprint covers more than 64 cells, scan only the fixed existing authored `obstacles` array, using exact 3D `Bounds.Intersects`, existing streamed flags and native enabled state. Also catches original oversized-index entries, so no disabled cover is skipped.
- Reject nonfinite candidate bounds as unsafe rather than accidentally treating invalid geometry as empty; this cannot turn an unsafe spot into an approved vehicle exit.
- Small probes still use the original indexed grid, duplicate-visit stamps and oversized-index checks. No per-probe array, `HashSet`, GC allocation, scene change, native terrain/foliage toggle, texture reduction or new collider activation.
- `MaxCellsPerProbe` is a defensive *candidate scan work* threshold, not a new physics activation radius, render distance or building size limit.

## Checks

`Tools/Validation/run_bounded_collider_probe_csharp.py` injects the real production `HasPotentialInactiveObstacle` body and `Finite` helper into an isolated Mono C# fixture. The fixture checks 1,500 randomized rotated placement/exit probes and 150 giant-area cases against a complete authoritative collider scan, plus nonfinite and pre-index behavior. The CI workflow also checks existing spatial-index and collider lifecycle tests and C# parsing.

These are controlled C# API-stub tests and **not native Unity 6000.3 compilation**, physical Snapdragon 680 FPS measurement, or proof of a particular stall cause. Native acceptance still needs a legitimate Editor build, actual vehicle/rival placement, collision gaps, 1% lows and profiler frames. The currently active Codespace has unrelated dirty game edits, and this change was authored only through an isolated GitHub branch without a new worktree or touching its files.
