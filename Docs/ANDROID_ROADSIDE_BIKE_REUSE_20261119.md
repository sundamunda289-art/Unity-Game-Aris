# Android roadside motorcycle instance reuse — 2026-11-19

Target: Unity 6000.3.26f1 / URP 17.3.0 / 4GB Android, Snapdragon 680 and Adreno 610.

## Audited scene-runtime work

`AarisRoadsideBikeScenery.Update` already rate-limits parking probes to 400ms, streams only three authentic imported motorcycle meshes, and asynchronously prewarms the original prefab when the player is close. However it destroyed a motorcycle GameObject when outside 66m and called `CreateBike` / `Instantiate(bikeSource)` again when loading a new 45m parking bay, including on three-slot priority swaps. Repeated native Instantiate/Destroy and renderer/LOD/material setup can cause avoidable allocation churn while moving through roads. This is a source-level potential contribution; no measured frame-time spikes have been attributed to it.

## Safe bounded reuse

- Preserve the **same real original** OBJ/prefab, mesh, native BoxCollider, paint palette (four deterministic variants), shaders, shadows, detail, authored parking coordinates and rotation.
- Reuse a previously instantiated rig when a distant model unloads; keep no more than three pooled originals. Keep the original three *visible* bike cap.
- Once the candidate road point has passed the **unchanged ground, slope, obstacle, streamed-collider checks**, reuse the farthest existing rig when replacing it, instead of destroying it and instantiating another. An unsuccessful candidate probe never moves or removes the visible model.
- Cache existing shared `Material[]` slot layouts for the four paint finishes only when the rig is first built. On reuse update the same renderer's `sharedMaterials` to the new plan ID's deterministic paint; metal, rubber, seating and lamps stay on the unchanged original material slots. No new Unity Material assets are allocated during swaps.
- Temporarily deactivate the rig before moving to the new validated position, then reenable. This unregisters and reinstates its original BoxCollider without an unconditional global `Physics.SyncTransforms()` call.
- On Android/Unity `Application.lowMemory`, flag the warning without destroying scene objects inside the callback. The following regular `Update` frees **only hidden pooled** motorcycle instances and suspends new caching for 60 seconds. All visible motorcycles remain unchanged; priority replacements can still reuse existing visible rigs without increasing memory.

- Leave the existing `ResourceRequest` prewarm and `LODGroup` (0.012 threshold) untouched. Scene shutdown automatically destroys this component's child instances and only then disposes the shared runtime materials.

## Validation and caveat

`test_roadside_bike_visuals.py`, `test_android_roadside_prewarm.py` and the new `test_android_roadside_bike_reuse.py` protect source contracts, placement safety, fixed material categories and synthetic three-rig bound. CI does not replace Unity compile, profiler, Play Mode visual QA or Android crash/ANR evidence. Original phone symptoms must be measured after installing and testing a new APK; do not claim this change alone resolves them.

## Cross-branch integration gate

The concurrently merged tactical HUD startup change initially caused a C# preprocessor syntax validation failure unrelated to motorcycle code. Canonical `main` subsequently merged the targeted fix in #714. Re-run all PR checks against this corrected main before accepting the bike pool optimization; don't waive a failed C# syntax gate.
