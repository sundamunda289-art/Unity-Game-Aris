# Opt-in additive world chunks — 2026-11-21

## Verified scope

New opt-in additive scene streamer and zero-allocation XZ distance/prediction policy. NOT attached to current map: the four original TerrainData tiles are still inside TerrainDemoScene.unity. The current game will not suddenly gain FPS or lower memory until genuine separately authored world cells exist. No authored scenes, vegetation, cars, textures, materials, physics, shaders, render scales, or tree density were changed.

## Runtime safeguards

- Independently authored scenes must be included in Unity Build Profiles. Reject missing, duplicate and host scene paths on initialization.
- Load within 90m of an authored cell AABB; unload only beyond 170m. 1.25s predictive lookahead is capped at 35m/s; teleports above 90m/s and long sample gaps are not predicted. Predictive loading never initiates farther than the exit margin.
- Only one async scene load or unload is in flight, with a completion-frame fence and cooldown. Use the existing constrained-Android frame gate for new optional scene loads and reserve a teardown frame for unload. Essential gameplay and terrain never enter this queue.
- Scenes that were already loaded by another system are not unloaded or loaded twice. Geometry/visual quality is not automatically lowered, and no synchronous wait or full-world Find operation runs each frame.
- Unity additive SceneManager.UnloadSceneAsync removes a scene, but does not guarantee unloading shared/Resources assets. This intentionally avoids costly Resources.UnloadUnusedAssets on every transition. Genuine bundle residency and explicit reference-counted unload should use Addressables when authoring chunks.

## Editor-only integration checklist

1. Capture baseline real-handset Profiler p50/p95/p99, native PSS, CPU/GPU timings, longest stall, temperature and logcat under cold-start, forest, driving, village, teleport and 10+ minute thermal soak.
2. Author genuine independently loadable scenes using Unity Editor; keep core terrain and essential collision unchanged until seams and shared dependencies can be verified.
3. Keep high-quality distant settlement/tree HLOD/impostor proxies in the permanent bootstrap scene. Never place the only visible distant silhouette in an unloadable cell. Review 20m/100m/300m/500m screenshots and occlusion/interior visibility.
4. Attach AarisAdditiveChunkStreamer to one persistent host object; assign player and each chunk's full built scene path, center and XZ half-extents; only then enable streaming.
5. Run Android build and visual/thermal regression. Unity async scene activation can still cause spikes; source tests do not measure hardware FPS or asset memory. Shader PSO warmup and automatic HLOD creation are NOT implemented here.

## Tests and multi-agent isolation

Only new scripts, their Unity metadata, the source tests, an isolated CI workflow and this document are added. Existing gameplay/scene files and other agent edits are untouched; no Codespace workspace access assumed. Mono stub C# tests compile production files in desktop and Android compile configurations and exercise distance/prediction, teleport rejection, single in-flight operation, hysteresis, external scene ownership. Unity Editor compile and physical Android phone profiling still required before activating chunks.

Official references:
https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/scenemanagement/scenemanager/loadsceneasync
https://docs.unity.com/en-us/engine/6000.5/script-reference/unityengine/scenemanagement/scenemanager/unloadsceneasync
https://docs.unity.com/en-us/engine/6000.3/manual/cameras/occlusion-culling/getting-started
https://docs.unity.com/en-us/engine/6000.3/manual/materials-and-shaders/shaders/shader-troubleshooting/shader-reduce-stalling/shader-pso-trace-warming/shader-pso-introduction
https://developer.android.com/games/engines/unity/unity-adpf
