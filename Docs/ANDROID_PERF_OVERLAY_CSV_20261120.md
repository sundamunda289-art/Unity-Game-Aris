# Android opt-in Perf Overlay and CSV — 2026-11-20

This instrumentation is **not** a measured optimization or proof of stable FPS.
It adds no LOD, shadow, map, input/HUD, audio, vehicle, scene or shader change.
Only use the logs to compare **the same phone** and route before/after tuning.

## Controls

- Android: hold exactly **three fingers for 1.2 seconds** to toggle the small top-left overlay. Hold exactly **four fingers for 1.2 seconds** to start/stop CSV.
- Unity Editor: **F8** toggles overlay; **F9** toggles CSV.
- Recording and the overlay default to **OFF**, so regular play has only one small touch/key check in Update. Avoid activating multi-touch while using gameplay controls; the gesture is diagnostic only.

## CSV file and interpretation

The active APK writes under `Application.persistentDataPath/AarisDiagnostics/perf_YYYYMMDD_HHmmss.csv`.
Access with Android debugging / app files tooling according to device and build configuration.
Samples include: rolling mean FPS, p50/p95/p99 frame time over the most recent
256 rendered frames, max frame time and >=50/>=100 ms counts in the last
report interval, CPU main-thread last sampled value, GC.Alloc last sampled
bytes when available, and Unity tracked Mono/reserved memory.

The CPU and GC counters are **last sampled values**, not percentile or process
totals. The Unity reserved number is **not Android PSS, GPU VRAM or system RAM**.
A missing counter is **n/a** on-screen, not a made-up zero. The CSV uses -1 for
unavailable counters. The asynchronous CSV writer batches output and does not
synchronously write during the per-frame sampling path. The last buffered
sample can be lost on an abrupt app kill: pair logs with Android logcat/PSS.

Do not enable an always-visible overlay in production benchmarks unless comparing
with the same overlay state on both APKs. Check cold and warm launches, the
settlement transition, long drives and combat camera conditions, 10+ minutes
for thermal behavior, and resume from background. Android native FPS/thermal
behavior **cannot be validated in Linux Codespace**.

## References

- Unity 6000.3 Android profiler: https://docs.unity.com/en-us/engine/6000.3/manual/platform-specific/android/developing/testing-and-debugging/profile-on-an-android-device
- Unity 6000.3 profiler markers: https://docs.unity.cn/6000.3/Documentation/Manual/profiler-markers.html
- Unity 6000.3 URP GPU culling restrictions: https://docs.unity.com/en-us/engine/6000.3/manual/analysis/graphics-performance-profiling/in-urp/gpu-culling
- Qualcomm Adreno low-power graphics: https://www.qualcomm.com/developer/blog/2025/08/optimize-performance-and-graphics-for-adreno-gpu-low-power-gaming
