# AI attack range after movement — 2026-11-24

Two confirmed code defects in AarisOfflineBRDirector/AarisOfflineRival:

1. Real physical bots made movement with CharacterController.Move before checking attack reach and facing, but used the distance and bearing calculated before the move. At the 38m gun / 2.25m melee boundary a bot could legally fire from outside the limit or fail to fire just after entering range.
2. Time-sliced virtual rivals similarly walked using 350ms movement increments and then used the old distance both to authorize their next abstract shot and to calculate hit chance. The virtual LOS/terrain cover gate correctly used the new position, so its attack range disagreed with its collision/cover origin.

The fix uses the same horizontal-range policy but post-movement shooter pose. Physical shots still require the existing actual first collider, muzzle clearance, angle, ammo, attack cooldown and two-player-attacker authorizations. Virtual shots still cannot damage the player or a real/nearby rig, still consult static-cover geometry and still roll the same hit probability formula using the updated distance. No additional Physics.Raycast, colliders, 3D models, shaders, per-frame allocations or bot ticks.

Validation: Python edge cases for retreat, approach, melee, direction, and vertical stance-height; native Unity Editor C# test uses reflection to execute the actual compiled game methods. Neither an isolated Editor QA nor a source-only compilation proves Snapdragon 680 FPS on a device.
