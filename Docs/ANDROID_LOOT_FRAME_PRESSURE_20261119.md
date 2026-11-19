# Android 4GB loot model integration pacing — 2026-11-19

**Target:** Unity 6000.3.26f1 / URP 17.3.0; Snapdragon 680, Adreno 610 and 4GB RAM.

## Verified source-level issue

`AarisWeaponLootWorld.StreamNearby` previously allowed **two fully authored pickup GameObject/model integrations in one rendered frame**, even when a 4GB phone already showed sustained slow frames. Each pickup creates its original source FBX, pivot, renderer-dependent bounds and subtle beacon. This is a **possible source of an additional CPU/GC spike during movement**, not proof that pickup loading caused the reported 3–30-second whole-phone stalls.

The original streamer already caps active physical pickup objects at 40, bounds candidate physics checks at six per streaming frame, bounds unloads, and protects the guaranteed starter fallback; these controls are retained exactly.

## Narrow policy, quality preserved

- Detect the low-memory / GPU constrained policy **once at match initialization**, not in `Update` or `StreamNearby`. The same `AarisMobileRuntime.UseConstrainedGpuStreamingBudget` logic already covers the Snapdragon 680 Adreno 610.
- On Android only, when `Time.smoothDeltaTime > 1/45` seconds **and** the device is budget-class (4GB RAM or targeted GPU), cap **new native pickup model build attempts at one per frame**. Do not block streaming entirely. When frames recover, original limit of two returns automatically; Editor and other devices are unchanged.
- The budget applies to both dropped and planned ground pickups. Preserve dropped-item state, authored catalog/meshes/LODs/materials, 40 live-object cap, 50m activation/69m release, 6-probe CPU cap, stable deterministic match choices, full ground/cover/LOS checking and auto-starter reserve.
- No texture resolution, foliage, world terrain, graphics API, UI, collision, weapons, physics or player control values are changed.
- No per-frame GPU name parsing, new collections, synchronous `GC.Collect` or `Resources.UnloadUnusedAssets` calls.

## Testing and limits

Run:
```bash
python3 -m unittest discover -s Tools/Validation -p 'test_android_loot_frame_pressure.py' -v
python3 -m unittest discover -s Tools/Validation -p 'test_android*.py'
python3 Tools/Validation/check_weapon_loot.py
```

The new tests cover constrained-device classification outside hotpaths, two-vs-one model budgets, retained collision and starter code, and synthetic no-starvation progress. GitHub Actions C# parser and game/world regression gates are required to merge.

No Unity native compilation, device Profiler capture or Android crash/ANR proof is obtained by Python source tests. A temporary reduction in *initial* pickup visibility speed on slow 4GB frames is intentional; original geometry and steady-state model count are preserved. The previously added `Aaris.Streaming.VehicleBuild` CPU marker is unrelated and retained; device Profiler evidence is needed to measure actual gain.
