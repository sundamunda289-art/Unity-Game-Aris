# Android roadside bike: bounded URP material integration (2026-11-20)

## Observed source pressure

The authored motorcycle source asset is requested asynchronously when the player
is within 110 m of a roadside bay. However, after that request completes,
AarisRoadsideBikeScenery.SetupMaterials previously created **eight shared URP Lit
materials and three paint clones** during a single admitted Update frame.
The native graphics-driver setup for those materials can overlap other engine
work despite existing per-system limits. This is a verified source scheduling
opportunity, **not** a confirmed cause of any real handset freeze.

## Minimal, quality-preserving change

- Only constrained Android GPUs (existing RAM/Adreno 610 classification):
  initialize two original surface materials in each newly admitted frame,
  then make the original three paint variants in a later admitted frame.
  There are no per-frame material creates during ordinary gameplay.
- Reuse the existing global optional-model gate: when an essential rig,
  collider batch, an async import or unused-asset cleanup owns the frame,
  retry rather than doing the optional material work anyway.
- Publish the bike palette only when *every* original material is ready.
  A nearby vehicle may appear a few frames later if the first encounter
  outpaces the existing 110 m prewarm; no half-shaded bike is shown.
- Editor and non-constrained Android retain the original synchronous,
  one-time setup. The source mesh, 45 m activation, 66 m retention,
  110 m prewarm, 3-visible-object bound, four paint choices, metallic/
  roughness/clear-coat/emission properties, pooling and destruction are
  unchanged.
- No terrain/vegetation/texture/shadow/LOD changes. No new scene, prefab,
  assets, dependencies, machine or worktree.

## Evidence and validation gates

Source regression checks verify the Android-only path, original geometry/
materials/paint invariants, shared admission, no exposure of incomplete
materials, and the existing prewarm/streaming contracts. CI separately parses
the modified C# source for syntax errors. Passing those checks is **not**
equivalent to native Unity Editor assembly compilation, visual screenshot
certification, an installable Android APK or a measured FPS increase.

Before declaring a mobile improvement, use the project's existing opt-in
p50/p95/p99 CSV overlay and Android Profiler/Perfetto to compare cold starts,
first approaches to roadside bikes, sustained camera movement and low-memory
cases on the same Snapdragon 680. Verify no newly delayed bike appearance
during high-speed travel/teleport, and inspect painted bike surfaces.

Public engineering references:
- Unity 6: asynchronous texture and mesh uploads
  https://docs.unity3d.com/6000.0/Documentation/Manual/LoadingTextureandMeshData.html
- Unity 6: optimize graphics performance
  https://docs.unity3d.com/6000.0/Documentation/Manual/OptimizingGraphicsPerformance.html
- Android: frame pacing
  https://developer.android.com/games/sdk/frame-pacing
