# Android car renderer/material validation buffer reuse — 2026-11-21

## Diagnosed source cost

While validating an imported real wheel prefab, `AarisVehicleWorld.HasRenderableWheelGeometry` previously created a new managed `Renderer[]` for each wheel. `HasCompatibleUrpMaterials` also generated a renderer array and a copy of `Material[]` for each renderer in the authored FBX. Vehicle spawning involves several native mesh, WheelCollider and asset integrations; avoidable allocations during these stages are a risk for transient GC stalls. There is no physical Snapdragon 680 profile demonstrating that this was the primary cause of the owner's stutter.

## Change

- Use two thread-local, caller-owned managed buffers: `List<Renderer>` for native component queries, and `List<Material>` with Unity 6000.3 `Renderer.GetSharedMaterials(List<Material>)` for authored material validation. These are lazily created on first use and reused across subsequent cars/wheels.
- The original `HasRenderableVehicleBody` still requires enabled, non-forced-off, active and nonempty visible mesh geometry. The exact URP Lit shader name and BaseColor/Metallic/Smoothness property checks are unchanged. Missing renderers/materials and invalid shaders still reject imported art safely.
- Both methods clear scratch references in `finally`, including invalid or failed native queries. No new worktree; no changes to cameras, terrain, trees, geometry, original material assets or paint, shadows, wheel radius, physics, renderer enable state or high-quality meshes.
- Controlled Mono C# fixture injects and compiles the actual three production methods, checking actual visible geometry vs hidden/empty/disabled/forced-off, skinned-wheel positive cases, valid/missing/invalid URP slots, repeated buffer reuse and cleanup on exceptions. Existing car silhouette/road/collision tests also execute under CI.

## Validation boundary

The changes reduce managed allocation opportunities in code but do not prove an FPS increase on a Snapdragon 680. CI's C# Unity API stubs are not a native Unity 6000.3.26f1 Player compile or Android IL2CPP, and the mobile GPU cost is not measured. Acceptance on device needs matched APKs, cold/warm car spawn GC Alloc, memory, p95/p99 frame time and equivalent vehicle screenshots.

Documentation: https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/renderer
