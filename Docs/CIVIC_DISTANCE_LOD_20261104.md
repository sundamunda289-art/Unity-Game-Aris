# Civic silhouette distance LOD — scoped saved-core refinement (2026-11-04)

## Why these models, rather than blindly scaling the map

The source-audited `Docs/GROUND_REVIEW_REPORT.json` reports three civic kits with meaningful existing LOD2 geometry savings. The actual saved `TerrainDemoScene.unity` uses inline LODGroups, so changing a source prefab alone would not affect the map. These three scene groups and their exact reusable prefabs now share a conservative 0.22 / **0.095** / 0.0025 screen-height profile rather than 0.22 / **0.07** / 0.0025.

| Scene group / source prefab | Existing LOD1 triangles | Existing LOD2 triangles | Reduction while LOD2 is active |
| --- | ---: | ---: | ---: |
| `ClockTowerTown_TownHall_01` / `TownHall.prefab` | 17,925 | 4,431 | 75.3% |
| `EasternPort_Harbour_01` / `Harbour.prefab` | 23,527 | 2,714 | 88.5% |
| `FreightDistrict_ParcelDepot_01` / `ParcelDepot.prefab` | 6,632 | 2,916 | 56.0% |

**Interpretation:** When one of these models occupies 7–9.5% of screen height, its existing LOD2 mesh is used instead of LOD1. Percentage reductions refer only to triangles in the *individual model* at those distances, not whole-scene FPS, RAM, draw calls, battery life, or reduced asset residency. Animated cross-fading may temporarily render two LODs.

## Byte-preserving scope

- Edited **exactly three** `LODGroup` threshold records in the scene and the three matching actual reusable `.prefab` sources. No generators, postprocessor overlays, wrapper models, or runtime object cloning.
- Changed no object positions, rotations, scale, actual vertex buffers, materials, physics/collider geometry, doors, windows, player properties, grass, trees, roads, heightmaps, terrain instances or GUID/`.meta` references.
- Kept LOD0 close-detail threshold at 0.22 and final cutoff at 0.0025. Existing 47 residential and 32 rock groups, three heavy compounds and the remaining scene groups stay at their reviewed profiles.
- `Tools/World/verify_tree_house_upgrade.py` reverses only the three newly declared LOD1 scalars (plus prior approved LOD edits) in memory before comparing the full original scene and prefab SHA256 values against the protected postconditions; any unrelated change must fail.
- `Tools/World/tests/test_saved_scene_lod_integrity.py` checks the 101-scene-LOD distribution and each new source prefab's historical bytes.

## Design findings and deliberately deferred changes

The existing 32 authored RockCover props already use varied horizontal rotations, with 8 small companion stones; random reorientation without terrain normals or physics can unground their colliders. Residential scale was already adjusted relative to a 1.78 m review player; true sill heights, capsule traversability, window vault clearance, room dimensions, clustering and doorway sightlines still need real mesh/terrain inspection and Unity controller probes. Do **not** infer these from prefab root scale alone or enlarge all houses again. Six SpeedTree prefabs already have intermediate LOD optimization; unbaked proprietary SpeedTree source geometry requires a correct Unity importer/runner, not a fake binary edit.

## Verification and release gate

1. On the exact PR head run `python -m unittest discover -s Tools/World/tests -v`, `python Tools/World/check_contracts.py`, `python Tools/World/world.py verify --native`, `python Tools/World/world.py verify --project-checks`, and related GitHub CI with real Git LFS assets. Reject source drift.
2. Import and compile in **Unity 6000.3.26f1 / URP 17.3**, examine these three places in Editor and matched fixed-FOV captures with transition screen heights 12%, 9.5%, 8%, 6% and 3%. Check silhouette pop, crossfade dither, roofs and lighting.
3. Compare *identical routes* on real **2 GB and 3 GB Android** devices with p95 frame time (not average FPS alone), memory/PSS, texture residency, GPU/render thread time, low-memory events, thermal load, crashes and screenshots. Measure whether any scene-wide improvement is actually present.
4. If mobile visual fidelity worsens, restore just these three scalar LOD2 thresholds. Defer more invasive home/rock placement and any third-party imported models until terrain/collider/road clearances and licenses are verified.

This patch is **not** a Unity compilation, device FPS result, crash fix, 2-GB compatibility proof or finished map beauty pass. It is a small, explicit, reversible and source-audited performance hypothesis.

References:
- [Unity 6 LOD Group](https://docs.unity.com/en-us/engine/6000.0/manual/analysis/graphics-performance-profiling/simplifying-distant-meshes-with-level-of-detail-lod/class-lodgroup)
- [Unity 6 URP performance](https://docs.unity.com/en-us/engine/6000.5/manual/render-pipelines/universal-render-pipeline/introduction/configure-for-better-performance)
