# Android physics catch-up / optional streaming admission (2026-11-21)

## Source-audited bottleneck

On the Snapdragon 680 / Adreno 610 target, a frame that executes multiple
FixedUpdate steps is already doing extra native physics work. The existing
`AarisMobileRuntime` counted fixed steps, and
`AarisMobileStreamingFrameGate` prevented overlap with essential rig,
aircraft, optional imports, collider toggles, low-memory cleanup, and asset
unload. It did **not** coordinate optional integration with general
FixedUpdate catch-up spikes. This is a *plausible scheduling collision*, not
a measured cause of the owner's freezes.

## Surgical Android-only fix

- `AarisMobileRuntime.Update` runs with execution order -300. After reading
  `physicsStepsBeforeRender`, it now signals the shared admission gate
  before optional streamers can start a new model hierarchy or import.
- `ReservePhysicsCatchupFrame` claims a low-end Android rendered frame when
  **three or more fixed ticks** have completed. Normal 0–2 tick frames
  continue unchanged. The gate uses only existing integer counters and
  `Time.frameCount`; no spatial scans, hierarchy walks or allocations.
- A persistent burst can defer optional work **at most twice consecutively**;
  the third heavy frame admits work normally. One subsequent 0–2 tick frame
  resets that streak. Duplicate calls in a single frame do not double count.
- The signal uses the existing device policy, including 6 GB phones with
  Adreno 610. Desktop, Editor, high-end Android GPUs and essential physics
  are not throttled. Normal fixed timestep, collisions, gameplay, immediate
  visible models, trees, terrain, textures and resolution are unchanged.
- The existing gate automatically keeps optional model imports, new
  hierarchies and nonessential diagnostics off a reserved frame; no new
  per-streamer coordination is needed.

## Verification

- `Tools/Validation/StreamingAdmissionTests.cs`: controlled C# fixture
  exercises the **actual** production gate under Android, Editor and desktop
  compile configurations, including 4/6 GB Adreno 610, duplicate signals,
  ordinary physics pacing, catch-up streak reset, starvation and fast-device
  bypass.
- `Tools/Validation/test_android_physics_catchup_stream_admission.py`:
  source policy checks and deterministic bounded-admission model.
- `.github/workflows/aaris-android-physics-catchup-admission-qa.yml`:
  pull-request source regressions, Mono compilation/execution and C# parsing.
- Source checks and C# stubs cannot measure *actual* engine Physics, URP GPU,
  native asset integration, thermal throttling or 4 GB Android FPS.

## Scope boundaries and next profiling gate

The four native TerrainData tiles remain loaded together; this patch is
**not** true additive terrain chunk streaming. Unity Addressables, interiors
split by zones, HLOD/impostors, baked occlusion and graphics-state shader
warmup require authored independent assets, camera/material verification and
physical-device metrics before safe activation. In particular, removing
distant terrain, buildings or vegetation blindly violates visual fidelity.

Before calling this an FPS fix, capture an identical cold start and driving
route on the actual Snapdragon 680 phone: p50/p95/p99 frame time, FixedUpdate
step burst count, biggest stalls, native/managed/PSS memory, logcat ANR/LMK,
first-use shader stalls and visual A/B comparisons. Confirm optional cars and
scenery still appear after long slow periods. Do not infer FPS improvement from
source tests alone.

### Public engineering references

- Unity 6 asynchronous upload timeslice and buffer:
  https://docs.unity.com/en-us/engine/6000.0/manual/analysis/graphics-performance-profiling/loading-texture-mesh-data-asynchronously/configure-asynchronous-upload-pipeline
- Unity Addressables async handles:
  https://docs.unity.cn/Packages/com.unity.addressables%402.2/manual/AddressableAssetsAsyncOperationHandle.html
- Unity 6 URP GPU Resident Drawer:
  https://docs.unity.com/en-us/engine/6000.7/manual/analysis/graphics-performance-profiling/in-urp/reduce-draw-calls-urp/reduce-rendering-work-on-cpu/gpu-resident-drawer
- Android Unity Adaptive Performance:
  https://developer.android.com/games/engines/unity/unity-adpf
