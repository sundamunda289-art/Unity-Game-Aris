# Android 4GB optional asset prewarm admission — 2026-11-19

## Evidence and scope

Inspected canonical Unity 6000.3.26f1 / URP 17.3 source and connected Codespace.
This is a **source-level performance risk fix**, NOT a measured Snapdragon 680 FPS
improvement, Android crash fix, Unity Editor compile, GPU trace or on-device test.

The saved scene contains four native Terrain tiles. Existing runtime systems already
budget CPU-side static collider indexing, terrain tree placement queries, weapon
pickups and vehicle instantiation. However, the vehicle fallback prewarmer, authored
vehicle stream, motorcycle stream and rival visual/animation prewarmer could issue
independent low-priority Resources.LoadAsync requests across the same Android
frames, and their completions could overlap with hierarchy integration. Per-system
limits alone did not serialize these optional native asset requests.

## Implemented without reducing world detail

- Extend AarisMobileStreamingFrameGate with a single cross-system optional
  ResourceRequest admission for Android devices reporting 1–4608 MB RAM. A
  completed request frees its slot. Unknown-RAM / larger devices preserve old
  behavior. The admitted load also consumes the existing optional-integration
  frame slot so prewarm and Instantiate do not begin on the same low-RAM frame.
- Route optional vehicle fallback meshes, near-player authored car bodies,
  roadside motorcycle models, rival models and rival animation clips through it.
  All are still loaded at their original resource paths and original resolution.
- A denied request is retried on a subsequent frame; an in-flight/completed
  failed car request remains eligible for fallback once admission is available.
  Renderer/material binding of a completed authored vehicle is also gated.
- Keep essential playable weapon prewarm, terrain, native trees, collision
  shapes, source meshes/textures, map placements, and match rules unchanged.

The gate is intentionally not a total memory manager. It prevents **cross-system
burst contention** but cannot eliminate the CPU/GPU work of rendering thousands
of SpeedTrees and high-quality URP passes. A uniform graphics-quality reduction
was not made.

## Relevant further audit findings (not changed here)

The serialized High URP asset retains HDR, native render scale 1.0, 4x MSAA,
4096 main shadowmap, 1000 m shadow reach, four cascades, depth and opaque
textures. The existing Android runtime already applies conditional GPU
budgets and an optional dynamic render-scale governor. The serialized Low
URP asset is also native scale; neither asset was edited in this change.
Large original .tif/HDR files do not establish Android runtime VRAM usage;
inspect Unity-imported/compressed allocations before drawing conclusions.

## Verification

Pass: static Python regression tests for global optional admission,
pre-existing frame gate, vehicle fallback preload, motorcycle prewarm,
rival model and animation prewarm (30 test cases total) and git diff --check.
These tests validate source-level contracts, not Unity binary/visual execution.
No Editor/APK build or Android device playtest was run for this change.

## Required on-device acceptance gate

On a real 4 GB Snapdragon 680 / Adreno 610 Android device, compare identical
cold-boot/landing/forest-sprint/car-entry scenarios before and after:
1. CPU main/render-thread and GPU p50/p95/p99 frame times from Unity Profiler or
   FrameTimingManager, plus the worst continuous stall.
2. Native/managed/graphics memory and Android low-memory-kill/exit records, GC
   allocations and asset-loading spike markers, temperature and thermal throttling.
3. Visual coverage and near/far trees, intact shadows/materials, buildings,
   usable vehicles, weapons and all six rivals. Confirm no objects disappear
   due to admission retries or delayed model setup.
4. Real Android logcat crash reason for full phone hang or Home-screen exit.
   Do not classify those as GPU driver deadlock without driver/crash evidence.

If measured runtime stalls worsen or essential match bootstrap is delayed
unacceptably, revert the optional admission integration as one reviewed PR.
