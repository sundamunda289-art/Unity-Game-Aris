# Android collision-grid cold-start pacing — 2026-11-19

Target: Unity 6000.3.26f1, URP 17.3.0, 4GB Android Snapdragon 680 / Adreno 610.

## Audited source, not a measured root-cause diagnosis

`AarisNearbyColliders.Awake()` built the complete authored static-collider spatial grid synchronously (MeshCollider/BoxCollider metadata, transformed bounds, multi-cell dictionary entries). Previous work already deferred disabling distant colliders at 24 checks/frame, but **did not** spread the grid-construction pass across frames. Unity's Collider.bounds docs state disabled objects have empty bounds; the existing code's original-on preservation is essential.

## Implementation

- On Android only, wait past the initial Awake, then index **at most 96 authored obstacle slots per frame** via `BuildGridAcrossFrames`. Other platforms keep the original full-grid startup behavior.
- Leave all originally enabled authored static colliders **solid** until the *complete* grid exists. Do not stream against a partially built grid, including teleports, player movement and inactive-collider overlap probes.
- Preserve the precise existing transformed native mesh/box bounds, all cells traversed by long structures and the 55-metre activation / 6-metre release threshold. Terrain, native trees, unsupported collider kinds and intentionally authored-off shapes are untouched.
- Resume from a cursor advanced **before** yielding, so disabling/re-enabling the component does not duplicate indexed cells; stop both pending coroutines on disable. Restore originally live colliders exactly as before.
- Once complete, invoke the existing full `RefreshNow` and prior deferred deactivation logic. Index arrays are allocated once; no per-frame new arrays and no change to graphics quality, foliage or scene assets.

## Validation and caution

- The new lifecycle test checks fail-solid behavior, ready-state guards, suspension/resumption and random synthetic eager-vs-batched grid-cell parity including cell seams and long buildings.
- `test_android_collider_boot_budget.py` now validates the new indexing lifecycle rather than obsolete in-Awake implementation details. Existing code-state tests also validate preserved authored-disabled/unsupported-collider semantics, now implemented with `return` in `IndexCollider` instead of loop-local `continue`.
- Test runs in the Codespace temp mirror are Python source/algorithm validation, not Unity Player/Play Mode or an on-device Android crash/FPS measurement. No claim this resolves the reported 3–30s freezes. Actual Profiler and logcat evidence remain necessary.

Official Unity references: https://docs.unity.cn/6000.1/Documentation/ScriptReference/Collider-bounds.html and https://docs.unity.cn/ScriptReference/MonoBehaviour.StartCoroutine.html.
