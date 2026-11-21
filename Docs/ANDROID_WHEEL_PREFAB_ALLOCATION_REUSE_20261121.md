# Android wheel-prefab integration allocation reduction — 2026-11-21

## Target and source finding

Original `AarisVehicleMotor.AttachWheelModels` instantiates four authentic wheel meshes for each compatible car. During that one-time integration it allocated a fresh `Collider[]` and `Renderer[]` per wheel, then fresh identical `Material[]` arrays for each palette-renderer. These managed allocations overlap unavoidable Unity native wheel hierarchy and collider work when several roadside cars become visible. They are a *plausible* contributor to spawn-frame GC, not a measured explanation for Android FPS drops.

## Quality-preserving change

- Use instance-owned reusable `List<Collider>` and `List<Renderer>` buffers through Unity 6000.3 `GameObject.GetComponentsInChildren<T>(true, List<T>)`. The same active/inactive children and original geometry checks still apply.
- Disable imported visual-only colliders as before. `finally` clears both buffers even when a wheel source is invalid, preventing retained destroyed objects on retries and after vehicle retirement.
- Cache one immutable `Material[]` layout per exact `VisualSubmeshCount`. Four matching wheel meshes use the same original shared palette with all original slots. If a later explicit attachment supplies a different palette, the layouts are invalidated; authored URP materials still bypass paint replacement entirely.
- Preserve WheelCollider physics authority, visual pivot offsets, world-pose syncing, prefab selection, wheel radius, authored textures/material assets, engine sound, roads, vegetation, lighting and original car cap. No visual quality downgrade, no new loops in `Update`, no new worktree.

## Evidence and acceptance

New `Tools/Validation/run_wheel_prefab_allocation_csharp.py` extracts and compiles the actual production method under controlled C# stubs; test cases cover four wheels, imported collider disabling, palette layout sharing, palette change, authored URP material preservation, invalid-mesh rejection, and subsequent retry without retained handles. GitHub Actions also runs original visible-body, axle/road geometry, wheel-pivot and full vehicle system source contracts. Original `main` builds not changed outside the scoped method and scratch fields.

This is not a native Unity 6000.3.26f1 compile or measured Snapdragon 680/Adreno 610 FPS gain. On-device acceptance requires matching APKs, CPU/GC frame samples during multiple car spawns, memory/PSS, and screenshots confirming tire geometry and palette equality. Untested Unity behaviour is not reported as successful.

Official Unity 6 API: https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/gameobject/getcomponentsinchildren
