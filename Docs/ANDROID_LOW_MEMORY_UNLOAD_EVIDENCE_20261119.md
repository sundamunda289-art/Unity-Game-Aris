# Android critical-memory asset cleanup diagnostics (2026-11-19)

## Verified source behavior

On the main thread AarisMobileRuntime receives Application.lowMemory,
records the event, and (unless in cooldown / already running) schedules
Resources.UnloadUnusedAssets() no more often than once every 30 seconds.
That operation can itself incur frame-time overhead. Removing it blindly
could increase the chance of Android low-memory kills.

The game's large original cargo-aircraft model is synchronously looked up
in AarisOfflineDropVisuals.CreatePlane during drop Start. That is another
cold-load risk worth measuring before attempting a gameplay-sensitive
async redesign; it is not a proven source of the reported 3-30s stalls.

## Small change and diagnostic output

Keep the same critical-memory cleanup and cooldown, never call blocking
AsyncOperation.WaitForCompletion or GC.Collect. Track the number of
asset-unload operations started, completed, and suppressed by an existing
cooldown/in-flight operation. When a pending unload finishes, emit one
asset-unload-complete record (with the operation time since it was
requested). Every JSON telemetry record now includes:

- assetUnloadStartedCount
- assetUnloadCompletedCount
- assetUnloadSuppressedCount
- lastAssetUnloadElapsedMs (starts at -1 = not observed)
- assetUnloadInProgress

Elapsed milliseconds are **wall time including frame delays**; not CPU
time in Unity native code, not PSS, and not proof that cleanup caused a
freeze. Existing visual quality and all source assets remain unchanged.

## Validation gate

Static tests:
python3 Tools/Validation/test_android_low_memory_unload_evidence.py
python3 Tools/Validation/test_android_stall_evidence_telemetry.py
python3 Tools/Validation/check_mobile_hotpaths.py

On-device verification still required: match recorded unload start/end
timestamps with Android logcat ANR / LMK / native crash markers and
Profiler main/GPU frame times during cold launch and extended match.

Documentation:
https://docs.unity.com/en-us/engine/6000.0/script-reference/unityengine/application/lowmemory
https://developer.android.com/games/engines/unity/unity-lmks
