# 4 GB Android vehicle lamp-detail LOD — 19 November 2026

## Why
The vehicle's already-authored PBR body and wheels must stay visible at all ranges. Each streamed car also creates **ten** small URP Lit decorative overlays (bezel/head/brake/reverse lenses). At 70+ metres the overlays can occupy less than a pixel while still causing CPU renderer traversal and GPU draw submissions.

## Change
- Parent only those ten decorative meshes beneath one new empty, collider-free `Mobile Vehicle Lamp Detail` transform.
- Every 0.4 seconds, compare squared distance to the existing player transform: hide above 68 m, re-enable below 52 m, preserve state in the middle. A **driven car always shows** the overlays.
- Perform the check **before** sleeping-car visual early return so walking toward an idle parked car restores detail.
- Keep the original URP PBR shells, paint, window surfaces, four WheelColliders, physical suspension, spinning tyres, brake state and one optional occupied headlight unchanged.
- No additional textures, particle emitters, shadow-casting lights, new shader variants, per-car material clones, per-frame allocations or native wheel queries.
- Optional `viewer` parameter is null-safe for existing editor probes using the original method call.

## Validation and limits
- `python3 Tools/Validation/test_vehicle_lamp_lod.py` checks 10 meshes share the root, 52/68 m hysteresis, driver override and non-physics scope.
- Existing vehicle static tests still need to run on a full checkout.
- **Source-contract tests do not prove Unity compilation, in-game screenshots or Android FPS**. Do not interrupt another Editor process or publish screenshots captured from private Codespace displays. Native Editor Play Mode, Android on-device frame-time and thermal verification remain necessary.
