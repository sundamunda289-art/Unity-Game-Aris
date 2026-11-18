# Android stability investigation — 2026-11-19

Target: Unity 6000.3.26f1 / URP 17.3.0, Snapdragon 680 / Adreno 610, Android package `com.aaris.game`.

## Evidence from canonical main at audit time

- Git HEAD: `f8404d840f243ea3b070c8b2936f96efec378896`. One enabled build scene (`TerrainDemoScene.unity`), approximately 3,395 serialized GameObjects, four terrain tiles, ~600 MeshRenderers.
- The active high-quality URP asset is selected by `GraphicsSettings.asset` and the saved QualitySettings. It specifies HDR, 4x MSAA, depth and opaque textures, four cascades and 1000 m shadows. **Android-specific runtime policy in `AarisMobileRuntime` changes shadow distance (120 m on Adreno 610), cascades and MSAA; do not infer actual on-phone values from serialized settings alone.**
- SRP Batcher, runtime terrain instancing and global mipmap streaming are *already implemented*. Some tree importers still had `streamingMipmaps: 0` at the audit snapshot; a separate concurrently edited Codespace branch `fix/android-tree-streaming-20261119` was already addressing this. This PR intentionally does not overwrite its work.
- `AarisSettlementExpansion` already places sites incrementally on Android. `AarisOfflineBRDirector` already spreads rival spawns with cooldowns. These are safeguards, not evidence of 30/60 FPS.
- `AarisMobileRuntime.Awake` synchronously copied/rotated a diagnostics file on the game thread. Its `Record` method could then block the game thread waiting for a pending file I/O task via `GetAwaiter().GetResult()`, especially during lifecycle events. This is a verified code-level main-thread stall risk, **not yet a proven cause of the reported freeze**.
- `Resources.UnloadUnusedAssets` is invoked in response to low-memory notification. It warrants Android profiler confirmation before changing; postponing it blindly could increase process-kill risk.

## Change in this PR

1. Queue log-file rotation and initialization on a background worker on Android.
2. Chain diagnostic append tasks sequentially; never wait for disk I/O on the Unity main thread.
3. Avoid an unbounded periodic report backlog; preserve lifecycle/low-memory reports as best-effort asynchronous writes.
4. Capture a throttled `long-frame` diagnostic after a >=1-second frame, no more than once per 30 seconds.
5. Leave all scene objects, meshes, foliage, material/shader quality, texture sizes, resolutions, URP profiles and AI counts unchanged.

Note: Android may kill an app before asynchronous quit/low-memory reports reach flash. ADB/Perfetto/Android vitals remain authoritative for such crashes.

## Device verification required before claiming a stability fix

Build and install an APK from the relevant merged commit. Repeat a cold-start and a 15–30-minute real-device match on the target Snapdragon phone. Capture Android traces **outside the app**, since an app crash cannot reliably write its own report:

```sh
adb devices
adb logcat -c
adb logcat -v threadtime -b main -b system -b crash > aaris-logcat.txt
# In another terminal during gameplay:
adb shell dumpsys meminfo com.aaris.game
adb shell pidof com.aaris.game
```

Record before/after per-frame CPU main/render/GPU times; 1% low FPS, p95/p99 and maximum frame times; peak PSS/native/graphics memory; surface resolution, driver/API, thermal state, loading progress, and crash reason (ANR vs Java exception vs native signal vs low-memory kill). Treat a disappearance to Home as *unclassified* until Android evidence distinguishes the cause.

Success gate: no launch ANR, no >1000 ms frames during stable gameplay, no process exit during extended match, and unchanged native-quality texture resolution and foliage coverage. Headless C# checks alone cannot prove these gates.

## Follow-on architecture after measurements

Prioritize producer/consumer frame budgets for scene additive loading and terrain/AI activation near the camera; avoid global `Resources.LoadAll` on time-critical frames. Consider build-time addressable asset groups and precompiled shader variant collections for confirmed cold-start spikes, keeping live streaming allocations off the main thread. Use object pooling and spatial scheduling only where profile markers show an actual cost; do not duplicate existing incremental-spawn systems.

References: [Unity 6000.3 URP asset](https://docs.unity.com/en-us/engine/6000.3/manual/render-pipelines/universal-render-pipeline/urp-reference/universalrp-asset), [Unity mipmap streaming](https://docs.unity.com/en-us/engine/6000.0/manual/materials-and-shaders/textures/texture-loading/texture-streaming/introduction), [Android ANR diagnosis](https://developer.android.com/topic/performance/anrs/diagnose-and-fix-anrs).
