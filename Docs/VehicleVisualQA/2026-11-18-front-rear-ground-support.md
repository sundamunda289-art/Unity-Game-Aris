# Front/rear axle support before vehicle downforce — 18 November 2026

## Real-world-looking failure

Prior handling code required **any two wheel contacts** before applying
the extra bounded road-car aero/downforce at speed. However, the two grounded
wheels could both belong to a single axle while the other axle was off the
ground at a road crest, curb, bridge lip or ramp exit. Applying artificial
downforce in that state can pull a partially airborne car downward and
produce an unnatural pitching/landing response.

The decorative cabin-pitch/roll calculation used the same two-wheel count
and could likewise respond to a single supported axle.

## Corrective gate

Use the existing **four WheelCollider GetGroundHit** results in the existing
two anti-roll passes. Store a local 2-bit mask: front axle=1, rear axle=2.
Each pass sets its bit if at least one tyre on that axle is grounded.
The mask is reset once each physics step, alongside groundedWheelCount.

At speeds over 6 m/s, downforce is now allowed only when at least two
wheels have ground contact **and** one belongs to each axle (mask=3).
Decorative cabin sway requires the same complete support. A car riding
off one axle remains governed by actual Rigidbody and WheelCollider
physics, not extra visual lean or suction.

No more raycasts, collider lookups, GameObjects, per-frame allocations,
changes to suspension curves or tyre materials. Existing steady four-wheel
driving and diagonal two-wheel support still work.

## Validation boundary

New source and numeric regression covers all 16 combinations of wheel
contact, including front-only/rear-only/airborne, diagonal contacts and
full support. Existing mobile/vehicle QA must pass before merge.
Unity Play Mode road crests, tyres touching actual meshes and Android
FPS are not yet represented by these offline checks.
