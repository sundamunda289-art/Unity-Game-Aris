# Android parked-car component query allocation guard — 2026-11-21

## Scope and evidence

Target: Unity 6000.3.26f1, URP 17.3.0, Adreno 610 / Snapdragon 680, 4–6 GiB Android devices. This change is deliberately limited to the **existing car streamer**; it is not a terrain streaming migration or an FPS guarantee.

Unity 6's documented `GameObject.GetComponentsInChildren<T>(bool,List<T>)` fills a caller-owned list instead of returning newly allocated arrays:
https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/gameobject/getcomponentsinchildren

## Source-audited hot spot

`AarisVehicleWorld.Build` runs whenever a new near-player vehicle appears. The original code created a new `Collider[]` and `Renderer[]` from child component enumeration, and on the fallback atlas path created identical `Material[]` layouts once for each renderer on every new car. All of this happened beside an unavoidable prefab `Instantiate`, collision setup and wheel rig integration.

## Implementation

- Reuse two owner-local `List<T>` buffers for instantiated vehicle collider and renderer queries. Process all active/inactive children as before.
- Accept `IReadOnlyList<Renderer>` in existing visual validation and authored-material conversion. Arrays used by separate wheel visual checks still work; validation and fallback behavior are unchanged.
- Cache fallback-only material-slot arrays by exact submesh count, always referencing the original scene-shared URP palette material. They contain no per-car paint: deterministic paint remains in the existing instance-scoped property blocks.
- Clear scratch buffers after successful material integration and before painting, and on early renderer validation failure, to avoid retaining retired vehicle wrappers.

## Explicitly unchanged

All authored mesh/texture files, four TerrainData tiles, scene content, trees, foliage, paints, materials, visibility distances, 44 candidate car plans, ten-car cap, 105 m load/160 m unload, collision rules, shaders, lighting, target FPS, URP asset and native update/physics schedules.

## Verification and rollout gate

Run `python3 Tools/Validation/test_android_vehicle_spawn_allocation_budget.py`, and the existing vehicle contracts (`check_vehicle_system.py`, `test_vehicle_visible_body_guard.py`, `test_vehicle_paint_fallback.py`) plus CI C# parse. These source tests cannot prove Unity IL2CPP or Android frames. Unity Editor import/build and on-device profiler (cold scene, fast travel, dense village, car spawn/retirement, repeated gameplay and thermal soak) remain required before declaring real gains.

Measure `Aaris.Streaming.VehicleBuild`, GC.Alloc, p95/p99 main/render/GPU frame time, active car visuals, collision/driver ownership, heap/PSS, and screenshot quality before/after. A single `Instantiate` remains a potential native spike; prefab object pooling needs separate gameplay/reset analysis and was intentionally not introduced.

Do not run destructive Git operations on other agents' dirty Codespace files. Prefer PR review and only a conflict-free fast-forward into a clean Codespace main; if dirty, leave other agents' work untouched.
