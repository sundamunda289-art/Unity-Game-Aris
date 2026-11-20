# Low-end Android open-world retirement pacing — 2026-11-20

## Evidence, not a proprietary-game claim

Published Unity documentation supports spatial/Addressables-based streaming,
LOD/HLOD, renderer culling, async texture and mesh GPU upload, shader/Pipeline
State Object warmup, and Android frame-time / thermal monitoring. It does not
expose the private internals of GTA, PUBG Mobile, COD Mobile or Genshin Impact.
Project-specific bottlenecks need Unity Profiler and real handset evidence.

## Existing Unity 6 URP systems already present

- **World:** 2 km authored four-tile native terrain and tree assets remain intact.
- **Cars:** 44 authored candidate records, max 10 nearby, 105 m load and 160 m unload;
  velocity-bounded predictive model prewarm, bounded candidate probes, one concurrent
  optional import on constrained devices.
- **Buildings / colliders:** deferred settlement builds, cached spatial collider grid,
  55 m enable / 61 m hysteresis retain radius, limited native retirement per frame.
- **Shared pressure:** `AarisMobileStreamingFrameGate` prevents most optional native
  hierarchy builds, expensive importer integration and diagnostics from coinciding.
- **Render and memory:** `AarisMobileRuntime` has native-frame telemetry,
  adjustable upload timeslice, Android low-memory reclamation, URP governor and
  budget-GPU detection including Adreno 610.

## Change in this PR: native teardown backpressure

`AarisVehicleWorld.Awake` now sets `unloadBudgetPerPoll = 1`, previously
`2`, **only** for Android RAM <= 3072 MB or GPUs matched by
`UseConstrainedGpuStreamingBudget()`, including Adreno 610 regardless of 4/6 GB
RAM. Each normal 300 ms stream poll can retire at most one distant, stationary,
unoccupied car instead of two. Unity processes `Destroy` finalization
after `Update`, so this reduces one potential main-thread/PhysX burst.
The next poll evaluates remaining cars; no original plan entry is discarded.

No change to imported meshes, textures, native trees, 44-car plans,
10-nearby cap, model load/unload distances, camera or shaders.
The already-existing retirement-frame fence remains in effect.
More delayed retirement can temporarily retain a distant parked car for an
extra poll; measure PSS/native heap and watch rapidly driven transitions.

## Test / deployment boundaries

Source-contract tests and deterministic scheduling models are useful but
do **not** prove Unity IL2CPP compilation, Play Mode, Vulkan/OpenGL behavior,
FPS improvements, or Android thermal stability. Validate on Snapdragon 680 /
Adreno 610 with real Unity 6000.3.26f1 Android build. Compare p50/p95/p99 CPU
and GPU frame times, count of >50ms frames, memory/LMK, native destroy spikes,
nearby parked-car visibility, and scene screenshots before and after.

Do not automatically strip Shader Graph color/depth textures, shorten trees,
lower terrain draw distance, add blind occlusion culling, or preload all PSOs:
these can visibly damage the map or increase startup/thermal cost. Profile each
future change, use reversible quality controls, and integrate PSO warmup only
after collecting real shader/graphics-state variants.

## Public platform references

- Unity asynchronous texture / mesh uploads:
  https://docs.unity3d.com/Manual/LoadingTextureandMeshData.html
- Unity `Object.Destroy` end-of-update behavior:
  https://docs.unity3d.com/ScriptReference/Object.Destroy.html
- Android Adaptive Performance:
  https://docs.unity3d.com/Packages/com.unity.adaptiveperformance@latest
