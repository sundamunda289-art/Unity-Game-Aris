# Aaris Island — grounded Android map review

Android **0.3.0 / code 6** adds bounded building physics, a read-only terrain CPU-memory pass and local crash diagnostics, plus 4 climbable towers, 6 original static wreck props and 24 Poly Haven rock props. Existing terrain, lighting, texture resolution and close meshes are preserved. The reported Android crash is **not yet device-verified as fixed**. [Changes, limitations and phone testing](Docs/MOBILE_REVIEW.md).

A saved 2 km × 2 km Unity URP island with a visible 1.78 m human reference player.
Open `Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity` in **Unity 6000.3.26f1 / URP 17.3**.
Clone with Git LFS and fetch the actual assets before opening or building.
The scene, player prefab, materials, terrain trees and road meshes are already saved in the repository. **No Unity Cloud pre-method, export method or scene-generation command is required.** Open the scene or build the checked-in project normally.

## Walk and inspect

- **Phone:** left joystick moves, drag the right side to look, WALK toggles running, JUMP jumps, HOME returns to the safe spawn.
- **Desktop:** WASD/arrows move, right mouse drag looks, Shift runs, Space jumps, F returns home.
- The saved shoulder view follows the grounded player. The free-flight/overview/altitude controls have been removed.
- Start in the flat western side of ValleySettlement, facing nearby houses. The saved spawn is checked against full-resolution terrain and native tree/rock obstacles, so residential scale is visible immediately rather than first viewing homes from roughly 176 m away.
- Right-side look reaches 70 degrees upward, so the saved sun at 50 degrees elevation is reachable. The previous 18-degree upward limit could never show that sun.
- CharacterController handles terrain, steps, slopes, trunk and building collisions. The saved Player layer excludes the avatar from camera/support queries; closest-hit camera checks avoid a truncated hit list in dense forest.
- The visible human uses shared Unity primitive meshes and saved outfit materials, with walking limb animation. It is a lightweight scale-reference character, not an imported Free Fire character model.
- Water remains visual: review movement stops at the shoreline, while solid docks/ramps above water remain walkable. HOME restores spawn position and facing. Combat, bots, loot, swimming and drivable vehicles are separate work.

## Saved core assets

The daylight sky uses the built-in procedural sky shader with a visible sun disk, a linked directional light and explicit ambient colors. No runtime lighting repair component is used. Asphalt, concrete, sand and marking PNG metadata use TextureImporter, retaining their original GUIDs.

Daylight uses sky exposure 1.15, neutral sky tint, sun intensity 1.35, shadow strength 0.55 and brighter sky/equator/ground ambient colors. Modern Unity material keywords are saved explicitly. These settings preserve shade and texture detail; their final appearance still needs a Unity render/device check.

Buildings retain their original close meshes and have saved middle/far meshes, with consistent material slots. Only the high-detail mesh has a static collider. Generated native meshes do not retain unused CPU-readable copies. The Android build selects ETC2 texture compression; global texture mipmap limit and render scale are not reduced.

The URP renderer explicitly includes the saved Player layer in both opaque and transparent filters. Source glTF sidedness and normal strength are preserved: 59 kit materials use their intended back-face culling, 123 source double-sided materials retain it, and 13 normal strengths are corrected. Meshes, source texture pixels, material color/texture references and forest geometry are preserved. Future glTF occlusion imports also map source red-channel AO into URP's green channel, including cached imports.

All 17 reusable building prefabs now contain the same three LOD levels as the scene. The 47 residential placements and their prefabs are uniformly enlarged by 10.5–12% per model type, while the player remains 1.78 m tall. Saved foundation meshes expand with them, and original close mesh geometry is shared. Road edges, vehicle corridors, building separation and full-resolution flat ground are checked before committing the new proportions. Large industrial/tower kits retain their original scale.

`Docs/FOREST_PLANTING_PLAN.json` describes 1,100 additional source-tree placements, mostly in the sparse eastern half. Native authoring checks full-resolution slopes and shoreline, reserves district pads and road corridors, and writes accepted instances directly into the four TerrainData files. Shared SpeedTree prefabs, textures and existing LODs are reused. No runtime forest spawner is added. Terrain heights are unchanged.

**The materialized totals, rejected candidates and actual building LOD triangle counts are in `Docs/GROUND_REVIEW_REPORT.json`.** `Docs/TERRAIN_INDEX.json` and per-tile instance exports reflect the saved native terrain bytes.

Cypress instances are a further 12% taller and wider in native TerrainData; already tall conifers/pines retain their scale. `Docs/CYPRESS_SCALE_TARGETS.json` stores absolute targets so repeated authoring cannot compound their size. Counts, positions, terrain heights, grass/detail data and terrain alpha textures are preserved. `Docs/WORLD_REVIEW_REPORT.json` records actual scales, clearances and daylight settings.

The island has 13 districts, connected roads, a 240 m runway and 45 vehicle markers. Its 128 playable building placements now share six low-rise prefabs with open entrances, walkable stairs and floor/roof loot sockets. The placed set includes 16 former runtime house sites and 21 new houses in forest clearings, hill approaches and near towers. [Saved placement and socket audit](Docs/AIRY_HOUSE_AUDIT.json).

Road paint is clipped at the actual paved mesh triangle boundaries and saved 2 cm above those triangles, rather than following the terrain underneath. Original road meshes and their detail are preserved. `Docs/ROAD_SURFACE_REPORT.json` records the measured separation after saving.

Coastal/canal water uses the native URP 17.3 PBR lighting function, with ambient spherical harmonics, sky/environment reflection, actual sun color/intensity and functional roughness/smoothness. Color and DepthOnly passes share the exact wave vertex function, supporting the renderer's existing forced depth priming. This adds no reflection camera, scene-color copy requirement, new render feature or post-processing pass. The checked-in `.shader` is the authoritative source for offline reproduction.

`Docs/RENDER_REVIEW_REPORT.json` and `Docs/RENDER_CORE_CHECKS.json` record source-material agreement, renderer layer coverage, native settings and unchanged geometry/texture references. The existing SRP Batcher, HDR, render scale 1.0, 4096 shadow atlas and 1000 m shadow distance are preserved. Their target-device cost still needs profiling; no FPS gain is asserted from offline checks.

## Manual APK build

Use **Aaris → Build Android Ground Review APK**, or the existing batch entry point:

`Aaris.MapReview.Editor.AarisExplorerBuild.BuildAndroid`

Output: `Builds/Android/AarisMapExplorer.apk` (kept for existing phone/cloud build scripts; app version 0.3.0, code 6).

Target: ARM64 / IL2CPP / Android 8+ / landscape, package `com.aaris.mapexplorer`. The player targets 60 FPS (with a 30/60 frame-pacing toggle); actual target-device frame time and memory must be measured. Existing Unity 6.3 URP compatibility defines are preserved. No automatic Unity/APK build is added.

The build preprocessor rejects a missing player/camera, wrong player height, pending native assets, wrong surface importers, missing building LODs, missing terrain data or missing scene scripts.

## Verification and reproduction

`python Tools/Validation/check_explorer.py --native` checks scene references, player and camera bindings, C# syntax, core asset references, LOD levels, terrain checksums and forest road/district clearances.

Optional maintainer reproduction only: `python Tools/MapBuild/ground_native.py` saves TerrainData and building LODs, reopens terrain binaries, verifies unchanged heights, regenerates indexes and updates hashes. It is idempotent for the saved planting plan. `python Tools/MapBuild/surface_native.py` reproduces the saved road paint. Neither command runs in the game or is required before a normal Unity build.

`python Tools/MapBuild/world_review.py` reproduces the saved proportions, complete prefabs, cypress scale and daylight. `python Tools/Validation/check_world_review.py --native` verifies those saved outputs. This authoring command is also optional for maintainers; the outputs are committed, and normal builds do not generate them.

`python Tools/MapBuild/render_review.py --apply` optionally reproduces source material/renderer settings. Native rendering checks compile the water's vertex/pixel HLSL for D3D11 and mobile Vulkan against pinned official URP 17.3 headers with verified Microsoft DXC binaries. `Docs/WATER_HLSL_CHECKS.json` records the actual compiler results and shader hash. Standalone HLSL checks do not replace Unity's ShaderLab import, Android shader build, full scene render or device test.

`Tools/MapBuild/build_map.py` also invokes ground authoring after the original offline map assembly. Its terrain inputs must be the four original vendor archive tiles, as described in `Tools/MapBuild/README.md`; do not re-sculpt the already edited terrain.

**Offline validation is not a Unity Editor compile, render or Android playtest.** These remain explicitly untested in the reports until performed. The former near-tree termination still requires a device crash log and a repeatable device test before it can be called resolved.

Original vendor terrain assets retain their existing Unity Asset Store licence requirements. Model provenance and licences remain in `Tools/MapBuild/model_sources.json` and `URP-TERRAIN-PROVENANCE.md`. Historical TRANSFER reports describe the initial import; current terrain hashes are in `Docs/MAP_BUILD_REPORT.json`.
