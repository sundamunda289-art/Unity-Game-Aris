# Android 6 URP: reuse real car bounds query buffers (2026-11-21)

## Observed source-level risk

The vehicle spawn path repeatedly invokes `AarisVehicleWorld.TryGetVisualBoundsLocal` when fitting a true imported car chassis, correcting a source FBX axis and measuring each of four tyres. Before this change, every call issued two native component queries that materialized managed `MeshFilter[]` and `SkinnedMeshRenderer[]` results. Those short-lived arrays add avoidable managed allocation pressure exactly when a car hierarchy is instantiated and PhysX WheelColliders are being initialized. **The project's reported Snapdragon 680 frame stalls have not been causally attributed to these allocations through a real device capture.**

## No visual/physics regression

Unity 6000.3 `Transform.GetComponentsInChildren<T>(true, List<T>)` can fill caller-owned lists rather than allocating new results. Two thread-local scratch lists are lazily allocated on their first actual bounds query and reused thereafter. A `finally` clears both lists, including exceptional native queries; disabled/inactive/hidden helper geometry is still excluded using the exact original guards, and visible MeshFilters and SkinnedMeshRenderers are still combined in chassis coordinates using unchanged `AccumulateLocalBounds`. Source meshes, authored textures, wheel pivot placement, collider dimensions, lights, car caps, terrain, foliage and rendering distances are all unchanged. No new per-frame work, no new worktree, and no change to the active dirty Codespace branch.

## Test strategy and limits

The new managed Mono harness injects and compiles the **actual production** car-bounds method and projected-bounds helper in Unity API stubs. It checks 80 successive mixed mesh/skin/hidden-geometry queries against known original bounds, empty/invalid sources, exception cleanup and recovery, and thread-static scratch reference clearance. Existing car visual/tyre placement/physics validation and production C# parser checks run as CI.

These source and controlled C# checks are not equivalent to a Unity 6000.3 Editor compile, Android IL2CPP/Adreno profiling, visual screenshots or measured frame-pacing improvement. On the original handset, compare allocations and p95/p99 frame times during the same vehicle spawn routes on the previous versus updated APK, and confirm original wheel/tire and body screenshots.

Official Unity 6000.3 API: https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/gameobject/getcomponentsinchildren
