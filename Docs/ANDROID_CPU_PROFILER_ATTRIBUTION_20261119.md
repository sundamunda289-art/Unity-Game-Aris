# Unity 6 CPU Profiler: isolate 4GB Android cold-start and gameplay stalls

Target: Unity 6000.3.26f1 / URP 17.3.0 / Snapdragon 680, Adreno 610, 4 GB Android.

**Purpose:** Surface the real source of multi-second stalls instead of attributing whole-device freezes to Vulkan or RAM without device traces. Six custom CPU markers are added only under `UNITY_EDITOR || DEVELOPMENT_BUILD`; they are absent from regular production APK code paths. No scene, render scale, terrain tree, model, texture, collider, or player motion properties are modified.

## New measurable CPU phases

| Profiler CPU sample | When it runs | What it can prove |
| --- | --- | --- |
| `Aaris.Startup.PlaneCreate` | One-time plane creation | CPU cost in original aircraft/model creation |
| `Aaris.Startup.CharacterRigInstantiate` | Character prefab, visual fit and animation-graph setup | CPU integration cost **after** async load completes, not async disk/driver time |
| `Aaris.Startup.ColliderGridItem` | Per authored collider during staged native grid indexing | Which 96-item indexing slices consume CPU; several markers per frame |
| `Aaris.Startup.TreeClearance` | Each building's vegetation-clearance check | CPU cost of cached terrain tree collision checks |
| `Aaris.Streaming.VehicleBuild` | When creating a nearby car's authored model/physics shell | CPU cost during map traversal/vehicle streaming |
| `Aaris.Diagnostics.Record` | One sample/event JSON serialization + enqueue | Whether the game's own telemetry contributes to a long frame |

The above samples are **CPU main-thread durations**. Neither their absence nor a long frame alone proves a native GPU deadlock, Android low-memory kill, Unity crash, asset streaming I/O, shader compile, or SurfaceFlinger issue.

## Capture protocol on the actual handset

1. Use an owner-authorized **Development Build** of the exact GitHub commit, with the Profiler connection enabled. Do not enable Deep Profiling on the low-end comparison run, because its high overhead confounds attribution.
2. In Unity Profiler CPU Usage, inspect Timeline and Hierarchy for `Aaris.` markers. Record first 0–15 seconds on cold launch, then run sustained gameplay and travel between sectors where large models become visible.
3. Capture a baseline and patched APK on **the same phone**, OS, orientation, and graphics API. Use p95/p99 frame times, main/render/GPU times (if device supports them) and peak memory. A 3–30 second frame on CPU shows a wide sample. A GPU/system stall may lack a matching custom scripting marker.
4. For app exits/phone hangs, corroborate with Android `logcat` plus available process exit-history metadata. Run `python3 Tools/Validation/analyze_android_logcat.py <local_logcat.txt> --output /tmp/aaris-sanitized.json` **locally**; review the sanitized counts, not raw private logs in GitHub. Existing `AarisMobileRuntime` telemetry emits startup/frame-stall/low-memory snapshots, and `AarisAndroidExitHistory` reports previous exit reason on Android 11+ when supported.
5. If the Developer Build cannot be compiled because Unity Editor licensing or Android build support is unavailable in Codespace, report that limitation rather than inventing successful device profiling.

## Design safeguards

The marker handle is a static readonly field guarded by the same preprocessor condition as its `using var ... Auto()` scope. Each scope is in a synchronous method; **none** straddles coroutine `yield return`, which would wrongly include inter-frame delay. Automatic scope disposal handles early returns/exceptions. The production player does not run `Auto()`, and all original source paths, conditions and assets remain intact.

## Verification

Run `python3 -m unittest discover -s Tools/Validation -p 'test_android_profiler_bottleneck_markers.py' -v` and existing `test_android*.py` suite. GitHub CI C# syntax parsing is mandatory before merge. Source tests and parser validation are not Unity compilation or an Android frame/ANR proof.

Official Unity 6 references:
- ProfilerMarker API: https://docs.unity.com/en-us/engine/6000.0/script-reference/unity/profiling/profilermarker
- Adding custom profiling information: https://docs.unity.com/en-us/engine/6000.6/manual/analysis/profiler/customizing/adding-information-code/intro
- Auto scopes: https://docs.unity3d.com/kr/6000.0/ScriptReference/Unity.Profiling.ProfilerMarker.Auto.html
