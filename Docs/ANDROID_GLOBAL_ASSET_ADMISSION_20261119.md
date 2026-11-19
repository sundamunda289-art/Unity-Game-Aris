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

## Second-pass concurrency simulation and fixes

A deterministic frame-scheduler model in
`test_android_cross_system_hierarchy_budget.py` exercises five independent
optional construction jobs (vehicle, weapon visual, medical, settlement and
motorcycle) becoming ready in one frame. Before coordination, individual local
budgets did not prevent several of those jobs being admitted together; the
source-level model now admits at most one optional **new hierarchy** per
low-RAM Android frame, while retaining every job to complete later. This is a
logic model, not a measurement of real Unity native render-thread scheduling.

Additional checks and code changes:
- `AarisSettlementExpansion.PlaceSitesAcrossFrames` uses the shared gate
  just before constructing each validated house/watchtower. All 21 planned
  sites retain their original placement, mesh, materials, collisions and
  existing terrain/tree clearance checks; a busy slot yields rather than
  skipping the site.
- `AarisOfflineTacticalHUD` applies admission only for **new** medkit and
  helmet 3D roots after terrain/physics validation and after pool reuse.
  Denied admission restores the medical scan cursor and retries the same
  site on the next frame instead of incorrectly imposing its 5-second
  invalid-ground cooldown. Existing pooled pickups never wait for admission.
- A completed rival model's renderer inspection now waits for a free
  integration frame. The vehicle bootstrap's synchronous Setup validation
  also reserves an optional integration frame after sequential prewarm.
  This closes the gap between async request completion and native rendering
  work: a completed ResourceRequest does not guarantee a free CPU frame.
- Optional frag grenade mesh and three visual-material prewarm requests
  also use the same low-RAM admission. Synchronous emergency Throw fallback,
  grenade physics, original material slots and visible model remain unchanged.
- Core player/terrain/collision runtime, essential weapon prewarm, aircraft
  input and base audio playback retain priority, bypassing this optional gate.

**Limitations:** Native terrain rendering, shader compilation, scene bootstrap,
physics MeshCollider cooking and Unity driver/device crashes still require
profiling. Scheduling alone cannot eliminate an individual multi-millisecond
house/model instantiation; the worst single hierarchy may need separate
incremental construction or prebuilt prefabs after live profiling. The
algorithm is intentionally non-allocating on each admission attempt, but
Unity import engine work still happens underneath it.

## Third-pass low-memory recovery and URP safety audit

Native memory reclamation is a separate concurrency risk from routine asset
prewarm. On an Android low-memory event the existing runtime correctly queues
`Resources.UnloadUnusedAssets()` without waiting on the render thread, but
independent optional imports and 3D hierarchy construction previously
continued during that asynchronous cleanup. The gate now holds a reference to
that `AsyncOperation`, rejects optional load/build *attempts* while it is
incomplete, and automatically resumes once `isDone` becomes true. This
applies even to larger Android phones after an actual critical-memory event;
essential terrain, camera/input and other non-optional scene activity are
not blocked. No active game objects are destroyed by the gate.

The native URP High asset's full-resolution Opaque/Depth textures were
investigated as a possible tile-GPU bandwidth cost. **Do not disable these
copies merely because the two handwritten shaders do not sample them.**
`Assets/TerrainDemoScene_URP/ShaderGraphs/WaterDepthBased.shadergraph`
contains both `UnityEditor.ShaderGraph.SceneColorNode` and
`UnityEditor.ShaderGraph.SceneDepthNode`. Without validated scene/material
usage and real Android visual checks, turning those features off could damage
water shading. Both source URP assets remain untouched; the runtime does not
override either texture requirement.

For evidence rather than guesswork, existing Android diagnostics now record
the **active** URP pipeline's MSAA, shadow cascades, main-light shadow atlas
dimension, shadow distance, depth/opaque texture requirements and HDR support,
plus three monotonic *attempted-admission* counters: optional work deferred by
memory cleanup, by same-frame competition, or by a still-active optional
import. Counters are not counts of unique jobs, elapsed milliseconds or
proof that a GPU pass was rendered. They are sampled only by existing
diagnostics and do not allocate a per-frame trace.

The saved High asset has a 4096-pixel main-light shadow atlas even though the
existing constrained-Android runtime already shortens shadow reach and
cascades. That can matter for render memory, but no atlas or asset quality
reduction is made without real GPU memory/visual evidence. Likewise no HDR,
MSAA, tree meshes, layer maps or scene color/depth dependencies were changed.

## Verification

Pass: 30 focused Python regression tests for global optional admission,
pre-existing frame gate, vehicle fallback preload, motorcycle prewarm,
rival model and animation prewarm. The complete Android source-regression
suite passed **214 tests** via `python3 -m unittest discover -s Tools/Validation
-p 'test_android_*.py'`; settlement source/integrity suite passed **13 tests**
via `python3 Tools/Validation/test_settlement_expansion.py`;
`git diff --check main...HEAD` passed.
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
