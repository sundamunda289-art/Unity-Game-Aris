# Android settlement facade integration across frames (2026-11-20)

## Source finding
On constrained Android hardware, `AarisSettlementExpansion.PlaceSitesAcrossFrames` previously constructed a complete authored house (including potential interactive modular surfaces) and immediately ran `AarisHouseFacadeTrim.Install` in the **same rendered frame**. The latter makes a new mesh, uploads its data through `UploadMeshData(true)`, adds its renderer/LOD, and may initialize the shared limestone texture/material. Existing frame admission covered the initial house but not this separate native facade pass. This is a plausible *source-level* coincident native work spike, not a measured diagnosis of phone freezes.

## Narrow fix
- Track each instantiated house immediately in `placed`, preserving original scene ownership and cleanup.
- On Android players only, yield at least one frame after base house creation, then acquire another slot from the **existing shared** `AarisMobileStreamingFrameGate` before the optional facade mesh integration; a refused slot retries without skipping a house.
- Bail out safely when the scene component or instantiated house is gone while waiting. Editor and non-Android paths retain their original immediate trim construction.
- Final meshes, textures, material, LOD, geometry, doors, physics, house locations, terrain, foliage, resolution, shadows and gameplay are unchanged. A house's decorative trim may appear one or more frames after its base prefab during Android scene bootstrap; no permanent art reduction is intended.

## Verification
- Run `python3 Tools/Validation/test_android_facade_frame_admission.py` and `python3 Tools/Validation/test_android_cross_system_hierarchy_budget.py` plus settlement and mobile hotpath regressions.
- Static tests prove source ordering and invariants, not a native Unity Editor compile, APK launch, GPU transfer time, or actual FPS. For a physical 4GB/6GB Snapdragon 680 device, compare cold and warm settlement entries using the same APK build flags and camera. Capture p95/p99 frame times, Android PSS, Unity main/render thread profiler, and visual A/B; watch for temporary facade pop-in.
- Do not merge changes in other agents' dirty Codespace working tree; update the canonical GitHub branch through a separate PR and only fast-forward a clean local checkout after verification.
