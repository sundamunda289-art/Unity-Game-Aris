# Unity Game-Aris: Android 4GB performance audit (2026-11-19)

**Scope:** Unity 6000.3.26f1, URP 17.3.0, Snapdragon 680 / Adreno 610, 4GB Android.
**Evidence limit:** Project source and running Linux Codespace were inspected.
The target Android handset was not connected. These results do not establish
the causes of the reported 3-30 second stalls or Android app exits.

## Verified project observations

- GitHub main and Codespace started at commit
  17306bd26497a3cf2a36cf3d48785199c93110e2; only untracked
  Library, Logs and obj were present. These were preserved.
- One enabled native Unity scene, four TerrainData tiles, 76 C# scripts.
- Streaming mipmaps are on (initial QualitySettings budget 384MB).
  AarisMobileRuntime already identifies Adreno 610, controls Android
  shadow/cascade/MSAA/mip residency, records p75/p95/p99 timings and
  asynchronously writes Android diagnostic logs. SRP Batcher is on.
- HighQuality URP is the active pipeline: HDR, depth/opaque textures,
  four original shadow cascades and 1000m source distance, with Android
  runtime overrides. Render Graph is off. Changing any of these requires
  visual comparison and device measurement, not blind edits.
- Among 493 TextureImporter meta files, 375 explicitly enable streaming
  mipmaps; 96 disable it, often for non-color maps or probes. Twenty
  enable alpha coverage preservation. Do not bulk-modify importers.
- AarisSettlementExpansion staggers sites per frame and VehicleWorld
  already prewarms some model resources. NearbyColliders does a sizable
  scene startup grid build; it warrants *profiling*, not unsafe removal.
- RoadsideBikeScenery synchronously loaded AarisRoadBike in Start and
  created vehicle materials before any nearby bike was needed. The
  authored OBJ is ~277KB on disk, so this is a narrow startup improvement,
  **not** an expected cure for multi-second freezes. Its import now
  begins asynchronously at 110m; the original 45m spawn, 66m unload
  limits, physical model and visuals are unchanged.

## Licensing / real-device validation blockers

Actual headless Unity Editor launch exited **198**: "No valid Unity Editor
license found." Unity CLI auth status returned 0 but CLI license status
returned 4. Unity compilation, Play Mode, Android APK output and FPS
improvements are **NOT verified**. Do not bypass licensing.

The 3 mobile symptoms could stem from shader compilation, synchronous scene
loading, GC, physics, driver crashes, ANRs or Android low-memory kills.
Linux host RAM, project folder size and source configuration cannot
identify the Android crash root cause.

## Next evidence gate

With a valid owner-activated Editor license: compile and test in Unity;
build Android and run on Snapdragon 680 / 4GB. Capture cold boot and match
0-15s samples, Unity Profiler main/render/GPU thread timelines, Android
logcat + ApplicationExitInfo / lmkd / ANR traces, device peak PSS, GPU
memory, and p50/p95/p99 frame times. Only then tune further loading,
rendering, foliage and collider scheduling without compromising sharpness.

## Follow-up: bounded collider deactivation

A read-only scan of the native scene found **295 serialized collider
references** in AarisNearbyColliders. Previously Awake() built the spatial
index and disabled distant enabled colliders in the same opening frame.
This is a plausible native broadphase cost, **not** a proven freeze cause.

The follow-up preserves every originally solid collider while it builds the
index, installs the near-player active set, and deactivates distant entries
in batches of at most **24 per frame**. Every batch rechecks the live player
position and release margin; a disabled streamer cancels its pending
coroutine and restores the original enabled collider state. Terrain,
vegetation, visuals, URP, authored scene and activation radius are unchanged.

Regressions: test_android_collider_boot_budget.py,
test_player_streamer_lifecycle.py, test_settlement_streamed_obstacles.py and
check_mobile_hotpaths.py. Static tests cannot demonstrate actual FPS gains,
Unity compilation or a crash fix. Compare cold-start physics/CPU Profiler
spikes, memory and ANR/LMK records on the 4GB Snapdragon 680 Android handset.

## Follow-up: Android primary character async loading

The 6.95 MB authored primary character FBX was synchronously requested in AarisCharacterVisual.Start(). Android now issues one Resources.LoadAsync<GameObject> request at a time in the existing original-to-fallback priority order; the saved placeholder stays untouched until a rig is actually ready. Editor scene probes keep their immediate existing path. Mesh, textures, animation selection, materials, resolution, foliage and authored geometry are not reduced. The completed async asset can still incur rig-instantiation/animation-graph cost; this is a source-level reduction in cold-start synchronous loading risk, **not** a measured freeze fix. Verify the model is actually visible on Android, first-frame and p95/p99 timing, memory, and fallback behavior.
