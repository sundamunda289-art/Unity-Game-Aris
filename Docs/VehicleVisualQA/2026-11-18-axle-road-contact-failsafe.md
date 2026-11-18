# Vehicle axle geometry and road-contact failsafe — 18 November 2026

## Gameplay failure prevented

In the previous build, `AlignEmbeddedTyresToRoad()` returned a
meaningful boolean after inspecting four authored wheel bounds. But
`Build()` **ignored that return value**, continued constructing
WheelColliders and marked the car spawned even for a tilted/broken
four-tyre rig. Such art could hover, clip into roads, or cause a wheel
to appear on the wrong side of the vehicle. The bug is visible only in
problematic Unity import cases, so ordinary regression success missed it.

`Build()` now refuses the invalid authored model and invokes the
existing verified vehicle-resource fallback for later spawns. Original
record positions, previously driven positions, damage, parking logic
and fallback-safe cached assets remain unchanged.

Separately, `AttachEmbeddedWheelModels()` checks real mesh centres
in chassis space **before** disabling visual colliders or moving
WheelColliders: front track and rear track each 1.02–2.40 m, front
axle ahead of rear by 1.55–4.65 m, paired wheel heights and forwards
positions consistent, and axle midpoints not far outside the shell.
Legitimate hatchbacks, SUVs, van, GT, long pickups and buggies remain
within these deliberately broad physical limits.

No additional physics force, rigid body, update-loop allocation,
rendering shader, texture or geometry is added.

## Verification scope

Existing vehicle/art checks and new deterministic source checks cover
accept/reject cases including the measured ~1.80m GT axle track.
Native Unity Play Mode must still be used to certify real imported
mesh transforms, tyre contact, road screenshot and Android frame rate.
