# ClockTowerTown tower replacement — 12 November 2026

## Scope
The tall two-storey landmark with the narrow tower in the user's live screenshot is `ClockTowerTown_TownHall_01` at approximately (350, -11.937, 245) in the native `TerrainDemoScene.unity`. The authored map layout contains one instance of model `TownHall`.

The scene replaces **only the rendered/collision geometry of this one instance** with the existing `Assets/AarisWorld/Generated/KenneyOpenHomes/OpenDetached.asset` mesh and `KenneyAtlasURP.mat` (single material). This asset is already in the repository, rather than adding redundant render meshes or runtime downloads. Its source is Kenney's low-poly Building Kit, licensed CC0. The authentic source archive was downloaded again for verification on 12 November 2026 from https://opengameart.org/sites/default/files/kenney_building-kit.zip (1,611,408 bytes; SHA-256 b46e4fc873d02dad2ab2153846ede6cc242208e751b778d37f4e75bce23cb2be; ZIP CRC passed, License.txt present). Source credits: https://kenney.nl/assets/building-kit and https://opengameart.org/content/building-kit.

## Transform & layout safety
- Keeps the same TownHall root object/fileID and coordinates, parenting, foundations, roads, surrounding houses, loot and vehicle spawns.
- New scaled mesh dimensions: approx 27.862 m × 12.805 m × 21.359 m, within the prior ~37.24 m × 39.20 m × 24.62 m landmark footprint.
- Uses a physically open entry and unglazed openings; no interactive/closed door or glass meshes are introduced.
- Uses the prefab's existing shared combined geometry with a MeshCollider of the *same* mesh (enabled), so no legacy tower collision remains.
- Preserves the scene's existing three authored LOD renderer slots and screen thresholds (required by map CI) while replacing **all three** TownHall LOD mesh/material references with the same low-poly, open-entry Kenney mesh and shared atlas. The former tower meshes never render at any LOD distance. A single common mesh keeps visual silhouettes consistent; additional decimated LODs can be authored later if mobile profiling warrants them.
- The source asset's physics-test gate expects a traversable doorway/window aperture with solid walls and roof, and ≤5,000 triangles. **This scene change has undergone structural/static validation only; fresh Unity import/Play and Android FPS for this exact main commit still need verification.**

## Multi-agent and review safeguards
The change is limited to this one scene plus this note on a branch created from the actual `sundamunda289-art/Unity-Game-Aris` main commit. It does **not** reset, switch, clean, or merge unrelated changes in the currently open Codespace's dirty branch, and it creates no Git worktree. No changes were made to Kenney source assets, to gameplay scripts, or to `Docs/MAP_LAYOUT.json`.

