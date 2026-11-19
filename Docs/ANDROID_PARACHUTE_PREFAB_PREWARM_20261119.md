# Android parachute import prewarm (2026-11-19)

## Audited risk

The original airborne parachute opens by calling `AarisOfflineDropVisuals.CreateElongatedCanopy()`, which imported `AarisDrop/ElongatedParachute` through a synchronous `Resources.Load<GameObject>` at the OPEN transition. That introduces a potentially unpredictable asset-integration cost on an already busy frame. The checked source OBJ is about 71 KB; this is **not proof of a multi-second freeze or actual runtime RAM usage**.

## Minimal change

On Android player only, `AarisOfflineDrop.Start()` schedules `PrewarmCanopyPrefab()` after the normal plane and flight HUD have initialized. The prewarmer first yields one rendered frame, then issues one low-priority `Resources.LoadAsync<GameObject>` for the exact existing canopy OBJ, and caches the resulting prefab. It does **not instantiate** new visuals or colliders during prewarm.

At canopy opening, use the cached resource when available, avoiding the original synchronous import on that transition. If the player ejects and opens extraordinarily quickly, or async loading fails, the original synchronous `Resources.Load` and the original 7-panel fallback remain available; never replace a realistic canopy with a visibly different temporary asset. Editor and non-Android build behavior is unchanged. Static cached references are cleared on Unity subsystem registration.

Original canopy mesh geometry, orientation, layered material colours, rope/harness constraints, opening scale, input timing, and landing physics are untouched. Flight is still visible immediately. This change moves **where** an asset loads, not what it renders.

## Tests and evidence limits

New `Tools/Validation/test_android_canopy_prefab_prewarm.py` checks Android-only loading, first-frame yielding, original prefab/fallback, canopy geometry contracts and asset preservation. Existing offline-drop, cargo-plane, asset, input, player streamer and mobile-budget tests should still pass.

Source tests are not Unity compilation, an APK, a profiler measurement or confirmation that crashes are solved. The current headless Unity Editor license is not valid for compilation. Real Android logcat + profiler traces are still required to differentiate resource integration, GPU stalls, ANR and low-memory process exits.

Relevant sources: `Assets/AarisWorld/Scripts/AarisOfflineDropVisuals.cs`; `Assets/AarisWorld/Scripts/AarisOfflineDrop.cs`.
