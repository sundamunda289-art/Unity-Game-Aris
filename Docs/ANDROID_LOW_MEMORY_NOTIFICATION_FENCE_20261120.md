# Android critical-memory notification streaming fence — 2026-11-20

## Audited race

On constrained Android, `AarisMobileRuntime.OnLowMemory` used to set only a
pending boolean. The actual `Resources.UnloadUnusedAssets()` async operation
was started later in `Update`. Other streamers could initiate optional
`Resources.LoadAsync` or heavy model hierarchy work in that intervening frame.
This is a source-level admission race, not a measured Snapdragon 680 freeze.

## Fix

- The OS low-memory callback also notifies `AarisMobileStreamingFrameGate`.
- Optional imports, hierarchy builds and nonessential diagnostics avoid the
  notification render frame **and its next frame**. Existing in-flight cleanup
  and completion-frame fences remain active until the cleanup settles.
- No loops, string formatting, allocation or blocking waits in the callback.
  The fence expires by frame count and cannot permanently starve streaming.
- Core scene, trees, terrain, character, vehicle positions, texture resolution,
  LOD, original models, and URP visual settings are unchanged.

## Validation and boundaries

`Tools/Validation/test_android_low_memory_signal_fence.py` checks the
callback-to-gate wiring, Android guard, admission ordering, no blocking calls,
reset and expiry model. It is not a Unity/Android gameplay test.

The memory signal may arrive while another native import is already in flight;
the fence cannot cancel work already submitted. Do not infer FPS improvements,
zero frame drops, zero crashes or fixed thermals without a licensed Unity Editor,
real Android build, on-device Unity Profiler, Android PSS/logcat and same-route
screen comparisons. The currently connected GitHub API cannot execute commands
in the owner's Codespace or inspect dirty local files. Do not reset or sync it
blindly; only read-only `git status` and delta-safe integration should follow
once direct terminal access is available.

## Sources

- Unity asynchronous uploads: https://docs.unity.com/en-us/engine/6000.0/manual/analysis/graphics-performance-profiling/loading-texture-mesh-data-asynchronously/configure-asynchronous-upload-pipeline
- Unity URP mobile: https://docs.unity.com/en-us/engine/6000.0/manual/render-pipelines/universal-render-pipeline/introduction/configure-for-better-performance
- Android performance / memory: https://developer.android.com/games/engines/unity/unity-reduce-memory
