# Mobile world proportions: saved source changes (2026-11-04)

Scope: `Aaris-khan-don/Unity-Game-Aris`, Unity 6000.3.26f1, URP 17.3, the
existing 2 km x 2 km four-TerrainData scene. The main scene, terrain binaries,
original mesh geometry, house source geometry, and existing .meta GUIDs are not
rewritten by this patch. This is a proposed source change, **not** a demonstrated
Unity render, Android FPS improvement, or approval of door/window navigation.

## Actual changes

- Six existing source tree prefabs have a wider XZ root scale relative to their
  vertical Y scale. Their LOD child meshes, original materials, collision
  components, prefab fileIDs, and .meta GUIDs are preserved. X/Z scale applies
  to the *whole existing SpeedTree mesh*, not an independently editable trunk.
  The result requires an imported Unity visual/collider check before merging.
- `AarisMobileRuntime` applies a device-memory-class budget on **Android
  player only**, without changing native TerrainData, camera far clip, or URP
  render resolution. For reported memory up to 2304 MB: <=256 MB mip
  streaming budget, <=0.90 LOD bias, <=90 m directional shadows, <=2048
  shadow atlas. For 2305-3584 MB: respective caps 320 MB, 0.98, 160 m and
  2048. Higher RAM/unknown-memory devices retain their existing settings.
  Runtime-only overrides restore on component destruction.
- Low-memory callbacks trim the streaming mip budget in 32 MB steps without ever
  increasing an already smaller budget (minimum protected floor 48 MB where
  the configured budget allows). Existing deferred unused-asset cleanup remains.
  Diagnostics now record the profile and actual runtime budget/LOD/shadow
  settings; hardware FPS cannot be inferred from those values.
- `AarisNearbyColliders` now computes disabled Sphere/Capsule collider
  bounds without enabling them for inspection, syncs transforms only when
  the activation set changes, and restores each originally enabled/disabled
  state when the manager is disabled. Unsupported collider classes are
  left in their authored state and logged rather than dropped silently.

| Source tree prefab | Previous root scale X,Y,Z | Proposed X,Y,Z |
| --- | --- | --- |
| Cypress_Forest_Desktop | 1.50,1.50,1.50 | 1.70,1.38,1.70 |
| Conifer | 1,1,1 | 1.16,0.92,1.16 |
| Pine_A / Pine_B | 1,1,1 | 1.14,0.94,1.14 |
| Pine_C / Pine_D | 1,1,1 | 1.12,0.96,1.12 |

## Architectural rationale / constraints

Close detail is retained and distant LOD meshes remain available; *casting
shadows all the way to a distant silhouette* is not necessary to see it. Limiting
the shadow atlas/resolution and shadow reach can reduce GPU work and shadow
memory on a slow device. Actual savings depend on the GPU and render path.
Mipmap streaming budgets include non-streaming textures; these caps are
**not process/GPU memory limits**. The current terrain paint sources and other
non-streamed textures can exceed a small budget by themselves. Neither 2 GB
compatibility nor a 60 FPS result is established.

The existing saved project already provides 17 three-LOD model prefabs and
native terrain vegetation. They must be kept compatible with the scene GUIDs.
Avoid re-running the historical world/ground generator blindly: it can
overwrite saved authored placements. Keep Unity LFS assets, complete metadata,
and source-of-truth TerrainData checked out before any native edit.

## Geometry work that still requires visual/physics evidence

- **Houses:** Audit the original combined-mesh geometry and each entrance
  from a 1.78 m controller at actual player scale. Provisional residential
  targets are clear doors 0.9-1.1 m wide x >=2.0 m tall, habitable room
  headroom >=2.4 m, and physically clear entry paths. These are design
  targets, not measurements of any existing house. Do not simply scale a
  house to compensate for an inaccessible door: it also changes roads,
  windows, interior steps, and collider clearances.
- **Windows:** Decide a subset of low-sill open windows as vaultable only
  after controller jumps, collision mesh openings, and screenshot evidence.
  Others should be visibly blocked or too high. No window was cut or
  claimed jumpable by this source-only patch.
- **Settlement spacing:** Previous report lists 47 residential placements
  and a minimum building gap of approximately 21.5 m. To form intentional
  clusters of 2-3 houses, re-inspect current site footprints and road
  frontage, then patch native positions with measured foundations and
  accessible corridors. Do not use that historical report as current
  truth or move a house without a current scene/terrain read.
- **Rocks:** Existing RockCover mesh has three LODs; last saved report lists
  eight recently added companions with distinct yaw and scale. To improve
  scatter, inspect current terrain normals/mesh support and rotate/place
  rocks in small groups with partial ground embedding and vehicle/path
  clearances. No speculative native rock placement was performed.
- **Trees:** Independent trunk taper, branches and crown silhouette require
  modifying licensed SpeedTree source or importing a validated replacement
  mesh; X/Z prefab scaling alone does not retopologize the trunk.
- **Streaming:** True scene/chunk streaming is not implemented by these
  renderer budgets; all four native terrains remain loaded.

## Required validation

1. Python regression: `python -m unittest discover -s Tools/World/tests -v`
   and `python Tools/World/check_contracts.py`.
2. Saved-source validation with complete LFS where applicable:
   `python Tools/Validation/check_world_review.py --native` and
   `python Tools/Validation/check_mobile_review.py --native`.
3. Unity 6000.3.26f1: import, C# compile, render landscape/trees from spawn,
   compare close/far LOD and inspect colliders; inspect door/window clearances.
4. Android 2 GB, 3 GB, 4 GB+: compare identical runs and routes using the
   REPORT diagnostics, including FPS p95, PSS, texture memory, thermal
   behavior, forest/settlement traversal, and low-memory callbacks.

The project does not currently have an observed device pass for these changes.
Do not merge or claim FPS/visibility improvements without reviewing run output.

## Follow-up: low-RAM terrain details and residential LODs

Seven residential source prefabs (a_frame_cabin, bungalow_house,
detached_two_storey_house, garage_apartment, pitched_roof_cottage,
row_three_townhouses, two_bedroom_garage_house) and RockCover.prefab
now transition from LOD0/LOD1 at 0.28/0.095 screen height rather than
0.22/0.07. Their farthest 0.0025 threshold, existing meshes,
cross-fading, transforms, colliders and original .meta GUIDs remain
unchanged. The close/mid-range model detail can cost less GPU time,
but Unity screenshots are needed to detect poor distant silhouettes
or transition popping.

Android 2-GB / 3-GB memory classes now cap the runtime terrain
grass/detail rendering distance at 55 m / 80 m and density at 0.58 /
0.78. An authored lower value is never raised. A low-memory signal
further decreases the active grass-detail distance to at most 75%
of its profile cap; the mip streaming budget no longer increases
when it was already below the old 192-MB floor. These values are
reversible at component teardown, and do not cull distant tree
meshes, terrain geometry, or camera far distance. These are
hypotheses for an A/B device test, not an FPS measurement.

The nearby-collider registry rebuilds its activation window when
the component is re-enabled, preventing all previously restored
colliders from continuing active outside the player's vicinity.
TerrainColliders/CharacterControllers accidentally added to the
registry are excluded. Authored obstacles that begin disabled but
are explicitly registered for proximity management are still
activated when near; their authored enabled states are restored
on manager disable.

The regression test in test_mobile_lod_runtime_contract.py guards
the saved LOD thresholds and runtime/collider source contracts.
It does not compile C#, raycast doors/windows, measure GPU budgets
or prove 2-GB compatibility. Device tests, Unity import/compilation,
real house mesh measurements, and native terrain placement review
remain mandatory before merging to main.
