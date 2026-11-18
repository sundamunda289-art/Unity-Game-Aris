# Map-relative vehicle calibration and wheel alignment

Date: 18 November 2026

## Verified environment
- Unity world units are metres; authored terrain covers 2,000 × 2,000 m.
- There are 39 road segments: 26 roads 8 m wide, 13 town streets 7 m wide.
- Each vehicle retains real wheel geometry, four WheelColliders and its handling class.
- The eight class profiles are maximum car envelopes, not eight stretched boxes.
- Existing eight premium paint colours are retained; body-only property blocks
  avoid recolouring glazing, wheel rubber, and light lenses.

## Actual native Unity measurements after calibrated fitting

| Vehicle | Width m | Height m | Length m | Shell length m | Wheelbase m |
| --- | ---: | ---: | ---: | ---: | ---: |
| Sedan | 1.960 | 1.416 | 4.485 | 4.216 | 2.860 |
| SUV | 1.992 | 1.953 | 4.800 | 4.512 | 2.661 |
| Sports | 1.893 | 1.242 | 4.460 | 4.192 | 2.564 |
| Premium GT | 2.080 | 1.150 | 4.480 | 4.211 | 2.612 |
| Hatchback | 1.781 | 1.472 | 3.900 | 3.666 | 2.323 |
| Van | 2.050 | 1.927 | 4.921 | 4.625 | 2.996 |
| Pickup | 2.064 | 1.936 | 5.420 | 5.095 | 3.301 |
| Buggy | 1.900 | 1.388 | 3.415 | 3.210 | 2.199 |

## Calibration changes
1. Detect Z-up/-Y-forward vendor FBXs only when the importer has not
   already corrected their coordinate axes. Preserve valid prefabs.
2. Replace one-size-fits-all 1.88 × 4.28 m scaling with eight
   category-specific metre envelopes. Ordinary bodies scale uniformly.
3. Narrow the Premium GT X/mirror dimension without deforming tyre
   circles because its Y and Z scaling stays identical.
4. Expand parking collision/ground probes for taller, longer vans and
   pickups, and allow a realistic 5.2 m maximum physics shell length.
5. Update the real Unity photo studio to use the correct current GT
   prefab, include the buggy, and display vehicles at the same game
   sizes alongside a 7 m street surface.

## Verified and still unverified
- Native Unity Editor: AARIS_MAP_VEHICLE_METRIC_PASS eight=8 wheelSets=6.
- Native Unity Editor: AARIS_ALL_VEHICLE_METRIC_PHYSICS_PASS count=8.
- Python regression suite: 107 car tests before photo studio extension.
- This is not proof of Android sustained FPS, full Play Mode driving,
  terrain wheel-ground contact or actual on-device colour perception.
