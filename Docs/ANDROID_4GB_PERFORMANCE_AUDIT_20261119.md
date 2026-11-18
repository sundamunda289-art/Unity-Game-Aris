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
