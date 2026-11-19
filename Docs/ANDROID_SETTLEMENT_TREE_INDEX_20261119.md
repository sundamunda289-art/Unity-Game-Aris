# Android settlement tree clearance: sparse spatial index (2026-11-19)

## Source-confirmed performance risk

AarisSettlementExpansion previously copied all four native TerrainData
treeInstance arrays to managed TreeInstance[] via the treeInstances property.
It retained those arrays during all 21 planned settlement site placements.
Each site and interactive relocation attempt scanned the arrays linearly.

This can create redundant cold-start memory pressure and CPU spikes. The
actual number of runtime trees, PSS allocation and relation to the owner's
3-30 second Android stalls remain **unmeasured on the handset**.

## Implementation

- Before scanning terrain, precompute 32m cells potentially needed for any
  planned building, including 26m relocation and the original 3.8m maximum
  tree-clearance margin.
- Use TerrainData.GetTreeInstance(index), not a managed treeInstances copy,
  scanning at most 1024 native tree records per Android frame. Desktop is
  kept unthrottled for Editor inspection.
- Retain only the world-position and clearance-margin records from those
  relevant cells, grouped by terrain and spatial cell. Building clearance
  checks examine nearby bins instead of searching the entire tile.
- Preserve exact yaw-derived footprint extents, all 4 terrain boundaries,
  tile-seam checks, site order, original tree/terrain visuals, and
  deterministic relocation and collider requirements.
- Clear temporary cells/indexes on completion, failure, or coroutine
  teardown; never modify native TerrainData or remove any trees.

## Tests and evidence limits

Run test_android_settlement_tree_index.py, test_settlement_expansion.py,
test_settlement_streamed_obstacles.py and check_mobile_hotpaths.py. Static
checks include deterministic brute-force versus sparse-index clearance
on randomized sites, negative coordinates, diagonal building yaw, tile
seams and 26m relocated sites.

These tests do not establish Unity C# compilation, native terrain import,
Android rendering or FPS. Next gate: real-device cold launch comparison:
PSS and GC allocation spikes, scene placement correctness, frame p95/p99,
and logcat ANR/LMK/native crash reports.

Official Android memory guidance:
https://developer.android.com/games/optimize/memory-monitoring
