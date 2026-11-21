# Android authored car model validation: shared hierarchy query scratch (2026-11-21)

## Source finding

The existing car preload/integration code searches imported FBX hierarchies for four semantic wheels and optionally for a credible body-only lamp fitting mesh. Both `HasEmbeddedWheelSet` and `TryGetAuthoredLampBodyBounds` allocated a new managed `Transform[]` result from `GetComponentsInChildren<Transform>(true)` per call. The wheel check also lowercased every unrelated child name before checking four English/ASCII wheel tokens. This added avoidable allocations during a native vehicle spawn on memory-constrained Android hardware.

## Scope of fix

- Reuse the existing thread-local `namedWheelQueryScratch` List already used by `FindNamedWheel`, rather than introduce another temporary hierarchy. Queries use the Unity 6000.3 `GetComponentsInChildren<Transform>(true, List<Transform>)` API.
- Preserve all originally supported inactive hierarchy nodes; the first matching, credible `_body` mesh still determines optional rear-lamp placement; original size/collision envelopes and fallback path are unchanged.
- Check wheel labels with `IndexOf(..., StringComparison.OrdinalIgnoreCase)` rather than allocating a lowercase `string` for each hierarchy node. All four original semantic wheel tokens remain required.
- Clear reusable buffers even on early success or an exception; streamed-out FBX transforms are not retained. No new worktree, no change to parked-car count, road positions, graphics, shadows, textures, geometry, visibility, or physics.

## Validation and limits

New controlled Mono C# test injects **both actual production methods** and exercises 250 repeated mixed-case wheel/body checks, visible-versus-oversized/invalid body bounds, incomplete wheel naming, null chassis/source and exception cleanup. Original imported-vehicle fidelity, road/axle fit and wheel-name source tests, plus production C# parser, run in CI.

These managed tests cannot prove Unity Editor 6000.3.26f1 compilation or real Snapdragon 680/Adreno 610 FPS changes. Physical acceptance still needs matched before/after APKs, measured car-spawn GC Alloc and p95/p99 frame times and visual equivalence screenshots. No quantified improvement claimed.

Unity 6000.3 API reference: https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/gameobject/getcomponentsinchildren
