# Android speculative scene residency guard (2026-11-21)

## Scope and actual status

This is a small, **opt-in** change to `AarisAdditiveChunkStreamer.cs`, not a scene migration and **not a measured FPS improvement**. The existing four native TerrainData tiles remain in `TerrainDemoScene.unity` and the build profile contains only that main scene. There are no separately authored, built chunk scenes to stream today.

The goal is to avoid memory spikes from stacking optional speculative scene prefetches once separately authored, quality-preserving world chunks are available. No trees, terrain, buildings, materials, water shaders, resolution, textures, collision shapes, scene files, or gameplay systems were modified.

## Verified code behavior

- `maxSpeculativeResidentScenes = 2` is a serialized optional load cap for scenes **owned by this streamer**. It does not count externally owned scenes and is deliberately not a hard limit on essential overlapping geometry.
- Prefetch-only loads are deferred when the cap is reached. An actually nearby cell (within the original entry margin) **always bypasses the cap**, preserving the priority of visible scene geometry and physics.
- An owned scene already outside the original larger unload margin is retired ahead of a speculative-only request, never ahead of an urgent current-player request.
- A predicted point strictly inside a candidate cell has zero squared distance. The old numeric priority scheme allowed this to tie the entry threshold, accidentally treating **predicted-only** loads as urgent during stale-scene eviction. The new `loadIsUrgent` value is determined from the **actual** position, not from its numeric priority.
- Original velocity prediction, teleport rejection, hysteresis, one-async-operation-at-a-time behavior, background priority, Android shared work gate, completion fence and cooldown are preserved.
- No polling allocations, extra native queries, synchronous waits, automatic scene attachment or new runtime components are added.

## Verification

`Tools/Validation/AdditiveChunkStreamingTests.cs` now tests cap-limited speculative loads, urgent nearby loads past the cap, and the zero-prediction-distance priority collision, in addition to the existing operation/hysteresis/ownership tests. The existing C# source and Android-preprocessor stub regression workflows should run on the PR. Such tests are not equivalent to a Unity Editor build, real Android GPU frame trace, real memory residency measurement or visual quality certification.

## Remaining work before claiming smoother open world

1. Profile the actual Snapdragon 680 handset for frame p50/p95/p99, Unity main/render/GPU times, Android process PSS, shader compilation stalls and >10-minute thermals.
2. Using a **licensed native Unity 6000.3.26f1 Editor**, partition truly independent world cell assets and dependencies, keeping nearby original detail and accurate distant HLOD/proxies. This must include preserving terrain seams and native physics. Do not merely disable distant GameObjects and claim memory reclamation.
3. Add those scenes to the build profile, attach/configure the opt-in streamer, and test high-speed travel, teleport, crossings, buildings/interiors and HLOD imagery against the current map.
4. If real traces identify shader first-use stalls, capture and prewarm *used* Unity 6 GraphicsStateCollection graphics states. If bandwidth dominates on Adreno 610, profile overdraw, depth/opaque render-target operations, SRP Batcher and compatible GPU instancing before changes. The scene's water shaders need depth and scene color: do not blindly disable those textures.
5. Merge only after native Unity compilation and same-phone before/after performance and image comparisons. A CI-green source contract alone does not verify Android smoothness.

References:
- Unity async scene loading: https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/scenemanagement/scenemanager/loadsceneasync
- Unity async GPU upload ring: https://docs.unity.com/en-us/engine/6000.0/manual/analysis/graphics-performance-profiling/loading-texture-mesh-data-asynchronously/configure-asynchronous-upload-pipeline
- Unity 6 PSO prewarming: https://docs.unity.com/en-us/engine/6000.0/manual/materials-and-shaders/shaders/shader-troubleshooting/shader-reduce-stalling/shader-prevent-stutter/shader-pso-trace-warming/shader-prewarm
- Unity 6 occlusion and bake limitations: https://docs.unity.com/en-us/engine/6000.3/manual/cameras/occlusion-culling/getting-started
- URP draw call strategies: https://docs.unity.com/en-us/engine/6000.6/manual/analysis/graphics-performance-profiling/reduce-draw-calls/optimizing-draw-calls-choose-method

## Multi-agent isolation

This change is committed only on its dedicated GitHub branch, with the single production streamer script, its isolated regression test file and this new document. No Codespace git reset, discard, staging, scene import, LFS hydration or synchronization has occurred. A Cloudflare registry stage of `connected` with an old last-seen timestamp is **not** a verified live terminal.
