# Critical-memory grenade visual-pool cleanup (2026-11-19)

Target: Unity 6000.3 URP, Snapdragon 680 / Adreno 610,
4GB Android; original offline Battle Royale map and models retained.

## Verified memory-lifecycle gap
PR #706 introduced a bounded pool of up to six deactivated 3D grenade
hierarchies and physics colliders to reduce repeated native GameObject
creation. On a critical-memory notification the Android runtime already
runs `Resources.UnloadUnusedAssets()`, but inactive pooled GameObjects were
still referenced by the static stack. An unused-asset sweep cannot collect
that explicitly retained hierarchy. This is a concrete memory-retention path,
not confirmed to be the root cause of the reported 3–30 second freezes.

## Minimal Android-only change
- In `AarisMobileRuntime.Update()`, when the existing low-memory flag is
  processed, call `AarisOfflineFragGrenade.ReleaseInactivePooledGrenades()`
  before scheduling the existing unused-asset cleanup. Never do scene work
  inside the OS low-memory event callback.
- Pop at most six inactive grenade roots and queue their normal Unity
  `Destroy()`; do not search the scene, destroy active projectiles, alter
  collider owner-ignore rules, change damage or touch controls.
- Suppress pooling for 60 seconds after a critical-memory notification so
  those native objects do not immediately accumulate again. Newly used
  grenade visuals continue to be created with the original appearance;
  under memory pressure they use the existing normal cleanup path.
- Desktop/Editor compile the original throw/detonation path, unchanged.
  No source TerrainData, textures, art imports, URP render scale, vehicle
  physics, or collision geometry is modified.

## Tradeoffs and evidence
Pool suppression can increase the frequency of instantiations while the
phone is critically low on RAM, in exchange for reducing retained objects.
Unity `Destroy()` is deferred until end of frame, so immediate GPU/native
reclamation at the precise `UnloadUnusedAssets` call is *not* guaranteed.
Measure Android PSS, Unity object count and frame spikes during/after a
low-memory notification; verify repeated grenade visuals and gameplay.
This change does not prove OOM crashes, ANRs or GPU freezes are fixed.

Source checks:
```sh
python3 Tools/Validation/test_android_frag_pool_low_memory.py
python3 Tools/Validation/test_android_frag_visual_pool.py
python3 Tools/Validation/test_android_low_memory_unload_evidence.py
```
CI green is not Unity Editor compile or handset validation.
