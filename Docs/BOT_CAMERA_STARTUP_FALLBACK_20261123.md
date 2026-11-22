# 100-rival camera-startup GPU admission fix — 2026-11-23

## Scope and reason

The production director already uses three independent behaviours:
* near full-quality Humanoid visual (<155 m, with a <=55 m close-range admission);
* camera-facing distant unskinned LOD (<485 m), with no shadows;
* time-sliced logical movement and rival-versus-rival mathematical combat (>=90 m at 0.35 s cadence), independent of rendering.

A first-launch Android camera may not yet exist or may be disabled during a camera handover. Before this patch `IsDistantRivalOnScreen` returned **true** in that case. It could therefore admit distant LOD renderers and PhysX hit receivers despite having no camera evidence. This is particularly wasteful during heavy first-launch imports or landing handover.

## Fix

When the cached gameplay camera is missing/disabled, distant visibility now returns **false**. The normal camera lookup recovers on its existing 2-second retry, and the already-staggered visibility probe wakes remote visuals when the camera is available. The <=55 m close combat visual admission is unaffected. Nothing changes the 100 logical opponents, time-sliced AI, abstract remote damage, authoritative HP/death callbacks, kill feed, or two player-attacker slots. Existing 155 m/485 m quality bands stay unchanged.

## Verification and limitations

`Tools/Validation/test_offline_rival_camera_fail_closed.py` verifies:
* missing/disabled camera does not admit remote visuals;
* distant spawn still disables expensive native capsules;
* close threats remain eligible for full visuals;
* camera recovery re-enables representation independently of mathematical combat.

The source-contract/unit model is **not** a Unity compilation, physical Play Mode test, or Snapdragon 680 on-device GPU measurement. Validate those separately before claiming Android FPS gains. Offscreen logical fighters still incur necessary nonzero CPU for abstract combat; 'zero CPU while fighting' is impossible.
