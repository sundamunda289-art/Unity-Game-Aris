# Android open-world streaming / Adreno 610 research — 2026-11-20

**Status: read-only source audit and engineering recommendations; NOT an FPS-fix commit.**

Target: Unity 6000.3.26f1, URP 17.3.0, Snapdragon 680 / Adreno 610,
4–6 GB RAM. Reviewed canonical GitHub `main` source; no accessible live
Codespace shell, native Unity Editor compilation, physical Android run, frame
capture, graphics-driver trace or asset-bundle build in this review.

## Public techniques versus proprietary implementation

Public Unity / Android / Qualcomm guidance supports:

1. **Spatial loading with lookahead and hysteresis.** World regions must be
   independently loadable (additive scenes or Addressables groups) for actual
   memory reclamation. Schedule prefetch based on player motion, bound each
   activation/deactivation batch, keep a retry margin, and avoid blocking
   `WaitForCompletion` paths.
2. **LOD, HLOD, impostors, and spatial occlusion.** Keep high-detail near-player
   assets and terrain intact, but use artist-verified distant representations,
   SRP Batcher / instancing where compatible, and occlusion only after
   checking indoor/outdoor visibility and occluder bake data. Occlusion
   culling is not a substitute for unloading assets.
3. **Time-sliced texture/mesh uploads.** Unity's native async upload ring
   amortizes transfers; changing its buffer size or timeslice cannot remove
   a costly one-frame `Instantiate` / shader/physics integration.
4. **Shader PSO tracing and targeted warmup.** Unity 6 can use a
   `GraphicsStateCollection`; compile the *actually used* GPU graphics-state
   variants first. Empty preload lists do not prove shader compilation is
   the stall cause. Warming all variants blindly can make startup worse.
5. **Mobile tile-based rendering / thermal pacing.** Favor reducing
   unnecessary render-target traffic, overdraw, transparent passes and
   bandwidth pressure. Attribute CPU versus GPU time with real frame
   timings and capture a 10+ minute thermal soak before changing quality.
   Qualcomm describes Adreno tiled rendering; Snapdragon 680 uses Adreno 610.

These are public design patterns, **not** verified descriptions of private
GTA, PUBG, Genshin Impact or CoD Mobile implementation.

## What is already in this repository

| Subsystem | Verified baseline | Remaining limitation |
| --- | --- | --- |
| Native map | Four authored TerrainData tiles, source trees/vegetation preserved | All four terrain assets remain resident; this is not additive chunk streaming |
| Cars | Approximately 44 candidate plans, up to 10 active; 105 m load / 160 m unload; 150 m predictive authored prewarm | A single native full-car hierarchy build can still cause a frame spike |
| Static physics | Spatial grid, 55 m activation / ~61 m retention, bounded deactivation | Dense-entry essential physics activation is not proven spike-free |
| Shared Android scheduling | `AarisMobileStreamingFrameGate` arbitrates optional models/imports/diagnostics, slow-frame and cleanup fences | Does not split a single heavyweight Instantiate internally |
| Rendering | URP SRP Batcher, authored LODs, terrain instancing, mip streaming | No measured PSO trace, indoor/outdoor occlusion bake, or independent scene-chunk assets |
| Upload | Source `QualitySettings.asset`: 2 ms timeslice / 16 MB ring; runtime constrained-GPU governor may adjust timeslice | More buffering is not automatically faster; measure stalls and memory |
| Graphics quality | `AarisMobileRuntime` uses Adreno 610 detection, shadow/MSAA/mip budgets, frame-time governor | Native sharpness, distant silhouettes, foliage and water must be visually compared on-device |

Source pointers:
- `Assets/AarisWorld/Scripts/AarisMobileStreamingFrameGate.cs`
- `Assets/AarisWorld/Scripts/AarisMobileRuntime.cs`
- `Assets/AarisWorld/Scripts/AarisVehicleWorld.cs`
- `Assets/AarisWorld/Scripts/AarisNearbyColliders.cs`
- `Assets/AarisWorld/Scripts/AarisRoadsideBikeScenery.cs`
- `ProjectSettings/QualitySettings.asset`
- `Docs/ANDROID_OPEN_WORLD_ONE_CAR_TEARDOWN_20261120.md`
- `Docs/MOBILE_REVIEW.md`

## Safe implementation order, gated on measurements

**Gate A — Evidence first.** On the actual handset compare fixed scene
route and graphics settings, cold boot, fast driving, dense settlement,
dense canopy camera turns, vehicle retirement, pause/resume and 10+
minutes of continuous play. Capture p50/p95/p99 CPU main/render/GPU
frame times, largest stall, PSS/native/graphics memory, GPU texture
bandwidth/overdraw, shader-compile events, thermal state, logcat and ANR/LMK.

**Gate B — If import/hierarchy stalls dominate.** Profile actual optional
import completion and vehicle/house Instantiate separately; then
reduce collision/instantiation work per frame or pool only measured
repeat offenders. Preserve exact authored models and collisions.

**Gate C — If resident assets dominate PSS.** Author *independently loadable*
terrain/cell scenes in Unity Editor (with shared dependencies and explicit
startup seam/height/texture/lighting checks). Player-centered async
prefetch, load/unload thresholds and in-flight state machines become
meaningful only after this asset migration. Preserve all source terrain
and the near-camera full-detail version.

**Gate D — If GPU bandwidth dominates.** Capture Adreno graphics counters;
try changes individually (compatible instancing, opaque/transparent pass
cost, measured occlusion, impostor rendering, shadow passes) with A/B
screenshots and alpha/forest/water edge checks. Do not turn off URP
opaque/depth textures blindly: source water graphs may use them.

**Gate E — If shader stalls dominate.** Trace *used* graphics states in
Unity 6, build a targeted GraphicsStateCollection, then stagger warmup
after the first visible scene and compare startup & first-use latency.

## Multi-agent release safety

Do not modify another agent's dirty Codespace files; no reset/checkout,
no new worktree, no forced push and no wildcard staging. Before any
runtime fix, record the *current* source and physical-device baseline,
run existing Python/C# source tests, Unity Editor import/compile/build,
and Android in-game visual/FPS tests. Only own paths go into a PR.
This research document does not claim compiled code, improved FPS,
reduced memory or a synchronized Codespace.

## Sources (public)

- Unity 6.0.3: PSO tracing/warming:
  https://docs.unity.com/en-us/engine/6000.3/manual/materials-and-shaders/shaders/shader-troubleshooting/shader-reduce-stalling/shader-prevent-stutter/shader-pso-trace-warming/shader-pso-introduction
- Unity 6.0.3: GraphicsStateCollection API:
  https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/experimental/rendering/graphicsstatecollection
- Unity: configure async upload pipeline:
  https://docs.unity.com/en-us/engine/7000.0/manual/analysis/graphics-performance-profiling/loading-texture-mesh-data-asynchronously/configure-asynchronous-upload-pipeline
- Unity Addressables: additive scenes:
  https://docs.unity.cn/Packages/com.unity.addressables%402.2/manual/LoadingScenes.html
- Unity: occlusion caveats:
  https://docs.unity.com/en-us/engine/6000.3/manual/cameras/occlusion-culling/getting-started
- Unity: Adaptive Performance timing and thermal:
  https://docs.unity.com/en-us/engine/6000.5/manual/analysis/runtime-performance-scaling/adaptive-performance/performance-metrics/track-timing-thermal-data
- Vulkan tile-based rendering guidance:
  https://docs.vulkan.org/guide/latest/tile_based_rendering_best_practices.html
- Qualcomm Snapdragon 680 platform:
  https://www.qualcomm.com/smartphones/products/6-series/snapdragon-680-4g-mobile-platform
