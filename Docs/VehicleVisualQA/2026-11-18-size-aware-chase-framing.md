# Vehicle chase camera that matches actual car size

Date: 18 November 2026

## Why
The verified Premium Road GT coupe is 4.48 x 2.08 x 1.15 m,
whereas SUVs, pickup trucks and vans have significantly taller and
longer bodies. The old camera always targeted 1.58-1.74 m above the
vehicle root and followed from a fixed 6.25-7.40 m distance.
That can place its sightline above a low GT roof and make cars look
toy-sized despite their correct physical scale.

## Change
On successful vehicle entry, read its fitted BoxCollider ONCE.
Cache:
- Chassis length, bounded to 3.0-5.3 m.
- Aim height from 62% of the solid chassis height above its root,
  bounded 0.68-1.65 m, protecting unusual imported geometry.

Then in the existing chase camera:
- Distance: 6.25-7.40 m plus a small length offset (-0.24 to +0.36 m).
- Height: 2.75-3.05 m plus a small aim-height offset (-0.15 to +0.10 m).
- Target: cached chassis-relative aim height, rising only 0.12 m
  as speed approaches the configured top speed.
- Preserve world-up heading projection, buffered sphere-cast obstacle
  avoidance, 0.15s frame-time cap and 62-70 degree speed FOV.

No extra meshes, shaders, colliders, camera raycasts, per-frame
component lookups or rendering allocations.

## QA
The vehicle camera metric framing regression test covers GT, SUV,
pickup, sedan and buggy at parked and moving speeds, and preserves
collision handling and low-FPS camera smoothing.

Actual Android FPS, terrain driving and in-game camera footage
remain to be verified separately.
