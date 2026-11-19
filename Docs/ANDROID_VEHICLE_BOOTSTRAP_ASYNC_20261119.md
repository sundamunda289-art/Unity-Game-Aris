# 4GB Android vehicle startup staging — 2026-11-19

**Target:** Unity 6000.3.26f1, URP 17.3, Snapdragon 680 / Adreno 610.
**Evidence:** actual saved C# sources and connected Linux Codespace; no real-device profiler attached.

## Verified source-level startup pressure

`AarisVehicleWorld.EnsureForPlayer()` previously called `Setup()` synchronously in
the scene-load bootstrap. `Setup()` parses the local catalog and calls
`Resources.Load<GameObject>` for each eligible fallback car and wheel. The
authored high-resolution car variants already use near-player `LoadAsync`
streaming; only **the fallback boot path** is changed here.

## Change

On Android when the RAM report is at most 4096 MB or the existing budget GPU
detector matches, the world is created immediately but awaits a coroutine
before running normal `Setup()`. The coroutine first yields a scene frame,
asynchronously requests the catalog, and then requests distinct eligible
base car and wheel prefabs **one at a time** with low loading priority.
It yields an extra frame between requests. After these cached assets are
available, the unmodified `Setup()` performs all original mesh/material
validation, plan creation and visual setup, and only then connects the
vehicle session and HUD.

World polling explicitly returns while startup setup is incomplete.
Desktop/editor and stronger Android devices keep their original synchronous
boot. No new baked meshes or instanced vehicles are created by prewarm;
the original 44 planned car distribution, 105m streaming behavior, authored
PBR models, wheel geometry, transforms, materials, foliage, terrain, shader
and texture settings are unchanged.

## Limits and follow-up proof

This mitigates a **plausible main-thread hitch source**; it does **not**
demonstrate improved FPS or cure Android crashes. `Resources.LoadAsync`
still integrates objects on the main thread with Unity's loading-priority
budget. The base-car `Setup()` still performs synchronous validation and
shared-material creation (after prewarm), so profiling is required.

Run:
```sh
python3 Tools/Validation/test_android_vehicle_bootstrap_async.py
python3 Tools/Validation/test_android_4gb_gpu_streaming_budget.py
python3 Tools/Validation/test_vehicle_streaming_priority.py
python3 Tools/Validation/check_mobile_hotpaths.py
```

Use a licensed Unity compile and **actual Android** startup traces before
accepting claims of eliminated freezes. Collect logcat ANR/crash/LMK markers,
per-frame CPU/render/GPU times, graphics memory, PSS and first vehicle
creation. Never mass-edit texture importers; ASTC savings depend on actual
texture formats and compression block sizes.

Unity reference: https://docs.unity.com/en-us/engine/6000.0/script-reference/unityengine/application/backgroundloadingpriority
