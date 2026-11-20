# Constrained Android motorcycle retirement frame budget (2026-11-20)

## Source finding
`AarisRoadsideBikeScenery.Update` polls a bounded set of **parked scenery** bikes every 0.4 seconds. A distant bike can be retired and `Destroy` scheduled (e.g., pooled models have been purged after Android low-memory). That same poll can immediately `CreateBike` for another nearby point. Unity's object destruction is deferred until after the Update loop, so native collider/mesh teardown and brand-new hierarchy integration may collide within one frame.

## Scope and fix
After determining the nearest eligible parking point and whether a previously visible bike can be reused, constrained Android devices defer a new bike hierarchy when **all** of the following are true:
- at least one distant bike was retired in this poll,
- no existing parked-victim bike can be recycled into the new position,
- no inactive pooled motorcycle is available.

The existing shared `AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame` reserves the current frame against later optional work, then the ordinary 0.4-second poll retries the same candidate. The guard runs before the original three-second invalid-position retry timer and physics placement scan, so a scheduling delay is not mislabeled as an invalid site.

## Invariants and limits
- Original source/model, paint materials, PBR textures, colliders, distances (45m load, 66m unload, 110m prewarm), 3-bike visible limit, pooling, parking plan, and landscape are unchanged.
- Pool reuse and recycling a parked-victim bike continue immediately; stronger devices and Editor are unchanged.
- A fresh nearby scenery bike can become visible a poll later after a teardown frame; this requires on-device A/B testing for visible pop-in.
- This is source-level *potential* hitch mitigation, **not measured FPS improvement**, not a Unity Editor compilation, and not a 4GB Android test.

Run `python3 Tools/Validation/test_android_bike_retirement_frame.py` and `test_android_shared_streaming_frame_gate.py`, `test_roadside_bike_visuals.py`, `check_mobile_hotpaths.py`. Device acceptance needs actual Unity/Android build and populated-road driving, with p95/p99 frame time, 1% lows, memory, graphics quality and spawn visibility captured.
