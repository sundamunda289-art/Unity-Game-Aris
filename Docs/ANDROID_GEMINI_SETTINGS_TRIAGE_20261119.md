# Game-Aris Unity 6 / Snapdragon 680 — third-party setting claims triage (2026-11-19)

## Evidence standard

Inspected connected Codespace `/workspaces/Unity-Game-Aris`, the serialized
ProjectSettings and the current source on review branch
`perf/android-global-async-io-gate-20261119`. This is **not** a Snapdragon 680
GPU trace, Android crash log, Unity binary compile or completed playtest. A
serialized flag is not itself evidence that a given 3–30 s stall originated
there. The compared third-party report read canonical main at `427083c`;
this draft review branch contains additional fixes that it did not inspect.

## Checked claims and actions

| Claim | Verified source fact | Safe decision |
| --- | --- | --- |
| Empty preloaded shader collection proves compile stalls | `GraphicsSettings.asset` has `m_PreloadedShaders: []`; `m_PreloadShadersBatchTimeLimit: -1`. `AarisModularHouse.Prepare` did use `Shader.Find("Universal Render Pipeline/Lit")` only when shared materials were first created, **not once per frame or house**. Neither value measures GPU driver compilation. | Prefer existing imported prefab material's retained Lit shader reference first, preserving the original Shader.Find fallback if material/renderer is missing or different. Do not manufacture a ShaderVariantCollection or call WarmUp blindly: correct variants and loading-screen time require actual Unity/Android evaluation. |
| CPU skinning and 4 weights explain frame drops | `ProjectSettings.asset` has `gpuSkinning: 0` and `meshDeformation: 0`; `QualitySettings.asset` has `skinWeights: 4`. PlayableGraph animation alone does not identify which specific CPU/GPU phase is expensive. | Profile animation, CPU skinning and GPU first. A universal 2-weight cap may **change deformations**; not a zero-visual-impact fix. Unity 6's `PlayerSettings.gpuSkinning` is tied to `meshDeformation`. |
| Desktop quality forced on Android | One `Aaris Ground` quality tier, Android and Standalone both default to index 0. It sets `anisotropicTextures: 2`, `softVegetation: 1`, `streamingMipmapsMemoryBudget: 384`, `streamingMipmapsMaxFileIORequests: 16`. | Android runtime **already** recognizes constrained GPUs including Adreno 610 and caps the active URP pipeline to 2x MSAA, shadow distance to ~120 m, cascade count to 2, mip budget to <=224 MB and simultaneous mip file reads to <=8. This preserves original source QualitySettings and scene assets. Do not claim the saved tier is the effective runtime tier. |
| Bake all collisions, disable logs, force sustained mode | `ProjectSettings.asset` has `bakeCollisionMeshes: 0`, `usePlayerLog: 1`, `AndroidEnableSustainedPerformanceMode: 0`. | These are correct settings observations, **not** proof of runtime MeshCollider cooking, logging I/O stalls or thermal throttling. Preserve crash diagnostics while investigating. Enable sustained mode only after baseline and 10+ minute device comparisons; it can trade peak for steadier clocks. Do not change physics import/build options without real prefab and build-size review. |
| Turn on terrain paint CPU copy release | `AarisMobileRuntime.releaseTerrainPaintCpuCopies = false`. Its existing `Start` deliberately logs `AARIS_TERRAIN_PAINT_RELEASE_DISABLED` if set to true; it **does not actually release the Unity-managed terrain alphamap GPU texture copies**. | Setting true is not a RAM fix. Preserve Unity 6 TerrainData splatmaps and native imported paint; check real memory counters before changing their readability or upload lifecycle. |
| Disable URP HDR/depth/opaque/MSAA | Actual High URP asset in Codespace: HDR 1, MSAA 4, main atlas 4096, depth 1, opaque 1, render scale 1.0, 1000 m shadow distance, SRP Batcher on. Android runtime already modifies supported constrained settings. | **Do not** disable depth/opaque textures blindly: `WaterDepthBased.shadergraph` contains SceneColorNode and SceneDepthNode; water effects can depend on them. No blanket HDR change without before/after rendered screenshots and GPU timing. |
| Runtime Combine / instancing every house is free | `AarisModularHouse` already enables instancing on shared base materials and runs `CombineStaticSurfaces` by material using `Mesh.CombineMeshes` followed by `UploadMeshData(true)`, while leaving interactive panels separate. More combining is **not** automatically an optimization; it can spike imports/GC and hurt culling. | Retain the existing combined surfaces and original house physics. Profile per-house draw calls before adding another `StaticBatchingUtility.Combine` pass. |

Official Unity references:
- https://docs.unity.com/en-us/engine/6000.3/script-reference/unityeditor/playersettings/gpuskinning
- https://docs.unity.cn/Packages/com.unity.shadergraph%4017.0/manual/Scene-Color-Node.html
- https://docs.unity.cn/Packages/com.unity.shadergraph%4017.4/manual/Scene-Depth-Node.html
- https://docs.unity.com/en-us/engine/6000.5/manual/render-pipelines/universal-render-pipeline/urp-reference/universalrp-asset

## Additional confirmed safe improvement

`AarisSettlementExpansion.LoadExpansionCatalogAcrossFrames` now submits
the **optional prefab-referencing** ScriptableObject to the already-reviewed
`AarisMobileStreamingFrameGate`. It yields and retries when constrained-device
imports are in flight or the low-memory reclaim operation is active. Its small
JSON world plan still uses the original independent async read, and the
21-house/watchtower placement loop is unchanged. Material preparation in
`AarisModularHouse` first reuses the retained prefab's URP Lit shader and
preserves one-time `Shader.Find` fallback for incompatible or absent models.

No direct edits to serialized QualitySettings, GraphicsSettings, ProjectSettings,
URP source assets, world textures, native trees, mesh art, collision geometry,
character weights, shader variants or scene placement.

## Release acceptance

A real 4 GB Adreno 610 phone remains the gate for:
- Before/after Profiler CPU main/render and GPU frame timings, especially
  p95, p99, single worst frame and full device freeze/ANR evidence
- Frame-time captures during first flight, landing, dense foliage camera turns,
  authored house streaming, rapid travel and 10+ minutes of thermal soak
- Player/AI animation appearance and performance before any meshDeformation
  or skin-weight change
- Water translucency/refraction/depth edge fidelity and original forest/map
  visibility before changing URP depth/opaque/MSAA/HDR
- Process PSS, graphics allocations, cold-start import stage timings and
  any actual Android crash/LMK records

Deterministic Python/source checks are **not** Unity compilation or measured
performance. Keep PR draft until device acceptance.
