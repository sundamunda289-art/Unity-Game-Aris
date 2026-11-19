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

## Fourth-pass startup CPU overlap and exact HUD ink optimization

The original Android `AarisMobileRuntime.Update` may emit an urgent
`frame-stall`, `low-memory` or `asset-unload-complete` report in the same
rendered frame as scheduled `startup` or 15-second `sample` diagnostics.
Each report sorts up to 180 rolling frame intervals and serializes profiler
and memory data. `CanRecordPeriodicDiagnostics()` now lets the non-urgent
reports run only when no other report has already used that frame. Scheduled
deadlines stay pending until an available frame; **all urgent evidence remains
immediate**. Existing `ProfilerMarker` scopes are unchanged.

Source inspection also found that `AarisHudGlyphs.BuildAtlas` computes
five complex antialiased line/circle glyphs through CPU Vector2 distance math
while the combat HUD is created at scene bootstrap. The new
`OutsideGlyphInk` fast-path skips those distance calculations only for
pixel coordinates rigorously outside **every** original line and circle,
including the full SDF antialias transition. Independent Python parsing of
all authored strokes verifies the bounds mathematically, then checks all
128px sample locations. Exactly **40,827 / 79,380 (51.4%)** of the five
glyphs' rasterized interior pixels fall outside these rectangles. This is
a reduction in **eligible expensive SDF evaluations**, not a measured
51.4% improvement in total atlas time, FPS, startup duration, or GPU use.
The sprites, colors, AA edge math, canvas, texture dimensions, atlas
material and gameplay touch hitboxes are unchanged. The atlas generator
also records one one-time `AarisHudGlyphs.AtlasBuildMs` duration in Android
JSON telemetry (`hudAtlasBuildMs`, -1 when not built), separating first-frame
HUD CPU/rasterization/upload cost from plane model and GPU timings without
timing each pixel or adding instrumentation to LateUpdate.

`AarisOfflineDropVisuals` still selects the original high-quality cargo
plane. Its `CargoPlaneRealistic.obj` source is roughly 7 MB and is
synchronously resolved at flight creation; its Unity player-compressed
cost is not established by source OBJ size. The existing
`PlaneModelResolveMs` / `PlaneInstantiateMs` telemetry should be
measured on actual devices before changing flight visuals or startup UX.

## Sixth pass: single asynchronous loot manifest for guns and medical pickups

Two independent gameplay consumers referenced the same
`AarisWeapons/loot_candidates` TextAsset. Previously, the weapon world
deferred its heavy gun **models** but synchronously loaded/parsed the
candidate manifest in `Initialize`; the tactical HUD could call the same
`AarisLootDocument.Load` while streaming first medical/helmet pickups.
If that happened first, the synchronous resource import occurred during
landing rather than as part of a staged bootstrap. This was a genuine source
path to **potential** frame-time contention, not a measured Android stall.

`AarisLootDocument.LoadForAndroidAsync` now imports the candidate TextAsset
once, asynchronously, following the first aircraft render frame, and
performs the managed `JsonUtility.FromJson` on the **next** frame after
native request completion. It publishes an immutable cached document and a
finished flag. Both the weapon world and the tactical medical streamer
consume that same cache on Android and do not retry a failed request through
synchronous `Resources.Load`. A ground-start medical scan safely returns
until the manifest has finished, then scans all original authored positions
in their original order. The established Editor/desktop synchronous loader
remains available, and a missing/invalid manifest still fails closed under
existing schema checks.

The smaller `AarisWeapons/weapon_catalog` definition file is now also
loaded by `Resources.LoadAsync<TextAsset>` in Android match prewarm, with
its JSON validation in a separate frame before native weapon prefab loads.
Both authored manifest loads retain **normal gameplay priority (0)** rather
than the optional cosmetic streamers' reduced request priority (-1).
Desktop and explicit essential synchronous callers retain the original
fallback. Five authored gun categories, weights, match seeding,
pickup/ground validation, physics reach, meshes and textures remain intact.

Android diagnostics additionally report one-time JSON decode times
`lootCandidateParseMs` and `weaponDefinitionParseMs` (default -1 until
attempted). These **exclude** native asset import, upload and GPU work; they
are not an FPS number. No per-frame timing instrumentation was added.

## Seventh pass: shared async car + roadside-bike plan

Source audit found two gameplay systems reading the same
`AarisVehicles/vehicle_candidates` JSON on Android: the parked-bike
`Start()` read it synchronously during boot, while the car-world `Setup()`
read it synchronously again **after** an asynchronous car model prewarm.
The latter could still schedule native Resources hydration on the integration
frame that the earlier prewarm was trying to keep light.

`AarisVehiclePointManifest` now owns one Android
`Resources.LoadAsync<TextAsset>` request and retains the completed asset.
Both independent coroutines join that **same pending request**, including when
they start on the same frame or in reverse order. The manifest uses normal
gameplay priority (0), not optional model priority (-1). A missing document
fails closed in each consumer's original validation without a repeat blocking
read. A subsystem-registration reset prevents keeping a stale manifest across
play sessions.

The bike world now consumes that preloaded document and moves its original
road-plan JSON parse off the first aircraft/terrain frame. It still filters
`kind == "roadside"` and retains the same every-other-road-point selection,
maximum three visible bikes, placement collision tests and original source
model. The car world's constrained Android prewarm resolves the road document
before its separate catalog/model requests, then hands the preloaded text
assets into the **unchanged** schema/physics/renderer validation method.
Editor / unconstrained Android retains the original synchronous `Setup()`
wrapper, and there are no changes to authored road coordinates, 44 planned
car slots, terrains, materials, pipeline quality, or player controls.

The five new source/simulation tests cover same-frame/late/reversed
requests, single native import admission, Android no-sync handoff, Editor
fallback and the preserved 2km road-plan validation. These are not real
Unity frame-time measurements.

## Eighth pass: optional Foley admission and FBX clip-load evidence

An audit found that the Android sound pipeline already loads the six
aircraft/drop cues synchronously before the first playable frame, then
imports the remaining thirty authored base cues sequentially with
`Resources.LoadAsync<AudioClip>`. Those **gameplay cues are unchanged**.
The remaining fifteen CC0 *optional Foley variants* (five grass footsteps,
five concrete footsteps, three punch impacts, soft landing and metal impact)
used independent async requests that could overlap car, bike, rival,
settlement or grenade native imports and a critical-memory
`Resources.UnloadUnusedAssets` pass.

`AarisAudioDirector.LoadOptionalFoleyAsset` now uses the shared Android
`TryStartOptionalLoadAsync<AudioClip>` gate. If admission is denied, the
coroutine yields and **retries the identical variant** without losing its
slot or changing resource path. The original base `SFX/` cue remains the
fallback until its optional CC0 variant is available. The extra prewarm
runs only after all base sounds, and after each request completes it yields
one frame before starting another. A disabled/destroyed audio component
cannot continue starting new cosmetic imports.

The player rig's Android bootstrap still uses the original
`Resources.LoadAll<AnimationClip>` calls because authored FBX animation
sources may contain multiple imported clips. Converting that API to a
single `Resources.LoadAsync<AnimationClip>` could drop animations.
Instead two small, **read-only stopwatch measurements** now wrap the
existing synchronous loads: `characterMocapGroupLoadMs` (largest natural
mocap source load), `characterMaxClipGroupLoadMs` (largest individual
clip-source load), plus `characterClipGroupLoadCount` in the existing
Android JSON diagnostics. All values reset on subsystem registration;
-1 means no such import yet. The timing calls run only when an animation
clip group actually loads, **not per render frame**, and are *not*
GPU-skinned animation playback time or measured overall startup duration.

No changes to sound samples/codecs, animation names, motion retargeting,
foot IK, material/mesh art, player steering, world visuals, skinning or
Unity project settings. The new markers are intended to distinguish
single-frame FBX subasset hydration from native GPU/URP stalls on a real
device; they do not establish the root cause by themselves.

## Ninth pass: protect the essential character-rig integration frame

Source inspection found an unavoidable main-thread cluster in
`AarisCharacterVisual.TryInstantiateCharacter` and
`TryPrepareAnimatedBody`: instantiate a multi-part Humanoid prefab,
configure the Animator/retargeted skeleton and `PlayableGraph`, evaluate
the first pose, then `SkinnedMeshRenderer.BakeMesh` to validate actual
skinned vertex positions before replacing the saved original player model.
The BakeMesh check is **not decorative**: it rejects bogus imported bind
geometry and prevents the player appearing metres/kilometres away from the
CharacterController. It was deliberately **not removed**.

On RAM-constrained Android (`SystemInfo.systemMemorySize` 1–4608 MB),
after all existing animation-clip prewarm steps, the rig coroutine now
calls `AarisMobileStreamingFrameGate.ReserveNextFrameForEssentialRig()`.
If reservation is accepted, it yields exactly one frame before constructing
that essential rig. During the reserved frame, **new optional**
model/material hierarchy builds and new optional native asset imports
(car, bike, rivals, houses, Foley) are denied and retry later. Existing
in-flight requests are not cancelled; player movement, ground collision,
camera, original placeholder visibility and essential audio/loot remain
unblocked. Larger-RAM devices have no additional frame delay.

Existing Android diagnostics now expose:
- `characterRigInstantiateMs`: native `Instantiate(source, ...)` cost
- `characterRigGraphSetupMs`: setup and initial PlayableGraph evaluation
- `characterRigBakeMeshMs`: native CPU skinned-mesh validation cost
- `optionalStreamRigFrameDeferrals`: optional admission attempts refused
  on the reserved frame (not elapsed milliseconds or unique jobs)

These timers run only while attempting the player rig and preserve its
fallback selection, first-pose validation, animation clips, weights,
mesh scale, foot IK and URP materials. Six regression tests confirm the
constrained-only reservation, optional retry behavior, unchanged original
geometry checks and diagnostics. No GPU driver, shader variants,
`QualitySettings` or serialized URP settings were modified.

## Verification

Pass: 30 focused Python regression tests for global optional admission,
pre-existing frame gate, vehicle fallback preload, motorcycle prewarm,
rival model and animation prewarm. The complete Android source-regression
suite passed **247 tests** via `python3 -m unittest discover -s Tools/Validation
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
