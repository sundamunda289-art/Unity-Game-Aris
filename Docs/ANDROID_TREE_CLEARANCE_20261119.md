# Android native tree-clearance cache pacing — 2026-11-19

Target: Unity 6000.3.26f1, URP 17.3.0, 4 GB Android / Snapdragon 680 / Adreno 610.

## Source-level issue

`AarisSettlementExpansion.PlaceSitesAcrossFrames` read `TerrainData.treeInstances` for all four original terrain tiles. The Unity property produces a full managed `TreeInstance[]` per tile, retained while the initial 21 authored settlement sites and any deterministic interactive-house relocation candidates are validated. This is independent of terrain renderer's native tree inventory.

Unity 6 documents `TerrainData.GetTreeInstance(index)` as an alternative that does not create the whole tree array. It should reduce **temporary managed-array allocation**, but no Android heap/GC delta has been measured yet.

## Bounded implementation

- Read `treeInstanceCount` once per tile and use `GetTreeInstance(i)`; never build the four full `TreeInstance[]` arrays for the expansion.
- Cache exact world X, world Z and original clearance radius (three floats per tree); retain **every tree**, rather than pruning the forest or changing the original terrain instance data.
- On Android, yield after at most 256 tree records per frame. Desktop also uses the allocation-light path, without the mobile per-frame yields.
- Preserve terrain-tile seam checks, rotated house footprints, 26 m relocation offsets, old collision predicates and source asset precision.
- Clear the temporary cache when expansion finishes or its coroutine is cancelled.
- Do not edit TerrainData, meshes, tree prototypes, shaders, materials, fidelity, game AI or authored scene assets.

## Validation and limitations

Run `python3 -m unittest discover -s Tools/Validation -p 'test_settlement_expansion.py'` and `python3 -m unittest discover -s Tools/Validation -p 'test_android_tree_clearance_streaming.py'`.

Tests cover source/lifecycle limits and synthetic per-tree blocking parity across seams and relocation offsets. They are not equivalent to Unity C# compilation, a device profiler or measured Android ANR/crash regression. Long-running terrain scans may still take multiple frames; performance gains cannot be claimed until on-device profiling.

References: Unity 6 `TerrainData.GetTreeInstance` Scripting API and `TerrainData.treeInstances` Scripting API.
