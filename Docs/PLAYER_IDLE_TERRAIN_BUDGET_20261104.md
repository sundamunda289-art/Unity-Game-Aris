# Grounded player: avoid idle terrain query work (2026-11-04)

## Source finding

The saved `Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs` called `SafeFooting(candidate)`
unconditionally in `Update()`, even when the two-dimensional movement vector
`wish` was exactly zero. This helper samples native Terrain heights and, for
underwater terrain, performs a physics support raycast. A stationary player
does not need an additional *candidate-movement* shoreline check every frame.

## Actual core change

The shoreline, map-boundary and bridge/solid-support test is now entered
only when the horizontal movement vector is nonzero. For each nonzero move,
the same candidate position and the same `SafeFooting(candidate)` decision
are evaluated before moving. The existing `CharacterController.Move`,
gravity, jump/coyote-time, nearby-collider refresh, camera occlusion casts,
scene prefabs, source tree meshes, houses, terrain, LODs, horizon, and asset
GUIDs remain unmodified. No saved `.unity`, `.prefab`, `.meta` or
TerrainData changes are included.

## Validation and claims

`Tools/World/tests/test_player_idle_footing.py` guards the movement-specific
condition, continuing idle gravity/CharacterController motion, bridge
support and camera/collider policy. The existing `check_contracts.py` now
also parses the player C# source for syntax errors. These are source checks
and cannot prove Unity physics or FPS gains.

On the matching Unity 6000.3.26f1 / URP 17.3 project:
1. Compile the modified script and confirm no exceptions.
2. Compare an idle player against the unmodified APK with CPU Profiler
   (script, Terrain.SampleHeight and Physics.Raycast call counts) for
   60 seconds on equivalent build and scene.
3. Walk and jump across shorelines, bridges and solid quay ramps; test very
   small joystick inputs, HOME teleport and the world-edge boundary.
4. On physical 2 GB and 3 GB Android devices, capture p95 frame time,
   graphics-thread time, PSS, temperature, and screenshot differences before
   claiming any gameplay benefit.

This is one bounded CPU-side improvement, **not** a redesign of tree shapes,
house dimensions, world placement or asset residency. Device FPS,
silhouette quality and accessible windows must still be tested.
