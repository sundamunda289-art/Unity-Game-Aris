# Android authored wheel integration frame fence — 2026-11-21

## Verified code-level problem
`AarisVehicleWorld.PumpAuthoredBodyLoads` integrated a completed async
authored body, then `CommitAuthoredBody` synchronously loaded the separate
`AarisVehicles/Authored/CivilianWheel` prefab for the authored sedan. A
single 300ms poll could therefore bind a vehicle body and perform another
native mesh/texture import in the SAME rendered frame, despite the shared
optional-model admission gate. This is a source-level contention risk, not
a measured Adreno 610 stall attribution.

## Safe, quality-preserving change
- The authored sedan body is still prewarmed at the existing 150m radius.
- On Android builds, when the body has no embedded wheel set but its catalog
  names a separate authored wheel, retain the *completed* body request as the
  in-progress sentinel, then request the identical wheel through the existing
  `AarisMobileStreamingFrameGate.TryStartOptionalLoadAsync`.
- Start the wheel on a later poll/frame, and commit the two original PBR
  prefabs together only after wheel import completion and a fresh shared
  frame admission. If it is absent/incompatible, retain the existing safe
  fallback vehicle art and complete that prewarm rather than retry forever.
- Count in-flight authored wheels toward the original two-request limit.
  The original 44 vehicle plans, ten active-car cap, 105m load, 160m unload,
  original textures/meshes, collider setup and wheel appearance are unchanged.
- Editor/desktop retains the old synchronous asset authoring path.

## Validation and limits
A PR-scoped independent regression validates request fencing, model readiness,
visual-preservation contracts and C# syntax via tree-sitter. Existing mobile
gameplay regression also runs on the production-script change.

This does NOT create independently loadable terrain chunks, prove native Unity
Editor compilation, Android APK correctness, thermal safety, GPU PSO warmup,
or guarantee a higher FPS. Complete the remaining native Unity
6000.3.26f1 compile, handset 4–6GB Profiler p50/p95/p99, cold-start,
vehicle approach/retirement and 10+ minute thermal-soak tests BEFORE
claiming performance improvement.

Relevant official Unity references:
- https://docs.unity.com/en-us/engine/6000.0/manual/analysis/graphics-performance-profiling/loading-texture-mesh-data-asynchronously/configure-asynchronous-upload-pipeline
- https://docs.unity.com/en-us/engine/6000.3/manual/analysis/graphics-performance-profiling/in-urp/reduce-draw-calls-urp/reduce-rendering-work-on-cpu/gpu-resident-drawer
