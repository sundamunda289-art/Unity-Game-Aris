# Android authored-wheel hierarchy lookup allocations (2026-11-21)

## Existing source cost

For an imported full-body 3D car, `AarisVehicleMotor.AttachEmbeddedWheelModels` examines the body hierarchy, finds four authored tyre meshes, validates their actual chassis-space bounds and then attaches the original visual wheel objects to the four real WheelColliders. Before this change it created a temporary `Transform[]`, four-element `Transform[]`, four-element `Bounds[]`, lowercased every examined child name, and made four new `Collider[]` queries. That work coincides with managed/native per-car instantiation on budget Android CPUs, even when the source is a shared model. No evidence currently proves these particular allocations caused the owner's stalls.

## Update

- Unity 6000.3 caller-owned lists receive imported Transform and Collider query results without new result arrays on each car spawn.
- Reuse four-element found/bounds buffers and a static, original `wheel_fl/fr/rl/rr` token table. Match case-insensitively without allocating lowercase copies of irrelevant FBX node labels.
- Keep the original mesh visibility checks, chassis-space wheel sizes, authored tyre centre offsets, front/rear axle plausibility, collider disabling, WheelCollider radius/position, and spring mass reset exactly in the original order.
- Clear source-hierarchy and collider list references and temporary found wheel pointers in `finally`, even for rejected malformed sources. This prevents an invalid or retired FBX body from being retained by the resident motor object.
- No new worktree, authored mesh/mat/scene edits, texture-quality downgrade, extra physics raycasts, or changes to active vehicle caps.

## Verification scope

`Tools/Validation/run_embedded_wheel_lookup_csharp.py` injects the real production wheel method and original axle geometry guard into a controlled C# test fixture. Positive and negative 49-child test cases verify mixed-case wheel names, original wheel identity, collider disabling, radius, actual chassis attachment, invalid axle safety, and clean retry without stale references. Existing vehicle visible body/road/contact tests and production C# parse run under CI.

A Mono Unity API fixture is **not native Unity 6000.3.26f1 or Android IL2CPP** and proves no measurable Snapdragon 680 FPS benefit. A physical phone still needs matched-paired cold/warm spawns, GC allocation per vehicle, frame-time p95/p99, crash logcat and screenshot equivalence tests.

Unity 6 API: https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/component/getcomponentsinchildren
