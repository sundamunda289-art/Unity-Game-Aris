# Android parked-car retirement / model-build frame separation — 2026-11-20

## Source evidence
The existing 300ms `AarisVehicleWorld.Update` poll first calls `Destroy` on up to the original per-poll maximum of distant, stationary, unoccupied car roots. It then continues to `PumpAuthoredBodyLoads` and can synchronously `Build` a nearby replacement car. Unity documents that object destruction actually occurs **after the current Update loop and before rendering**. This can stack hierarchy teardown, physics component release and new renderer/mesh construction in one rendered frame despite the existing per-system spawn and unload budgets. This is an identified *source-level concurrency risk*, not a diagnosis of actual device stalls.

## Change
- On constrained Android GPUs / low-RAM phones only, if at least one far parked car was retired in the current poll, reserve the existing shared native-streaming frame slot and skip the **optional vehicle load/build phase in that one poll**.
- Preserve every original parked-only eviction condition, vehicle state snapshot, 160m unload radius, 105m load radius, 44 match plans, active limits, authored models, textures, collisions, cars and map geometry. Player-controlled or moving cars still cannot be retired.
- The next existing 300ms poll can reattempt nearest eligible car/model normally; no pending plan is dropped or invalidated. Fast devices and Editor retain the prior fast path.
- This may delay the appearance of a newly approached parked car by one or more 300ms polls on budget Android hardware during repeated vehicle retirement. That tradeoff requires *visual pop-in checks* before claiming a better player experience. The change does not lower source fidelity or alter rendered objects.
- Native destruction from another streamer's earlier Update may still overlap; the shared gate is a best-effort ordered admission, not a full engine-wide barrier.

## Verification
Run `python3 Tools/Validation/test_android_vehicle_retirement_frame.py`, `test_android_shared_streaming_frame_gate.py`, `test_vehicle_streaming_priority.py`, `test_android_4gb_gpu_streaming_budget.py`, and `check_mobile_hotpaths.py`. GitHub Actions source/parse tests are not native Unity compilation.

Actual acceptance needs real Unity 6000.3.26f1 Play Mode and a Snapdragon 680 / Adreno 610 Android build, driving rapidly between populated zones. Compare p95/p99 frame times, build/destroy Profiler markers, 1% lows, memory, visibility of nearby parked cars and Android logcat. Revert the change if delayed model visibility outweighs stall reductions.

Reference: https://docs.unity3d.com/ScriptReference/Object.Destroy.html
