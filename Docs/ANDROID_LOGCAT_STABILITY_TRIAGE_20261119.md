# Android freeze, ANR and low-memory evidence triage (2026-11-19)

Target app: **com.aaris.game**. This playbook distinguishes log **signals**, not verified causes. It does **not** assert that a device freeze was a GPU deadlock or that a home-screen exit was a low-memory kill.

## One-command local triage

Capture device logs during an owner-authorized reproducible test run. Android developer tooling can save a snapshot:

    adb logcat -b main,system,crash -v threadtime -d > android-match-logcat.txt

Then use the offline, dependency-free analyzer:

    python3 Tools/Validation/analyze_android_logcat.py android-match-logcat.txt --output android-summary.json

It also accepts stdin with - and --package for another package. It outputs **only classification counts and up to 12 line numbers per category**: never raw log messages, account identifiers, file paths from logs, or stack traces. Do not publish raw logcat, bugreports, tombstones, or device traces to public GitHub; they may contain personal information.

## Category interpretation

| Category | Evidence | What it does NOT prove |
|---|---|---|
| lmk_kill | Target app explicitly named in an lmkd/LowMemoryKiller kill line | Root memory leak, precise PSS peak, or which asset consumed memory |
| app_anr | Android names the app in an ANR/input-dispatch timeout | Whether GPU, CPU, I/O, locks or driver blocked the main thread |
| java_crash | FATAL EXCEPTION plus nearby target-app Process: line | A native Unity engine crash |
| native_crash | Fatal signal plus nearby tombstone process marker | Which Unity shader, mesh, or subsystem crashed |
| app_oom | App line explicitly reports OutOfMemoryError | Android LMK specifically |
| app_process_exit_unknown | Android says the app process died | The reason for termination |
| app_low_memory_signal | App receives a low-memory signal | That an LMK occurred |
| app_frame_stall | App reports a long frame | Which thread stalled |
| gpu_driver_signal_unattributed | GPU/Adreno/KGSL fault, timeout or reset text | That Aaris caused the system event |
| thermal_signal_unattributed | Thermal throttling/critical system message | That game overheating caused the freeze |

A missing category **does not prove absence** of the event: buffers may overwrite evidence, vendors may use different tags, and Android can kill a process without a Unity error.

For defensible causal attribution, correlate incident time with app PID/process exit reason, Unity Profiler main/render/GPU traces, per-frame peakFixedStepsPerRender, fixedStepsInSample, Android PSS, native/system tombstones and LMK/ANR records. The in-game estimatedGraphicsDriverBytes field is a Unity estimate, **not** physical GPU VRAM or Android PSS. OpenGL ES render-thread/GPU timing can be missing. Preserve water Depth/Opaque textures and imported foliage/materials unless profiling and scene checks support a change.

The analyzer reads one local text stream and has no network calls, device permissions, subprocesses, or code execution from logs. It cannot fix a crash, compile Unity, or replace physical-device verification. The Codespace Unity Editor license must be legitimately activated before claiming build/Play Mode verification.

Official references: Android debug LMKs https://developer.android.com/games/engines/unity/unity-lmks ; Unity memory optimization https://developer.android.com/games/engines/unity/unity-reduce-memory ; Unity 6 lowMemory callback https://docs.unity.com/en-us/engine/6000.0/script-reference/unityengine/application/lowmemory .
