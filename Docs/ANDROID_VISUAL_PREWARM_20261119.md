# Android rival visual prewarm audit — 2026-11-19

Target: Unity 6000.3.26f1 / URP 17.3 / Snapdragon 680 / Adreno 610 / 4 GB Android.

## Specific code-level risk

The first offline encounter previously began synchronous `Resources.Load<GameObject>`
for a large humanoid prefab on the first rival spawn after landing. That could
produce a one-time frame hitch if the resource had not yet been loaded.
This is **not** proof of the reported 3–30 second stalls, phone freezes, or
Android application exits.

## Bounded change

- Start visual prewarming after a two-second realtime delay to avoid the
  very first terrain frame.
- Sequentially request candidate character prefabs with Unity
  `Resources.LoadAsync<GameObject>`; stop on the first valid visible prefab.
- Reuse the selected prefab at opponent spawn instead of issuing the initial
  synchronous prefab load.
- Hold initial match timing until the resource coroutine has completed.
- Leave humanoid meshes, all imported textures, material assets, mip
  settings, tree counts, scene geometry, graphics settings, AI and game rules
  intact. This is not a new streaming infrastructure or guaranteed FPS fix.

## Verification

Run `python3 Tools/Validation/test_android_rival_visual_prewarm.py` and
existing Android regression tests. Compile under a legitimately licensed
Unity Editor. On the actual 4 GB device collect two cold launches and
long-match Android logcat (ANR, driver signals, lmkd), Unity Profiler
main/render/GPU frame times, Memory Profiler/PSS, peak graphics memory,
and first prefab integration timestamps. Source tests and headless Linux
testing do **not** establish device-side improvement.

Investigate `Resources.UnloadUnusedAssets` on memory warnings using
recorded frame times; removing emergency unloading without memory evidence
might worsen low-memory kills.
