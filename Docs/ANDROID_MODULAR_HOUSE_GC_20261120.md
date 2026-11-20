# Android modular-house spawn GC audit — 2026-11-20

## Source evidence
The 2 km native map already uses low-end Android shared streaming admission,
async resource loading, source-preserving prefab LODs, and frame-budgeted
terrain/collider queries. An independent new chunk manager or duplicated
streamer would compete with that existing system.

The four authored interactive modular houses use `AarisModularHouse.Create`
and build multiple wall/window/roof primitives synchronously. Immediately
afterward, `CombineStaticSurfaces` gathers mesh filters by material and
creates one combined GPU mesh per material. Before this change that routine
allocated a new `MeshFilter[]` from `GetComponentsInChildren`, repeatedly
allocated `Material[]` from `Renderer.sharedMaterials`, and recreated its
dictionary and per-material lists every house.

## Surgical change
- Use Unity 6 `GetComponentsInChildren<T>(bool, List<T>)` and
  `Renderer.GetSharedMaterials(List<Material>)` to reuse query buffers.
- Reuse material groups, `CombineInstance` lists and renderer scratch lists
  on the Unity main thread. Clear native object references in `finally`.
- Retain `CombineInstance[].ToArray()` at each combined mesh because the
  `Mesh.CombineMeshes` API requires an array.
- Retain exact 16-bit vertex guard, transform, material assignment, original
  renderer disable-on-success, and `UploadMeshData(true)` behavior.
- No changes to native `.unity` scene, TerrainData, terrain paint, tree instances,
  meshes, original textures, shaders, LODs, collision shapes, render scale,
  cloud endpoints, or the shared import gate.

This removes avoidable managed-array/list churn during procedural house
construction. It does **not** remove the native mesh combine or guarantee a
specific frame-time gain. Houses are still placed through the existing
cross-system streaming admission; this change is deliberately not a second
streaming scheduler.

## Verification and release gate
`python3 Tools/Validation/test_android_modular_house_gc.py`
and existing `test_open_combat_houses.py`, `test_house_roof_hard_edges.py`,
`test_android_house_shader_reference.py` check the original safety/appearance
contracts. Run Unity 6000.3.26f1 source compilation and genuine Android
Profiler A/B on Snapdragon 680, including village entry, driving and warm
revisits, before claiming fewer frame stalls or an FPS uplift. Static checks
are not native compilation or physical device benchmarks.

Official Unity docs:
- https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Component.GetComponentsInChildren.html
- https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Renderer.GetSharedMaterials.html
- https://docs.unity3d.com/6000.3/Documentation/Manual/performance-garbage-collection-best-practices.html
