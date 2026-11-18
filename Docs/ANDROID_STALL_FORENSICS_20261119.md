# Android stall and crash evidence checklist (Snapdragon 680 / Adreno 610)

This is an evidence-gathering guide, not proof that any crash has been fixed.

## Capture on the real 4 GB Android phone

1. Build a Development Build with Unity Profiler enabled, then reproduce the freeze / app exit on device. Collect CPU Timeline and Memory Profiler snapshots before the problematic transition. Capture Android logcat and Android system ANR / low-memory-killer records using authorized developer tools.
2. Distinguish: Java exception, native crash / tombstone, OS low-memory kill (LMK), ANR, GPU driver reset, and long Unity main-thread / render-thread work. An abrupt return to Home is not sufficient to distinguish them.
3. Record the maximum frame duration, main-thread vs render-thread vs GPU time, allocated/reserved Unity memory, process PSS from Android developer tools, Texture.currentTextureMemory, and whether it happened on cold launch, scene entry, foliage streaming, or mid-match.
4. Check Unity Timeline for Resources.Load, Instantiate, Resources.UnloadUnusedAssets, shader compilation, Android Debug.getPss, I/O calls, physics, and asset upload. Address the measured culprit, not every subsystem at once.
5. Keep source textures and tree density unchanged while first reducing work spikes: batch optional loads, stagger resource activation, schedule only bounded synchronous operations, and avoid blocking diagnostics calls. Verify alpha coverage and mip residency at actual camera distance in device screenshots.

## Evidence gates

A passing Python source test is not a Unity compile. A successful Unity EditMode test is not an Android device test. A successful Android build is not proof of stable runtime. Only repeated device sessions with matching build SHA and captured profiler / crash logs can establish that 3–30-second stalls and crashes are addressed.
