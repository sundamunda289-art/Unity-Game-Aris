# Android aircraft startup stage evidence (2026-11-19)

## Source-confirmed risk

The initial aerial entry executes AarisOfflineDropVisuals.CreatePlane from
AarisOfflineDrop.Start. The preferred original full-detail local model
CargoPlaneRealistic.obj has about 6.7 MiB of serialized OBJ data. That
serialized size **does not equal runtime GPU, native or managed memory**.

The synchronous model lookup, native prefab Instantiate, and the material /
rotor setup may contribute separate startup stalls. There was no measured
per-stage evidence in the existing Android telemetry. It would be unsafe to
replace the aircraft with a placeholder or delay camera handoff blindly.

## Zero-visual-downgrade diagnostic addition

Track precisely once per plane creation, using
Time.realtimeSinceStartupAsDouble (Unity 6):

- planeModelResolveMs: elapsed time during preferred and fallback Resources.Load
- planeInstantiateMs: elapsed time for imported prefab Instantiate; -1 if no
  imported prefab existed
- planeTotalCreationMs: elapsed time from first lookup through full visual
  setup including rotor and navigation markers
- planeHighDetailSelected: whether the preferred realistic model resolved

These fields join the existing JSON diagnostics and initialize to -1 until
observed (boolean is false until a model has been tested). They are **wall
times recorded around synchronous main-thread operations**, not Android PSS,
GPU frame times or evidence of a crash cause.

No source model, material, shader, animation, camera, input, flight transition,
quality setting or fallback semantics are changed.

## Test / decision gate

Static tests:
python3 Tools/Validation/test_android_plane_startup_stage_timing.py
python3 Tools/Validation/test_offline_drop_assets.py
python3 Tools/Validation/test_android_stall_evidence_telemetry.py

Do not claim a freeze fix based on static checks. Collect cold-start
AarisDiagnostics/current.jsonl from the actual Snapdragon 680 device and
compare plane creation stage times with Unity Profiler / Android logcat.
Only then choose between async preflight staging, import tuning or splitting
visual setup. Async Resources loading itself may still require main-thread
integration, and moving aircraft creation without retaining first-frame
visibility can produce player-visible regressions.

Official Unity 6 Resources.LoadAsync reference:
https://docs.unity.cn/6000.0/Documentation/ScriptReference/Resources.LoadAsync.html
