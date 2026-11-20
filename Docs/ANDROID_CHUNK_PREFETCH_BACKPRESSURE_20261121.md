# Quality-preserving additive chunk admission — 2026-11-21

## Verified starting point
`main` contains one enabled authored scene: `Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity`. `AarisAdditiveChunkStreamer` is **opt-in** and requires authored additive chunk scenes registered in the build profile. It does not repartition the current terrain, static buildings, or vegetation. Until such scenes exist and the component is configured, this change does **not** claim live map streaming gains on the current APK.

## Change
- A configurable default of **two speculative resident scenes** on *constrained Android* prevents accumulated prediction-only cells from causing peak memory spikes while crossing multiple authored chunks. This cap never rejects a chunk inside the player's actual load radius. Existing retained/urgent cells, world textures, native vegetation, shadow settings, distant silhouettes, and collision fidelity are untouched.
- After an additive scene finishes loading, its observed native integration frame reserves the existing shared physics/streamer frame slot. After unloading, the observed native teardown frame reserves that frame and the next against competing optional model builds. This supplements the already present operation-start fence and shared global one-in-flight policy.
- No new allocations in Update, no synchronous `Resources.UnloadUnusedAssets`, no new worktree, and no scene reauthoring.

## Validation / limitations
`Tools/Validation/AdditiveChunkStreamingTests.cs` exercises both the Android-defined production script and desktop path through the isolated .NET simulation. `Tools/Validation/test_additive_chunk_streaming.py` asserts source contracts. These checks verify logic, **not** actual Unity editor compilation, Vulkan/OpenGLES GPU driver behavior, phone temperatures, frame times, FPS, memory, or visual quality. Only real Snapdragon 680/Adreno 610 profiling can establish device-level gains.

Further production adoption needs authored chunk scenes + bootstrap distant HLOD impostors, then on-device visibility/occlusion and peak native memory testing. Untested URP depth/opaque copies, texture scale, foliage counts, shaders, and map clarity were not degraded.

References:
- https://docs.unity.com/en-us/engine/6000.0/script-reference/unityengine/scenemanagement/scenemanager/loadsceneasync
- https://docs.unity.com/en-us/engine/6000.0/manual/analysis/graphics-performance-profiling/loading-texture-mesh-data-asynchronously/configure-asynchronous-upload-pipeline
- https://docs.unity.com/en-us/engine/6000.3/manual/analysis/graphics-performance-profiling/in-urp/reduce-draw-calls-urp/reduce-rendering-work-on-cpu/gpu-resident-drawer
