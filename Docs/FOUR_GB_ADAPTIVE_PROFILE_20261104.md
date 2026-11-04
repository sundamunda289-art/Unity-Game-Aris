# Android 4 GB-class adaptive budget coverage (2026-11-04)

## Verified defect

The saved `Assets/AarisWorld/Scripts/AarisMobileRuntime.cs` previously returned early
when `SystemInfo.systemMemorySize > 3584` MB. An Android device reporting roughly
3,700–4,096 MB therefore skipped the *entire* existing reversible grass/shadow/
terrain/streaming-mip budget, p95 pressure adaptation and emergency GPU-cost
fallback. The user's reported ~3.7 GB phone is a concrete reason to cover this
gap. This is a **source inspection**, not proof of the crash root cause.

## Saved-core patch

The Android-only RAM ceiling is now **4096 MB**. The existing threshold
(`<=2304` MB) still selects the stricter `ram-2gb` budget; 2305–3584 MB
retains `ram-3gb`; 3585–4096 MB uses the already-established moderate
three-GB settings under a distinct `ram-4gb` telemetry label.

This is a *coverage fix*, not a new visual preset. It preserves the authored
far camera and terrain tree draw distances, tree/house LOD bounds, all native
terrain instances, houses, rocks, material GUIDs, and the saved level geometry.
The already-existing pressure fallback can trade some near detail and, only
at sustained high frame-time pressure, a little render resolution for
smoothness. Original values are restored on component destruction; low-memory
warnings deliberately keep safety reductions sticky until scene restart.

## Verify before claiming outcomes

- Check source/unit tests and build with Unity **6000.3.26f1 / URP 17.3**.
- Run the *same* APK routes on 2, 3 and 4 GB-class Android hardware; capture
  JSONL `mobileQualityProfile`, p95 frame time, main-thread/GPU time,
  Android PSS, texture memory, low-memory events, thermal state, and screenshots.
- Check `ram-4gb` actually activates on a device reporting >3584 and <=4096 MB;
  check >=4097 MB devices remain at the original baseline.
- Compare sustained foliage/camera turns, window/roof detail and distant
  silhouettes in A/B before accepting visuals. The profile may cause
  mipmap/detail transitions; a smaller budget **does not ensure 60 FPS**.
- A 10–15 second home-screen crash can be graphics-driver, OOM, C#, native,
  or other causes; require Android logcat crash evidence to identify it.

Mipmap streaming was **not** enabled indiscriminately on Terrain textures:
Unity documents that Terrain texturing does not support standard mipmap
streaming, so this patch does not risk blurred ground/terrain materials.
No third-party assets were added; no evidence-driven house/window placement
or authored size changes were available in this change.

References:
- Unity texture budget: https://docs.unity3d.com/6000.0/Documentation/ScriptReference/QualitySettings-streamingMipmapsMemoryBudget.html
- Unity URP efficiency: https://docs.unity.com/en-us/engine/6000.0/manual/render-pipelines/universal-render-pipeline/introduction/configure-for-better-performance
- Unity terrain mipmap limitations: https://docs.unity3d.com/cn/2023.2/Manual/TextureStreaming.html
