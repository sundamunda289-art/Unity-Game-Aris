# AI inspection and core editing

## Clearance-aware house enlargement and parking access (review PR #75)

When enlarging a residential home, do **not** edit `m_LocalScale` alone and assume its
neighbours will move. Run the deterministic source simulation first:

```bash
python Tools/World/smart_residential_clearance.py
```

Additionally check the **entire projected car body**, rather than just the
spawn-marker centre, along every straight spawn-to-road route:

```bash
python Tools/World/vehicle_swept_clearance.py
```

This read-only conservative XZ sweep uses a **configurable example car** 2.2 m
wide × 5.0 m long, plus 0.5 m clearance on every edge. It expands the
hypothetical vehicle footprint along all 45 saved straight-line routes and
checks against all 59 current building AABBs; intersections fail closed.
`--vehicle-width-m`, `--vehicle-length-m`, and `--vehicle-margin-m` allow
other trial sizes. For a future proposed house-size change, pass the **same**
`--house-scales-json` to both planners. These dimensions are test assumptions,
**not** measured vehicle prefab/WheelCollider data. Actual car turning radius,
steering, gearbox, chassis mesh collision, ground contact, obstruction shape,
terrain collider and real Android driving remain engine-level merge blockers.

A new **read-only Unity Editor physics probe** can be run in the **actual reviewed
PR #75 scene after a fully hydrated native Unity import** from the menu
`AarisEngine > QA > Read-Only Garage Physics Probe` (or via Unity's approved
`-executeMethod Aaris.WorldAutomation.AarisResidentialRoutePhysicsAudit.Run`).
It confirms the 45 scene markers and 17 saved translations match the reviewed
manifest, samples terrain at 0.8-m steps on the 17 garage-to-road links and
queries enabled physics Colliders via `Physics.OverlapBoxNonAlloc` using a
**hypothetical** 2.2 × 5.0 × 1.8-m car envelope (plus a 0.5-m side/length
margin and a 0.15-m ground gap). It prints an
`AARIS_EDITOR_GARAGE_ROUTE_PHYSICS_AUDIT=` JSON result to the Unity log and
fails on obstacles/terrain offset. It never saves/opens scenes, creates cars,
updates .meta, or touches original project files. **Adding this test is not
proof that it ran**: an actual Editor import/execute/readback and real vehicle
Prefab, wheels, steering sweeps and Android gameplay validation remain required.

To preview a **future single-house resize** without editing the project, provide a
local JSON file containing exact GameObject-name keys and **absolute target
multipliers relative to the historical `Docs/MAP_LAYOUT.json`**, for example:

```json
{"ClockTowerTown_garage_apartment_06": 1.15}
```

Then run:

```bash
python Tools/World/smart_residential_clearance.py --house-scales-json my-house-scales.json
```

This checks all **45 reserved vehicle-spawn bays** against **59 building/landmark
AABBs**, all pairwise 6x12-m parking-pad envelopes, and the fixed road exit
of each of the garage-served access routes. Existing conflicts fail closed;
after enlargement the previous clearance is preserved for close pairs and
all previously more-distant pairs retain at least 2 m of horizontal
clearance. When the straight garage access cannot be maintained or the
remaining route to the exit becomes shorter than 5 m, the planner rejects
the proposed change. It does not blindly teleport the house, roads or cars.

In review PR #75, 47 homes and their 47 flat foundations expand together.
Exactly **17** garage-served `VehicleSpawnPoint_XX` marker positions and their
**17 matching `VehicleSpawnPoint_XX_Apron` scene transforms** translate
roadward by approximately **0.703–1.475 m**; the other 28 markers and all
road-exit locations remain fixed. Apron meshes use world-space vertices:
their local translation is intentional. Exact-byte restoration of the
historical scene and strict reviewed manifest checks forbid partial
marker/apron changes, other object mutations, altered .meta GUIDs or
unreviewed TerrainData modifications.

If the four native TerrainData files are fully hydrated from Git LFS, run:

```bash
python Tools/World/smart_residential_clearance.py --native
```

This additionally reads the real native rock/tree/shrub **instance centers**,
checks 3-m vicinity of the enlarged house envelopes, reports centers in
the 17 affected garage corridors, and samples genuine native terrain
heights/slopes across a 4.8-m-wide roadward strip. Missing LFS must fail:
a pointer is not valid native geometry. The initial inspected source had
**5,564 rocks, 1,939 trees and 5 shrubs**, with no native instance center
within 3 m of any enlarged residential AABB. This is not a statement
about the objects' full physical collider extents.

**The simulation is not Unity vehicle physics.** Current
`Docs/MAP_LAYOUT.json` describes empty vehicle markers rather than
fully spawned drivable cars. Before approving a change, open the **exact
reviewed scene commit** in an isolated matching Unity Editor, verify
import/compilation, terrain-footprint support, all relevant mesh/collider
radii, actual vehicle prefab and navigation path from spawn to road,
entrance walkability, and before/after screenshot views. If a native rock
must move, first measure its actual bounds and terrain support, then use
a version-pinned native Unity operation with LFS readback and a separate
review PR. Never assume that moving the tree/rock pivot guarantees
clearance; never reset the owner's dirty Codespace checkout. Keep PR #75
as a draft until visual/physics QA is complete.


The tree/house migration in `Tools/World/house-openings-20261103.json` and `tree-proportions-20261103.json` authors actual saved mesh/importer/prefab core files. `apply_tree_house_upgrade.py` previews the hash-bound request, compares it to the authored target contract, writes the source, and rolls back on a failed source check. It never merges a PR or builds/deploys an APK. The dedicated task workflow pushes only the enumerated core assets to its own branch after native/source checks.

Renderer slots now include `submesh_geometry` with the exact saved index/triangle count where decoded. An empty slot can retain a material called door or glass; it is marked `empty` and excluded from live surface-role counts. `site`, `describe` and edit observation deltas carry that distinction. Unknown/imported geometry remains unavailable rather than empty. Unity-backed edits stage typed Python mesh edits in their disposable project before launching the Editor.

Seven residential model types (47 saved instances) share 21 close/medium/far mesh assets. Inspected door panels, window glazing, small brass hardware and one non-structural utility box obstructing a rear doorway are removed from the actual triangle buffers; structural oak, walls, floors, roofs, UVs, normals and material slots survive. Renderers and existing non-convex colliders retain the same mesh GUID. `verify_tree_house_upgrade.py` checks 20 doorway envelopes with 900 discrete rays across all LODs. These are source samples, not Unity CharacterController results or proof of every interior route.

`remove_mesh_triangles` accepts `{id, op, path, file_id, selections: [{submesh, triangles: [original_triangle_indices]}]}` in schema-v2 transactions. It requires current asset/metadata hashes, rejects unsupported compressed/streamed/cooked layouts and preserves empty material slots after clearing their faces. Shared impact is returned in the plan. `asset` can read the saved buffers; the AI must supply the intended face selection rather than matching a generic material name and deleting everything using it.

Tree source profiles shorten the six inspected species by 18–38%, widen the base by 40–55%, and widen the crown by 10–12%. The original instance variation and all native records remain. `tree_shapes.py` authors the existing source import scales, tagged taper profile, prototype collider dimensions and probe anchors. The Editor postprocessor applies the same taper to positions, SpeedTree 8 next-LOD positions and leaf-facing/wind anchors, and transforms normals/tangents with the deformation. Texture UV0 and packed wind weights survive. The position-bearing attribute contract follows Unity Graphics' `ShaderGraphLibrary/Nature/SpeedTreeCommon.hlsl`, `SpeedTree8Wind.hlsl` and URP `Shaders/Nature/SpeedTree8Passes.hlsl`. This requires a Unity import to take effect; source verification does not establish wind, billboard or rendered quality.

The historical world review keeps its original close-mesh hashes. Its native preservation check accepts only the exact declared before/after transition for this migration and the unchanged vertex-buffer fingerprint; unrelated mesh drift still fails. No historical report is rewritten to disguise a changed asset.

This is a source inspection and controlled authoring toolkit, not a replacement game engine. Python can inspect saved Unity YAML, resolve GUIDs and hierarchy transforms, decode the native TerrainData, prepare precise patches and run verification. Actual collision movement, imported prefab resolution, URP rendering and native terrain writes use the matching Unity Editor through `WorldBridge.cs` in a temporary project copy. No map layout or graphic-quality change is applied merely by installing this toolkit.

## Python-only static props: AI decides, Python measures and writes

The static-prop path needs Python and materialized source assets, with no Unity Editor or Codespace. The existing optional engine bridge remains useful for rendering/physics, but is not used to import or place these supported static props.

1. AI reads current `context`, `inspect`, `height`, `region` and object records. Sensors report facts; they do not recommend or choose a location.
2. `import-model` converts a supported static FBX/PBR ZIP into native mesh, URP material, textures and explicit YAML prefab. Run without `--write` first. Supply the actual archive SHA256 and explicit source-unit-to-metres scale. The current importer supports one mesh/material and four named PBR texture maps. `--freeze-static-pose` explicitly discards any animation and imports default geometry only.
3. AI supplies a schema-v2 `place_static_prefab` operation with `prefab_path`, `name`, `world_position`, `yaw_degrees` and `local_scale`. Optional `parent` is a stable scene GameObject ID. Optional `collider_registry_file_id` explicitly adds copied colliders to the existing AarisNearbyColliders registry. Obtain expected hashes for the scene, prefab, full asset dependency closure and their metadata.
4. `plan` and `preview --backend python` return new resolved IDs and post-edit measurements. Preview never alters source. AI reads those facts, adjusts its request if needed, then runs `apply --backend python --write`.
5. Apply writes actual saved scene objects with new fileIDs and existing mesh/material references. The copy has no connected PrefabInstance. Existing object IDs, metadata and untouched documents stay intact. Source/hash/serialization failures reject the transaction or roll it back. Placement taste/suitability is always the AI's decision.

```bash
python Tools/World/world.py import-model --source /path/model.zip --source-sha256 <actual-sha256> --destination Assets/WorldImports/MyProp --unit-scale 0.01 --texture-size 2048
# Inspect the returned geometry/units; repeat with --write to create the new assets.
```

A placement operation is:

```json
{"id":"observatory-dish","op":"place_static_prefab","prefab_path":"Assets/WorldImports/MyProp/MyProp.prefab","name":"ObservatoryDish","world_position":[575,23.7588,-720],"yaw_degrees":145,"local_scale":[8,8,8]}
```

These example coordinates are not a location recommendation. Measure the current terrain and neighbours first. `static_prefabs.dependencies(World(root), prefab_path)` lists asset paths whose current hashes and metadata must appear in `expected_files`.

The satellite integration is saved under `Assets/WorldImports/SatelliteDish` and three inline scene objects: `SatelliteDish_HighlandObservatory`, `SatelliteDish_SouthernAirfield`, and `SatelliteDish_ForestLookout`. They share one 4,000-vertex/5,538-triangle mesh and PBR material; their colliders are explicitly in the existing nearby-collider registry. The source archive contains an animation stack, so this import deliberately uses the authored default static pose. It does not animate. Run `python Tools/World/check_satellite.py --native` to recheck saved geometry, identity, terrain footprint samples and neighbourhood coverage. Missing surrounding LFS meshes are reported as unavailable, not clear space. The source check is not a Unity import/render/physics test.

Context reports target-relative right/up/forward vectors, orientation, scale, labelled mesh/collider AABBs, support gaps, native tree/rock/shrub records and collider-based diameter estimates where original prefab data exists. Nine footprint samples describe terrain variation beneath a model's bounds. Native detail patch occupancy describes grass/ground-detail density; it does not invent individual grass positions. Missing geometry stays explicitly unavailable. Neither a complete-looking JSON nor a successful source write proves a physics collision, rendered pixel, visible trunk thickness, exact contact mesh, performance result or global suitability.

## Start locally

Use Python 3.11 or later in a virtual environment:

```bash
git clone https://github.com/Aaris-khan-don/Unity-Game-Aris.git
cd Unity-Game-Aris
python -m venv .venv
. .venv/bin/activate
python -m pip install -r Tools/World/requirements.txt
python Tools/World/world.py schema
python Tools/World/world.py context
```

`context` reports what is available, the scene GUID/hash, engine prerequisites, commands and limitations. For full engine work run `git lfs pull`; allow enough disk for both the full project and an imported disposable copy. For a read query, the selective fetcher avoids downloading the entire archive:

```bash
export WORLD_REQUEST='{"command":"scan","search":"Garage","details":true,"limit":5}'
python Tools/World/fetch_assets.py
python Tools/World/world.py request
```

The fetcher downloads actual terrain assets for terrain queries and referenced mesh assets for detailed object queries. It limits each request to 1GiB of advertised LFS bytes. Narrow an oversized query. Source textures are fetched with an explicit `asset` request rather than silently downloading every texture. GitHub/LFS access, quotas and network availability still apply.

Geometry-enabled `inspect` also fetches the scene's referenced shape geometry before measuring neighbours: a large building can intersect the query area even when its pivot is far away. This remains subject to the same 1GiB budget and does not download all material textures. A geometry-disabled inspection reports pivots only and must not be interpreted as complete obstacle coverage.

## Read the world

| Need | Command after `python Tools/World/world.py` |
|---|---|
| Object index, terrain size/counts/hashes | `scan --limit 100` |
| Names matching Garage, bounds and materials | `scan --search Garage --details --limit 5` |
| Continue a page | `scan --offset 100 --limit 100` |
| One object, hierarchy, components, materials | `object --target scene:<scene-guid>:<game-object-fileID>` |
| AI-friendly coordinate site capsule with appearance, semantic directions and completeness | `site --x 350 --z 167.3 --radius 50 --limit 100` |\n| One target plus terrain/support/nearby scene and native context | `inspect --target scene:<scene-guid>:<game-object-fileID> --radius 50` |
| Coordinate neighbourhood | `inspect --x 350 --y 18.4 --z 167.3 --radius 50` |
| Continue dense scene/native neighbourhood pages | `inspect --x 350 --z 167.3 --radius 60 --scene-offset 100 --native-offset 100 --limit 100` |
| House/prop dimensions, parts, semantic roles, material slots and dependencies | `describe --target scene:<scene-guid>:<game-object-fileID> --texture-analysis --limit 100` |
| Ground height, gradient, slope, normal | `height --x -496 --z -212` |
| Grid over a selected region | `region --xmin -510 --zmin -220 --xmax -490 --zmax -200 --step 2` |
| Native trees, rocks or shrubs in an area | `instances --category tree --xmin -600 --zmin -300 --xmax -400 --zmax -100 --limit 100` |
| List serialized objects in a binary/YAML file | `asset --path Assets/.../Terrain_1_1_....asset` |
| Read a particular native field | `asset --path Assets/.../Terrain_1_1_....asset --file-id 15600000 --field m_Heightmap.m_Heights --offset 1000 --limit 100` |
| Material properties | `asset --path Assets/.../Wall.mat --file-id 2100000 --field m_SavedProperties` |
| Source code lines | `asset --path Assets/AarisWorld/Scripts/AarisNearbyColliders.cs --file-id 0 --field lines --limit 100` |
| Shared text-serialized references to an asset | `usages --path Assets/.../Wall.mat` |
| Static loading/LOD/culling/frame/memory evidence | `runtime --search LoadScene --limit 100` |
| Actual phone measurements | `performance --report /path/to/current.jsonl` |

Replace placeholder asset paths with a real path returned by `context`, `object` or `asset`. JSON requests use the same command names and underscore keys (`file_id`, not `file-id`). Output includes timestamps, the actual files read and SHA256 fingerprints. Re-query after a source change. `--root <project>` and `--out <file>` are global options and go before the command.

The scene is currently a four-tile 2km by 2km world. Each native terrain has a 2049 by 2049 height grid. The reader derives spacing/size/origin from the asset and scene rather than applying a single map-wide Y offset. This project's stored integer normalization is `/32766`; unsupported encodings/transforms fail explicitly. At a tile boundary `height` returns both sides and their difference. Bilinear height/slope are numerical estimates of the stored grid, not measurements of a roof, building floor or Unity's rendered triangle. For exact engine terrain values and collider movement, use the engine bridge.

`scan` indexes saved inline GameObjects and pages results to keep AI context usable. `instances` covers the native vegetation/rock database. `inspect` is the normal AI context capsule: target/point, current terrain relation, nearby scene objects, nearby native instances, support/overlap candidates, coverage and source fingerprints. Scene and native candidate lists have independent offsets and return their next offsets; a first page is never evidence that the rest of the radius is empty. Native records include saved tint and lightmap tint. It reports facts and evidence levels; it never decides that an artistic placement is right or wrong. `asset` exposes other serialized fields without dumping a 53MB binary as an enormous JSON message. Arrays are paginated; dictionaries are compact summaries with keys and samples. Unknown/importer-only data is reported as unavailable and requires Unity.

`describe` is the targeted structure/appearance command. It walks the current saved hierarchy, reports overall height/footprint/AABB volume, pages every descendant, binds each material slot to its renderer and mesh reference, exposes attached MonoBehaviour fields and bounded static C# class/method symbols, and follows a bounded GUID dependency graph. Semantic roles come from saved names/material labels first and conservative geometry second. Material slots are classified separately into structural surface evidence (for example an explicitly labelled door or floor) and material-family evidence (for example timber, glass, steel or stone); the object's generic house name is not allowed to overwrite the more specific slot label. A labelled `FrontDoor` can be reported as a door with high confidence; an unnamed thin panel remains `door_or_window_panel` until stronger evidence exists, and `glass` alone remains `window_or_glass_surface`. `--texture-analysis` computes bounded mean/median/variation and dominant swatches from materialized source raster pixels so stains/color variation are visible to the AI, but it does not apply UVs, shader math, lighting or post-processing. Native TerrainData packed Color32 tint and lightmap tint are decoded to both saved RGBA bytes and normalized values. Static symbols do not execute runtime branches; use probes/tests for behavior and `capture` for final visible pixels.

Stripped imported/nested prefab documents no longer crash the scene index. The reader reports each serialized PrefabInstance source, overrides and a composed best-available transform. A targeted `describe` resolves materialized YAML prefab hierarchy or bounded FBX source mesh/AABB evidence. Unity's ModelImporter can still change units, mesh splitting and materials, so imported source evidence is kept distinct from an engine export. Missing LFS bytes remain explicitly unavailable and are never treated as empty space.

Object bounds are transformed mesh AABBs, including descendants/LODs. Missing LFS meshes produce `bounds_incomplete`, not invented dimensions. Texture files can be queried for image dimensions/format. Serialized `_BaseColor` or `_Color` often is white while the texture supplies the visible wall color. A screenshot from the real URP pipeline is the appropriate evidence for visible colors, lighting and silhouettes.

The source reader also resolves Unity's built-in Cube, Sphere, Capsule, Cylinder, Plane and Quad mesh envelopes using their saved mesh IDs. These shapes need no LFS download. Parent rotation and scale apply to their bounds just as for saved meshes. Unknown built-in IDs remain explicitly unavailable; a missing shape is never silently counted as clear space. Built-in mesh envelopes, including those referenced by MeshColliders, remain AABB estimates rather than physics tests.

## Edit the actual core files

`set_prefab_lod_thresholds` is a schema-v2 Python operation on an explicit saved prefab `LODGroup`: supply `path`, `file_id` and the complete `screen_relative_heights` list. Include current prefab and `.meta` hashes. The same 2..8 LOD count, strictly decreasing finite values in (0,1], and unchanged final culling threshold are required. Only changed scalar tokens are written; renderer sets, meshes, fade settings, bounds, colliders, IDs and all other bytes stay intact. `plan`, isolated `preview`, then `apply --backend auto --write` return exact before/after thresholds and shared text usages. Native TerrainData can reference a prototype without a YAML occurrence, so text usages are not complete native usage coverage. See `Docs/INTEGRATED_MOBILE_WORLD_20261104.md` for the six reviewed SpeedTree transitions and remaining device QA.

Use `Tools/World/patch.schema.json` and schema v2. Obtain hashes from current files (for example `sha256sum <path>`), not old reports. Include both each target file and its existing `.meta`:

```json
{
  "schema_version": 2,
  "transaction_id": "map-roof-cover-20261103-01",
  "idempotency_key": "map-roof-cover-20261103-01",
  "reason": "Adjust this house after checking its road access and foundation",
  "expected_files": {
    "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity": "<current SHA256>",
    "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity.meta": "<current SHA256>"
  },
  "operations": [{
    "id": "move-selected-rock",
    "op": "set_transform",
    "target": "scene:<scene-guid>:<game-object-fileID>",
    "world_position": [350, 18.4, 167.3]
  }],
  "observe_after": [{
    "target": "scene:<scene-guid>:<game-object-fileID>",
    "radius": 40,
    "native": true,
    "geometry": true
  }]
}
```

`world_position` is converted through the saved parent matrix; `local_position` remains available when the AI intentionally wants Inspector-local coordinates. Other transform fields are normalized `local_rotation_xyzw` and positive `local_scale` (up to 100 per axis). YAML operations also include `set_object_state`, `set_material_color` and `set_material_float`. A shared material edit affects all its users; query `usages` first.

The Unity backend adds typed native operations: terrain height brush; native tree/rock/shrub move, scale, rotation, add and remove; prefab instantiate; scene object/prefab-instance duplicate and delete. Native instance targets are bound to the current TerrainData hash plus their inspected prototype, position, scale and rotation. Unity rejects a changed instance instead of editing whatever later occupies the same array index. Native instances remain terrain-supported; create a scene/prefab prop for a roof or bridge placement.

```bash
python Tools/World/world.py plan --patch change.json
python Tools/World/world.py preview --patch change.json --backend auto
python Tools/World/world.py apply --patch change.json --backend auto --write
python Tools/World/world.py verify --native
python Tools/World/world.py verify --project-checks
```

`plan` returns concrete diffs, native operations, scopes and impacts without writing. `preview` applies the transaction to an isolated hard-linked project (or disposable Unity import), then returns post-edit context while leaving source bytes unchanged. `apply --write` rechecks every hash immediately before copying back. Python preserves all untouched documents, fileIDs and `.meta` files and restores original bytes if post-write structural validation fails. Unity returns changed asset paths and old/new stable identities. Work in a branch and check the Git diff.

Python plans also collect automatic pre-edit operation state and neighbourhood observations. Preview/apply returns `operation_before`, `operation_after`, `operation_deltas`, `pre_edit_observations`, `post_edit_observations`, `observation_deltas`, and exact `written_file_changes` with old/new hashes and byte sizes. Default observations include native neighbours, material evidence and confidence-scored semantics. Position/size deltas plus added/removed neighbour IDs make a moved or resized house auditable without pretending that an AABB is a rendered/physics result. Unity-native transactions keep their engine evidence path; do not claim the Python report covers an Editor-only operation.

Support distance, terrain gap, AABB overlap and authored-boundary relationships are observations, not design vetoes. If the AI intentionally accepts one, record its current observation ID/hash/reason in `acknowledgements`. Acknowledgements cannot bypass stale hashes, invalid GUID/fileID, non-finite values, missing LFS bytes, import/save/compile failures or corrupted serialization.

`terrain_brush` is a bounded, smooth vertical delta (radius 0..100m, delta up to +/-20m). Supply `center_xz`, `radius_m`, `delta_m`, plus expected hashes for the saved scene, all four native terrains, and all five `.meta` files. Then use:

```bash
export UNITY_EDITOR_PATH=/absolute/path/to/Unity
python Tools/World/world.py apply --patch terrain-change.json --backend unity --write
```

The launcher applies YAML edits to a disposable copy, runs Unity `TerrainData.GetHeights/SetHeights`, lets Unity recalculate native LOD/vegetation information, saves native assets, checks seams and current original hashes, then copies back only allowed core files. All tiles touched by a brush use the same world-space falloff. A missing editor/license, incomplete checkout, engine error, stale hash, invalid brush or seam over 5cm prevents copying back. Python does not hex-edit the serialized terrain. Saving may upgrade the terrain's serialization version to the project's Unity version.

Terrain changes can bury roads, float buildings, shift trees or expose the sea edge. The adapter does not redesign foundations or relocate vegetation for you. The AI must inspect these impacts and implement/review the needed core changes. Historical authoring reports/layout JSON are not silently regenerated; project checks may correctly reject a change that makes them stale. This is deliberate evidence, not a reason to rewrite a report to claim success. New operation types or C# behavior can be added as source changes with appropriate tests; the adapter does not promise safe arbitrary field/binary mutations.

## Temporary walking probes and real captures

These require a complete Git checkout with all LFS files plus installed/licensed **Unity 6000.3.26f1**, matching `ProjectVersion.txt`. The supplied cloud workspace has no Unity Editor; Python/static verification here does not establish engine compilation or a successful walking/render test.

Save a request JSON with either explicit walking waypoints (world-space foot positions) or a stable target object ID for four automatic candidates:

```json
{"command":"probe","target":"scene:<scene-guid>:<house-fileID>","maxSteps":10000}
```

```json
{"command":"probe","waypoints":[{"x":350,"y":-11.8,"z":155},{"x":350,"y":-11.8,"z":160}],"maxSteps":4000}
```

Up to 32 independent route bots can run in one imported scene:

```json
{
  "command": "probe",
  "probes": [
    {"id":"front-door","waypoints":[{"x":350,"y":-11.8,"z":155},{"x":350,"y":-11.8,"z":160}],"maxSteps":4000},
    {"id":"east-side","target":"scene:<scene-guid>:<house-fileID>","maxSteps":10000}
  ]
}
```

```bash
python Tools/World/world.py engine --request route.json
```

The probe uses the player's 1.78m height, .28m radius, .30m step and 46-degree slope limit with controlled walking/gravity. It temporarily enables saved environment colliders (including those initially disabled by distance gating), disables original CharacterControllers, syncs physics, and removes/restores everything in `finally` without saving the probe scene. Multi-probe bots run independently for deterministic evidence; they do not plan routes or simulate combat AI. Reports include start/target/reached positions, terrain height/slope/normal, route status, and nearby capsule-sweep contact candidates with object IDs/bounds. A collider candidate is not proof of the sole blocker. Colliders absent from the original asset are not invented.

For exact post-placement support/collider context without walking, send `{"command":"observe","observations":[{"target":"scene:<scene-guid>:<fileID>","radius":40,"limit":100}]}`. Unity temporarily activates saved environment colliders, measures terrain and a downward support ray, reports nearby colliders, then restores the scene without saving unless this observation is part of an explicit apply/preview transaction.

Automatic attempts start just outside the renderer AABB on four sides and aim at its center on the terrain. They are geometry hypotheses: a center may be an exterior courtyard or solid volume rather than a room. A blocked straight candidate does not prove a house has no entrance. For an actual entrance/interior test, inspect the model and supply the doorway/stair/interior waypoints. No bot decides to alter a wall automatically. Repeat selected probes after the AI makes a reviewed change; the total step budget bounds each request.

`{"command":"export"}` returns engine-resolved objects, world positions, bounds and components. For an actual URP view:

```json
{"command":"capture","cameraPosition":{"x":350,"y":30,"z":120},"cameraTarget":{"x":350,"y":0,"z":170}}
```

```bash
python Tools/World/world.py engine --request capture.json --capture-dir /path/to/temporary-output
```

Capture needs a graphics-capable runner/display. It uses a URP single-camera render request and returns a real 1280 by 720 image; a null graphics device fails instead of fabricating a preview. Dispose of explicitly retained capture files when finished. None of these Editor results measures Android FPS or native crash stability.

## GitHub execution

Opening a file or attaching a GitHub connector does not run Python. A permitted AI/terminal can dispatch Actions, wait, then read/download the report:

```bash
gh workflow run world-query.yml --ref main -f request_json='{"command":"height","x":-496,"z":-212}' -f ttl_hours=2
gh run list --workflow world-query.yml --limit 3
gh run watch <run-id> --exit-status
gh run download <run-id> --dir /path/to/temporary-output
```

Use the returned run ID, not an unqualified "latest" when multiple AIs are querying. Python-only queries work on GitHub's hosted Linux runner; they need Actions enabled and available LFS quota. The current ChatGPT GitHub connector supports repository reads/writes but has no workflow-dispatch endpoint; use Actions UI or an authenticated terminal/API with Actions write permission when dispatch is required.

For engine queries, register a self-hosted Linux runner with sufficient RAM/disk, a matching licensed editor, Python/git-lfs and a display/GPU for captures. Set repository variables `WORLD_UNITY_RUNNER` to its runner label and `UNITY_EDITOR_PATH` to the executable. Dispatch `world-engine-query.yml` with the same request JSON and TTL. When the runner variable is missing, the engine job is skipped and the availability job explains why; it is not a successful engine test. Concurrency prevents simultaneous automation on that runner. Each invocation imports a disposable project; this is heavier than a cached editor and is not an instant service.

`world-core-edit.yml` takes `patch_json` and `backend=python|unity`, checks out current main, enforces hash preconditions, applies actual core changes, runs saved-file checks and pushes a new `ai/world-edit-...` branch/PR. It does not auto-merge. Actions needs repository permission to create PRs; if that setting blocks PR creation, the verified branch remains available and the error names it. Events created using `GITHUB_TOKEN` may not trigger another workflow, so the edit job runs the project checks itself. Native edits require the configured Unity runner; an unavailable runner skips that job. Normal scan/query jobs have read-only repository permission; only the explicit edit workflow has source-write permission.

## Retention and diagnostics

Default local queries print JSON to stdout and leave no report file/cache or background service. Python/Unity temporary directories are removed on normal return, errors and handled cancellation; hard power/process kills cannot guarantee a cleanup callback. On the engine query runner the temporary project is under the job's temporary output directory and an `always` step removes it. Source scripts/instructions remain in Git for reuse. Caller-selected `--out`/capture outputs are retained by the caller; remove them when done. Git does not delete downloaded copies or content pasted into chats.

GitHub query artifacts have a one-day retention fallback. `world-query-cleanup.yml` runs hourly and deletes only completed manual runs of `world-query.yml` and `world-engine-query.yml` whose exact TTL title has expired (choices 1/2/6/24h, default 2h). Run deletion removes their query logs/artifacts too. Effective deletion is TTL plus the delay until the next successful cleanup; schedules can be delayed/disabled and are not an exact timer. Source, releases, build deliverables, edit PRs and ordinary verification history are outside this cleanup's fixed scope. Do not rely on deletion as permission to log credentials.

Android diagnostics now include `Application.buildGUID`. Future builds write `<apk>.world-build.json` (or `world-build.json` in a directory build) linking BuildReport GUID, commit and selected core hashes. This build provenance stays with the build. `performance` reads actual device JSONL; it does not estimate performance from map geometry. Managed error logs alone cannot prove the cause of a native crash, GPU fault or Android memory kill—collect logcat/exit reason and correlate the exact build.

## Verify the toolkit and a source change

```bash
python -m pip install tree-sitter==0.26.0 tree-sitter-c-sharp==0.23.5
python -m unittest discover -s Tools/World/tests -v
python Tools/World/check_contracts.py
python Tools/World/world.py verify --native
python Tools/World/world.py verify --project-checks
```

Tests cover parent/world transforms, schema-v2 transaction identities, isolated preview, neighbourhood context, reverse usages, runtime evidence, precise height/slope interpolation, missing/pointer data, ambiguous IDs, stale source/metadata, actual material/transform/state writes, identity preservation, rollback and cleanup scope. CI also checks the actual four-tile scene, spawn markers, C# syntax and workflow boundaries. Existing project checks cover saved bindings/materials/road geometry. A Unity import/compile, engine probes/captures and an Android device pass remain separate necessary evidence for changes that affect those behaviors.




## Live local file change sensing

Run `python Tools/World/world.py site --x 350 --z 167.3 --radius 50` to read a single AI context capsule. The `site` response includes the raw paged `inspection` and a compact `site` view showing terrain/grass, saved colors, material slots, dimensions, world-axis directions, names/semantic confidence, native radius-wide tree/rock counts, and which geometry is still unresolved. Scene object roles/distances and native instance details are *page scoped*; follow both `scene_next_offset` and `native_next_offset`. Semantic labels and AABB overlap are not proof of exact visible shape or physics clearance. Native height and foliage dimensions remain terrain-grid and prototype-envelope estimates.

To re-run a selected neighbourhood whenever its saved source files change **while a terminal process is running**, use:

```bash
python Tools/World/world.py watch --x 350 --z 167.3 --radius 50 --poll-seconds 2 --quiet-seconds 1
# optional: --max-events 3 ; use Ctrl+C to stop continuous watching
```

This is a foreground polling sensor, not a hidden agent or GitHub background service. It prints JSONL: an initial baseline, then `core_files_changed` events with changed file paths, available SHA256 values, before/after neighbour distances, shape/material/tint change identifiers, and the new `current_site`. Saves in progress can be retried. Hashes of files never previously read are marked unavailable; files larger than 128 MiB are not hashed by the watcher. It checks one selected area per process; it does not certify the entire 2km world, compile Unity, run physics, or auto-commit. For a GitHub-hosted one-shot query submit `{"command":"site","x":350,"z":167.3,"radius":50}` to `world-query.yml`; it materializes relevant LFS within the normal 1 GiB cap. A web-only GitHub request does not launch `watch`.

Automatic edit observations and caller `observe_after` observations are merged (bounded to 32 rather than silently dropped); material operations also sample their saved MeshRenderer users. A change of neighbour membership can mean moving across a radius/page boundary or pagination shifts, not object deletion. Exact material property changes are reported by `operation_deltas`; Unity captures are necessary to establish actual pixels or visible stains, and the matching Editor is necessary for final imported asset/collision validation.
