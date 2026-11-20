# Cached motorcycle paint layout construction — 20 November 2026

## Verified source issue

Each new original roadside motorcycle prefab instance previously allocated:
- one fallback-index array;
- four arrays of renderer slots, one for each fully authored paint finish;
- per-renderer material-kind arrays and four converted material-slot arrays.

This happened even though every motorcycle is instantiated from the same
prefab, uses the same original shared surfaces, and differs only by the
existing deterministic paint index. The pool already reuses parked roots, but
its initial one-to-three native builds can still generate this unnecessary GC.

## Narrow quality-preserving improvement

The first successful bike creates the exact original 4-paint layout matrix
and retains it. Later clones call `Renderer.GetSharedMaterials(List<Material>)`
using a reused list, and compare **every original shared Material identity**
and renderer slot count with the cached prefab. Only truly matching clones
share the immutable layout arrays. A changed renderer/material topology falls
back to the complete original conversion, without replacing the stable cache.

The shared layout is not a Unity Material clone: no new materials, textures or
mesh surfaces are created by this optimization. There is no per-frame polling,
altered LOD, occlusion, terrain, shadow, vehicle spawn logic or reduced detail.
Original 45 m appearance, 66 m release hysteresis, 110 m early loading and
three-instance cap remain unchanged.

The cache belongs to its existing runtime owner. Completed palettes survive
normal enable/disable; interrupted *incomplete* palette rebuilding has no
live bike instances or stale layout cache. Destroying the owner still destroys
its original material set in the existing owner method.

## Verification

`test_android_bike_layout_cache.py` verifies original source invariants,
Unity's nonallocating material reference read, complete identity checks,
fallback and untouched original art controls.

`run_bike_layout_cache_csharp.py` compiles the **actual** C# cache methods in
an isolated Mono fixture and runs the identical-prefab hit, different-material
fallback, changed-slot-count fallback, unchanged four paint finishes, and
nonallocating cache-hit getter rules.

Those prove source/managed behavior, not native Unity Editor compilation,
actual Android allocations, visual parity on a handset, or a measured FPS gain.
Run a real Snapdragon 680 A/B cold bike encounter under the existing
Aaris perf overlay and confirm peak GC bytes, p95/p99 and all four variants.

Unity's official API explicitly recommends `GetSharedMaterials(List<Material>)`
when avoiding the copy created by the `sharedMaterials` property:
https://docs.unity.com/zh-cn/engine/6000.3/script-reference/unityengine/renderer/getsharedmaterials

The canonical Codespace has unrelated dirty actor/Editor changes; the patch is
authored on its own GitHub branch and tested against canonical content. No
existing Codespace tracked/untracked source was overwritten or stashed.
