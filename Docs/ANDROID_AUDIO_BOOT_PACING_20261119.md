# Android optional-audio cold-start pacing (2026-11-19)

## Confirmed source observation

AarisAudioDirector.Awake synchronously loaded 30 base audio keys plus 15
extra optional CC0 variants. The Resources/SFX folder has ~45 audio assets
totalling ~1.2 MB on disk. This is a narrow first-frame I/O/CPU optimization,
not evidence that these files caused the reported 3–30 second frame stalls.

## Safe change

The Android-only code path keeps the original base cues immediately available
and streams optional variation through one Resources.LoadAsync<AudioClip>
request at a time, starting after the first scene frame. The existing
GetClip fallback still plays the corresponding base sound while a variant is
pending. Non-Android and Unity Editor builds retain original eager behavior.

No scene, shader, texture, plant, terrain, render resolution, sound file or
3D asset was altered. This change cannot guarantee FPS improvement.

## Verification

Run:
    python3 Tools/Validation/test_android_optional_foley_streaming.py
    python3 Tools/Validation/test_audio_footstep_gait.py
    python3 Tools/Validation/check_mobile_hotpaths.py

These are source-level Python checks. They do not replace real Unity C#
compilation, Android cold-boot audio comparison, profiler timings, driver
logs, low-memory-kill diagnostics or ANR traces. Revert this isolated
change if mobile testing finds audible regressions or no measurable benefit.
