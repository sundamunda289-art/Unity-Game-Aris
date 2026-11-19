# Dense physics query allocation audit — 19 November 2026

## Observed source behavior

At main `6af32db`, four frequently used query paths used small fixed NonAlloc
buffers. Every saturation immediately ran an allocating RaycastAll or
SphereCastAll. A continuously crowded scene could therefore allocate arrays
on every foot-support query, camera query or gun trace/pellet. The actual
frequency and frame cost on the owner's handset are not measured here.

The new `AarisPhysicsHitBuffer` keeps the original initial capacities, lazily
promotes a caller's scratch buffer once on saturation, and starts subsequent
queries directly in that retained buffer. There is no shared mutable query
array, delegate allocation, sorting, thread handoff or forced collection.

| Consumer | Initial capacity | Maximum retained capacity |
| --- | ---: | ---: |
| Character ankle support, shared by both feet | 12 | 128 |
| Equipped gun camera/muzzle/pellet traces | 48 | 256 |
| Aerial ground support | 72 | 256 |
| Aerial camera sphere cast | 64 | 256 |

An exactly-full result is still considered incomplete. If even the retained
buffer saturates, the original exhaustive allocating query runs and every hit
is inspected. Above-cap pathological density can still allocate; the cap
bounds retained scratch memory, not the number of collision results accepted.

The first promotion adds one allocation and a retry; later queries below the
cap use one NonAlloc call. Sparse scenes retain the original buffer size.
Buffers remain owned by the existing components and are released with them.

## Preserved gameplay

Caller-side ray origins, directions, ranges, sphere radius, DefaultRaycastLayers
and trigger exclusion are preserved. The original nearest-hit loops and
self/aircraft collider exclusions remain. Gun camera-versus-muzzle authority,
ankle walkable-normal/nearest-support checks, landing height filters and camera
clearance remain. No collider is disabled, no query is truncated, and no tree,
mesh, texture, shader, material, draw range, render scale or visual asset changes.

This addresses conditional managed-allocation pressure, not every possible
source of CPU/GPU stalls. Other candidate costs remain native model integration,
physics scene complexity, GPU overdraw/shadows, and thermal throttling. They
must be distinguished using real CPU/render/GPU timing and Android exit data.

## Executed validation

The production helper was executed by a standalone C# unit harness against a
controlled Physics backend; no Unity Editor, APK build or licensing operation
was started for this work.

- **4,908 checks passed**, including empty, below-capacity, exactly-full and
  above-capacity results, unordered contacts, complete hit membership, nearest
  hit parity, unchanged query arguments, retained identity and stale tail data.
- **4,800 warmed dense queries** used one NonAlloc call each, retained the same
  array, and made zero allocating All calls in the controlled backend.
- Source integration checks cover original collision filters, support normals,
  camera safety, initial capacities and retained limits.
- Battle/weapon/pose/foot-contact regressions and 42 applicable offline-drop
  checks passed. One unrelated FBX-content test was not run because the local
  checkout intentionally contains its Git LFS pointer; no LFS assets were changed
  or downloaded for this patch.
- All 38 gameplay C# files passed syntax parsing and 38 Unity frame callbacks
  passed the blocking-operation guard.

Reproduce the helper unit test with system `mono-mcs`, or set
`AARIS_UNITY_EDITOR_DATA` to an installed Editor's `Data` directory, then run:

```bash
python3 Tools/Validation/run_physics_hit_buffer_csharp.py
python3 Tools/Validation/test_physics_hit_buffer_integration.py
```

The controlled backend is not native PhysX or device evidence. Native scene
collisions, Unity integration compilation, Android FPS and freeze/crash
attribution remain unverified for this change. APK/build work was intentionally
left out at the owner's request.

Primary reference: [Unity 6000.3 RaycastNonAlloc](https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/physics/raycastnonalloc).
Unity documents that a full buffer has undefined order and need not include
the closest hits; the exhaustive fallback is therefore retained.
