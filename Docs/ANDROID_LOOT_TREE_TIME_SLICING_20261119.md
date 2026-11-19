# Android terrain-tree loot time slicing — 2026-11-19

**Target:** Unity 6000.3 / 4GB Android / Snapdragon 680 (Adreno 610).

## Actual source-level bottleneck candidate
After the Android weapon-prefab async pass, the loot world planned native
tree cover **synchronously**: four Terrain tiles, at most 240 candidate
attempts per tile, plus per-candidate spacing checks. This might add a
first-match frame spike; a 3–30 second stall **has not been proven** to
originate here. The underlying `TerrainData.GetTreeInstance` remains the
existing no-whole-tree-array allocation path.

## Implementation
- After the original validation and match RNG seed, Android player builds
  start a coroutine to sample native trees in bounded batches of at most
  **32 attempts per rendered frame**, yielding again after each tile.
  This budgets *attempts*, not accepted tree placements, so dense/rejected
  areas cannot consume all 240 attempts on one frame.
- No intermediate loot list/choices/cell grid is exposed: only after the
  final sampled tree do we publish candidates and plan weapons. The existing
  `Update()` gate already ignores `candidates == null`.
- Desktop and Unity Editor retain synchronous planning. Both paths use
  the **same** tree-sampling iterator, RNG call order, acceptance test,
  48-per-tile cap, 32m spacing, match seed and home coverage guarantees.
- Player model and gun visuals, 40-live-weapon cap, collision/LOS,
  actual terrain/trees, spawn rate, graphics, importers and shaders unchanged.

## Check
```sh
python3 Tools/Validation/test_android_loot_tree_budget.py
```
Run the existing loot/landed-pickup regressions and GitHub CI too. The added
frames could delay availability by a fraction of a second; verify on Android
that loot is present after landing and that the planned total/indoor coverage
is unchanged for a fixed RNG seed. Real performance claims require an Android
build, per-frame profiler timing and logcat; static tests alone do not prove
zero hitches, successful Unity compilation, or no low-memory kills.
