# Android roadside bike low-memory cleanup admission — 2026-11-20

## Source-level problem

`AarisRoadsideBikeScenery` defers the Android `Application.lowMemory` callback into its normal `Update`, where it destroys up to three inactive, pooled motorcycle roots. In the **same** update, the original control flow could continue through async model prewarm, URP material integration, ground probing and fresh optional motorcycle `CreateBike`. Unity schedules actual `Destroy` native teardown after Update, and the bike script's early execution order can place it before the main low-memory runtime has started `Resources.UnloadUnusedAssets`.

Therefore, an Android low-memory frame could paradoxically add native scene work while releasing stale cached assets. This is a code-level *plausible source of pressure*, not a measured phone frame stall.

## Narrow change

- Continue releasing only inactive pooled motorcycle instances. Never delete active visible scenery, parking plans, source assets, material caches or world geometry.
- On Android player builds only, after the existing low-memory pool release block, call the pre-existing global `AarisMobileStreamingFrameGate.ReservePhysicsStreamingFrame()` and return from this one Update, even if the inactive pool was already empty.
- The shared reservation blocks optional model integration for other later-updated streamers on *constrained* Android; the bike itself does no new model work on the notification frame on any Android. The normal 0.4-second poll retries without marking a parking site invalid or consuming a new slot.
- On desktop and inside Unity Editor, the prior path is unchanged.

## Limitations

This does **not** guarantee that another component earlier in execution order will not initiate work before the reserve. The existing low-memory `UnloadUnusedAssets` fence still independently controls imports while native cleanup is pending. A cache-poor phone may see a roadside bike appear later after an actual memory-pressure event, but its final visible model and authored textures are unchanged.

Tests: `python3 Tools/Validation/test_android_bike_low_memory_cleanup_fence.py`, `test_android_bike_retirement_frame.py`, `test_roadside_bike_visuals.py`, `test_android_shared_streaming_frame_gate.py`.

Actual validation requires Unity 6000.3.26f1 compilation and a real Snapdragon 680 Android build with low-memory event logging, memory snapshots, p95/p99 frame times and visual checks.
