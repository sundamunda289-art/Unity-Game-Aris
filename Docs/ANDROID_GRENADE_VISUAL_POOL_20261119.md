# Bounded Android grenade visual/physics pooling (2026-11-19)

Target: 4 GB Android / Snapdragon 680 / Adreno 610, Unity 6000.3 URP.

## Source-level risk and change
The original `AarisOfflineFragGrenade.Throw()` created a fresh root,
SphereCollider, Rigidbody and imported grenade visual on every throw.
`Detonate()` destroyed the entire hierarchy. This allocation/instantiation
path is directly verified in source, though it is **not** proven to cause
the reported 3–30 second stalls.

On **Android player builds only**:
- Keep up to **six** fully initialized but inactive grenade GameObjects for
  reuse. On a pool hit, no model instantiation, GameObject construction or
  per-throw imported-resource lookup occurs. Overflow keeps the original
  Destroy path. The first six distinct grenades still allocate initially.
- On reuse reset position, physics, fuse, explosion state, bounce-audio
  cooldown and previous owner; reapply the player collider ignore rule.
  After detonation, clear both damage/LOS scratch collections so the pool
  does not retain victims, and disable the Rigidbody/root before pooling.
- Two seconds after the tactical HUD starts, prewarm the authored grenade
  prefab and original three finish materials **sequentially** with
  low-priority `Resources.LoadAsync`, without creating any gameplay
  grenade. An immediate throw before loading completes retains the
  original synchronous fallback. A failed asset retains the original
  visible sphere fallback. Assets/materials/shaders are not replaced.
- Edit Mode/Desktop keeps the old create/destroy behavior so original
  authored scene QA remains comparable.

## Invariants
No change to blast radius, damage, line of sight, Rigidbody tuning,
bounce properties, grenade inventory, input, models, shared URP finishes,
terrain, graphics quality, shaders, character movement or other weapons.

## Gates / limitations
```sh
python3 Tools/Validation/test_android_frag_visual_pool.py
```
Run weapon/tactical regression checks and GitHub CI. Only licensed Unity
compilation and Android Play Mode can prove that pooled Rigidbody
reactivation and collision-ignore reset behave correctly. Collect
Unity Profiler GC Alloc during repeated grenade use, first throw frame time,
and Android PSS; pooling can retain up to six inactive visual roots and
therefore exchange some RAM for fewer later allocations. It does **not**
establish a cause or fix for phone-wide freezes or unexplained app exits.
