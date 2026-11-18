# Signed-speed, direction-aware brake distribution

## Root problem

The prior `ApplyWheelForces` made service braking front-biased whenever
the Rigidbody's **total** velocity exceeded 2 m/s. This included a
fast reverse manoeuvre, where braking weight transfer is toward the rear,
and sideways slides, where lateral velocity does not justify either
longitudinal bias. These cases could make the car feel unnatural.

## Correction

Re-use the motor's signed `SpeedMetresPerSecond` (dot of Rigidbody
velocity and chassis forward). With an actual occupied service brake
(500 <= torque < parking torque), speeds >2 m/s produce modest
front bias (1.12 front / 0.88 rear), speeds <-2 m/s modest rear bias
(0.88 front / 1.12 rear), and low longitudinal speeds, lateral skids,
coasting and parking remain balanced (1.0 on each wheel).

Unlike the previous boolean cache, a signed three-state integer cache
ensures wheel brakeTorque refreshes when direction changes even with
identical held brake input. Total applied brake torque remains unchanged
across every bias: 2*(1.12+0.88)*torque = 4*torque.

This changes only the per-wheel distribution already used by the existing
four WheelColliders. No physics raycasts, new transforms, allocations,
renderer changes or additional driving input controllers are introduced.

Unit tests cover forward/reverse, 2 m/s boundaries, high-speed lateral
skids, stopped/parked/driverless states, torque conservation, native-source
cache invalidation and hotpath constraints. Real Unity cornering,
stopping distances, contact patches and Android FPS remain to be tested
in Play Mode (not claimed by the offline tests).
