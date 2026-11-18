# Unity Android boot freeze audit — 2026-11-19

Target: Unity 6000.3.26f1 / URP 17.3.0, Snapdragon 680 / Adreno 610, `com.aaris.game`.

## Directly inspected, not guessed

- Canonical project's single enabled build scene contains approximately 3,395 serialized GameObjects, four Terrain objects and 600 MeshRenderer components.
- The active high-quality URP asset specifies 4x MSAA, HDR, depth/opaque textures and 4 cascade shadows out to 1000 m. The existing Android runtime policy already overrides MSAA, shadow range and cascades on Adreno 610, so the saved asset is not the effective on-device quality.
- SRP Batcher, global mip streaming, instanced terrain and incremental rival/settlement initialization were already implemented; do not re-enable them or assume they are missing.
- The 2026-11-19 merge #679 addressed main-thread waits in diagnostic `Record()` and added throttled `frame-stall` samples. But its `Awake()` still synchronously created, copied and truncated a diagnostic file and `Record("start")` still appended it on the main thread. This is a verified *risk*, not a proven cause of the user's launch freeze.
- The separately active `fix/android-tree-streaming-20261119` Codespace work covers foliage texture importers; these are intentionally not touched here.

## This follow-up

- Android's diagnostics-file rotation and first report now use the same ordered worker queue established by #679.
- Non-Android Editor retains synchronous diagnostics for easy reproducibility.
- No texture, leaf coverage, mesh, renderScale, shadow, terrain, bot, or source-scene changes.

## Required on-device verification

The launch freeze, long frame stalls and unexplained exits remain **unconfirmed** until the actual Android device provides traces and performance samples. Recommended:

```sh
adb devices
adb logcat -c
adb logcat -v threadtime -b main -b system -b crash > aaris-android-logcat.txt
# In a separate terminal during a match:
adb shell dumpsys meminfo com.aaris.game
adb shell pidof com.aaris.game
```

Compare repeated cold launches and an extended match: main/render/GPU frame-time percentiles, maximum stall time, native/PSS/graphics memory, thermal state, and whether exits were ANRs, native signals or low-memory kills. Android may kill a process without allowing Unity to finish a file write.

Only after real-device measurements should we prioritize asset bundles / Addressables, chunked additive scene activation, shader variant warming or AI frame budgets; changing everything simultaneously obscures the true culprit.

Official references: [Android ANR diagnosis](https://developer.android.com/topic/performance/anrs/diagnose-and-fix-anrs), [Unity URP 6000.3 asset reference](https://docs.unity.com/en-us/engine/6000.3/manual/render-pipelines/universal-render-pipeline/urp-reference/universalrp-asset), [Unity mipmap streaming](https://docs.unity.com/en-us/engine/6000.0/manual/materials-and-shaders/textures/texture-loading/texture-streaming/introduction).
