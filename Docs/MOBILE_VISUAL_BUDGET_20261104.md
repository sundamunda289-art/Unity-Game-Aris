# Android 2–3 GB visual-budget pass (2026-11-04)

## Scope and truth levels

This changes **saved source files** on a review branch. No Unity Editor import, game rendering, Android installation, GPU profiling or FPS measurements are asserted. Do not merge/deploy on the strength of the offline text checks alone.

Source of truth: `Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity`, the four native TerrainData assets, original source prefabs, URP assets and player scripts. This pass does **not** run the historical map rebuilding generators or rewrite existing `.meta` GUIDs. Exact 2 km × 2 km terrain layout, native instances, roads, house placement, vehicle markers, resident terrain/paint maps, and asset GUIDs are left alone.

## Core edits

1. **Cypress prefab proportions.** The root of `Cypress_Forest_Desktop.prefab` changes from (1.50, 1.50, 1.50) to (1.68, 1.42, 1.68) in X/Y/Z. The 18.3% higher X-to-Y ratio gives a wider/shorter base silhouette; the existing meshes, references, collider and four LOD thresholds are untouched. This is a source scale adjustment, **not** a verified measurement of visible trunk thickness or guaranteed terrain-instance appearance. Check nearby trees, foliage/wind, forest density, collider contact and billboard transitions in Unity. Original map reports say 679 cypress instances, but reports can become stale.

2. **Android-only memory-tier rendering.** `AarisMobileRuntime` selects a conservative profile using `SystemInfo.systemMemorySize`. Values from 1 through 2,500 MB use the 2-GB tier; 2,501 through 3,500 MB use the 3-GB tier. Unknown or larger RAM keeps the authored visual settings. Numbers below are **upper caps**, not a promise that this many megabytes are consumed or that these distances are always achieved. Existing lower values are preserved. Streaming caps only apply when the device supports mip streaming and the project's streaming switch is active.

| Setting | <=2,500 MB | 2,501–3,500 MB | Android low-memory callback |
| --- | ---: | ---: | ---: |
| Texture mip streaming budget, MiB | 192 | 256 | 128 |
| Max streamed mip level reduction, minimum | 3 | 2 | 4 |
| Shadow distance cap, m | 55 | 70 | 35 |
| Full-mesh tree billboard transition cap, m | 100 | 140 | 75 |
| Max full-LOD tree count cap | 96 | 144 | 64 |
| Terrain detail/grass distance cap, m | 45 | 60 | 35 |

**Far tree visibility is preserved:** `Terrain.treeDistance` is never changed. Existing distant tree billboards remain eligible to render. Faster billboard transitions may visibly pop, so compare close/far frames and wind animation before accepting. We deliberately avoid changing render resolution, HDR, materials, terrain paints, house meshes or the main URP pipeline. The callback applies the emergency settings once and still uses the existing cooldown-limited unload policy.

3. **Collider refresh.** `AarisNearbyColliders.Update` continues querying the existing 250 m spatial index; it no longer forces `Physics.SyncTransforms()` after a query that made no collider enable/disable changes. Explicit `RefreshNow()` (used after HOME teleport) **does** force the sync. Physics behavior must still be tested across cell boundaries, tower ramps and teleports.

4. **Diagnostics.** A saved JSON sample now reports mobile visual profile name, actual streaming budget and shadow distance with average FPS, p95 frametime, Unity allocations and Android PSS. These are measurements only after executing an APK on hardware.

## Known quality limitations and next high-impact work

- **Buildings/interiors:** The 17 authored building types include whole-model mesh colliders, so an apparent door/window does not automatically guarantee a passable opening. Do not cut door holes by scaling the whole house. Measure door/window mesh regions, rebuild the affected LOD0 geometry and collision explicitly, and run player collision and sightline probes. Typical initial authoring guidelines (not measured current values): door clearance roughly 0.9–1.2 m wide and 2.0–2.2 m tall for the 1.78 m player; intentional vaultable windows about 0.9–1.2 m above the floor if vault physics supports them; sealed windows should stay solid. Reserve accessible interiors for locations that can be tested.
- **House placement:** Existing saved-world report gives a minimum residential/other building gap about 21.5 m but a narrow vehicle-corridor gap near 0.26 m. A global 10% scale increase would risk clipping. Inspect all neighbouring foundation/road/vehicle collider envelopes before choosing buildings to resize, cluster or rotate.
- **Distant geometry:** Several very large original landmark/village building models retain high LOD2 triangle counts (e.g. reported Airfield LOD2 over 140k triangles, CanalTown LOD2 over 125k). Reauthor geometric impostors/combined lower LOD models after visual and material-slot testing. Do not merely reduce texture sharpness or set smaller draw distance to conceal inefficiency.
- **Rocks:** Original terrain rock prototypes and 24 new placed props use different mesh/collider paths. Inspect both before adjusting yaw, scale distribution, burial depth and gameplay cover. Missing rock collision must not be treated as safe path.
- **Large-world streaming:** Collider spatial indexing is not actual terrain/texture asset streaming. Four TerrainData tiles remain resident; unloading them requires scene/chunk assets and seam-preserving distant proxy geometry. On 2-GB phones, this could be more important than tiny per-frame optimizations.

## Validation and exit criteria

PR offline CI runs the existing saved-core checks plus:

    python Tools/Validation/check_mobile_visual_budget.py

The regression checker inspects source invariants and serialized prefab, not runtime results. At minimum, before merging:

1. Unity 6000.3.26f1 import/compile and Android build with materialized LFS assets; no missing references.
2. Test two 2-GB Android models and at least one 3-GB model with the actual APK. Record `buildGuid`, Android PSS, GPU/CPU frame-time, average FPS and p95 frame-time at the spawn, dense forest, the largest villages and inside/outside buildings.
3. For a 60-FPS **target**, p95 frame-time should normally stay under ~16.7 ms on target hardware; if not, establish a stable 30 FPS fallback (p95 under ~33.3 ms) before claiming smoothness. These are acceptance goals, not measured outcomes.
4. Compare before/after screenshots for cypress crown and trunk, tree billboards at 75/100/140 m, rock cover and landmark silhouettes. Check visibility and collision of doors/windows with a 1.78 m player; test HOME, forest roads and the four tower staircases.
5. Test pause/resume, screen rotation, low-memory events and at least 10 minutes of repeat travel. Read `AarisDiagnostics/current.jsonl` and Android logcat for crashes; the existing historical 10–15-second exit is not diagnosed here.

**Do not claim increased FPS or fully repaired door/window gameplay without these runtime checks.**
