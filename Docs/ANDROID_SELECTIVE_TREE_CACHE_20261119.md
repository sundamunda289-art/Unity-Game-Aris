# Android selective terrain tree clearance retention (2026-11-19)

## Observed source-level bottleneck

The Unity 6 settlement pipeline already scans native tree records at a
bounded 256 samples per Android frame via TerrainData.GetTreeInstance.
However, it previously retained **every tree** from all four 1km tiles
in temporary 3-float-per-tree managed lists. Each of up to 21 planned
settlement sites (including interactive relocation attempts) scanned
those lists to test footprint clearance.

This adds avoidable temporary RAM and repeat CPU scans for trees far
from any building. Actual tree counts, frame timings and Android PSS
remain unmeasured on the owner's handset.

## Narrow fix that preserves game assets

Before retaining compact clearance samples, compute 32m spatial
interest cells around every valid planned site. Include the original
rotation-aware footprint half extents, maximum tree margin (3.8m),
and, for interactive sites, maximum deterministic relocation (+/-26m
on either axis). This interest-cell set is an efficient filter, **not
a change to the rendered or collidable native forest**.

Every native tree record is still visited under the existing 256
samples-per-frame budget, but only potentially relevant records are
temporarily retained. The per-tile TreeClearanceSample list, the exact
TreesBlock comparison, collision semantics, tile-seam coverage, house
order, fallback and relocation behavior all remain the same.

Both the temporary samples and interest cells are cleared from the
coroutine's finally block on normal completion or cancellation.

No terrain tree, mesh, material, texture, map object or visual asset
is deleted, hidden, downscaled or altered.

## Static validation and limits

Run the parity test and established contracts:

- python3 Tools/Validation/test_android_selective_tree_retention.py
- python3 Tools/Validation/test_android_tree_clearance_streaming.py
- python3 Tools/Validation/test_settlement_expansion.py
- python3 Tools/Validation/test_settlement_streamed_obstacles.py
- python3 Tools/Validation/test_android_profiler_bottleneck_markers.py

Deterministic geometry compares the full original forest with
selective retention across four terrain tiles, negative positions,
45/90/135-degree footprints, seams, and all 18m/26m relocation
offsets. This is a mathematical and static source check only:
native Unity compilation, mobile frame time, actual memory savings
and Android freeze/crash attribution still require device evidence.
