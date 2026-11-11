# Aaris Kenney windowless mobile houses — native scene authoring

Status: **candidate implementation, not a verified game fix.**
Do not merge into `main` or claim improved FPS until a genuine Unity Editor scene
preview, scene-save validation, and an Android performance comparison pass.

## Goal

Replace only named residential meshes in the **native 2 km × 2 km terrain
scene** with good-looking compact CC0 meshes: no door leaves, glass panes,
breakable panels, runtime-generated houses, or opaque collision blocking the
central open entrance. Preserve existing houses' **placement, rotation and
footprint**; do not move any roads, terrain, trees, garage spaces or
foundations.

Seven residential styles are supported:

- bungalow_house
- pitched_roof_cottage
- a_frame_cabin
- detached_two_storey_house
- garage_apartment
- two_bedroom_garage_house
- row_three_townhouses

Non-residential landmarks (lighthouse, warehouses, airfield, town hall,
specialty models) are intentionally excluded.

## Source and license

Kenney Modular Buildings 2.1, CC0 1.0:
https://kenney.nl/assets/modular-buildings

Public mirror for the 2024 source ZIP:
https://opengameart.org/content/modular-buildings

Archive SHA-256:
`47e1614686b4c0fe55190e88b22ff2b8b10935edf61f37f478a9b6a99477edc6`

The generated assets include a copy of the actual Kenney License.txt and
PROVENANCE.md. The Python script rejects a different hash or invalid ZIP
and extracts only the four selected CC0 source mesh shapes and color atlas.

## How the author applies changes in the existing Codespace

1. Check `AGENTS.md`, `git status --short`, the live Unity Editor and
   local LFS hydration. Do **not** run `git reset`, `git clean`,
   `git checkout --`, stash, or overwrite other modified source assets.
   Current live Codespace is not assumed to be on the same branch as this PR.
2. Run `python3 Tools/Buildings/generate_kenney_windowless.py` in the
   correct project checkout. It writes new assets under
   `Assets/AarisWorld/Models/KenneyWindowlessHouses/` only.
3. Allow Unity Editor to import the seven combined OBJ files and the one
   atlas texture. Inspect import logs for compiler errors, missing materials,
   and LFS pointer files.
4. In the **already-running Editor**, open
   `Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity`.
   Select **Aaris > Houses > Preview one CC0 windowless house** or send
   `preview` to `/tmp/aaris-codespaces/kenney-windowless.request`.
   This changes one bungalow **in memory only**, focuses the Scene camera,
   and writes a private result to
   `/tmp/aaris-codespaces/kenney-windowless-result.txt`.
   Inspect its real render and the wall / roof / doorway geometry.
5. **Undo the single preview-only operation and verify the scene has
   no unsaved edits** before applying all homes. Do not discard someone
   else's unsaved work. After visual approval and a clean Editor scene,
   select **Aaris > Houses > Bake CC0 windowless houses into native map**
   or send `apply` to the same private request file. The Editor creates
   a **unique timestamped** source scene backup at
   `/tmp/aaris-codespaces/windowless-scene-pre-bake-*.unity`, then changes
   only strictly identified residential objects and saves the original
   scene path. A failed operation triggers a grouped Undo rollback.
6. Verify the private report `AARIS_KENNEY_WINDOWLESS_SCENE_SAVED`, seven
   persistent prefabs, one shared URP material, generated original-license
   record, playable indoor collisions, and 0 glass/door panels in replaced
   homes. Verify that no other authored scene objects, road clearance,
   garage spawn or material edits changed.
7. Compare the original and replaced native scenes at identical camera
   positions, with Unity profiler draw calls, triangles, native heap usage,
   Android 2 GB device frametimes, and screenshots. The expected smaller
   triangle/renderer count is a **design objective**, not yet a measured result.

## Implementation details

Each selected Kenney CC0 cube/roof OBJ is **combined at author time**
into one OBJ per architectural style. Each uses one shared texture atlas
and one URP/Lit material. The Editor then generates **seven real prefab
assets**, adds cheap box-collider sections for walls/ground/roof, and
replaces only the matched scene meshes.

The center entrance has a clear aperture; there are **no movable door
leaves, windows or glass**. This preserves passable entry without the
old one-piece MeshCollider blocking the character.

The tool refuses unknown children, out-of-range dimensions, an unexpected
home count, any unsaved scene, a wrong active scene, a doorway narrower than the
physical player capsule, unexpected ReviewLOD children or any attempt to
operate during Play Mode. New models and editor
scripts should be kept out of runtime scene-generation code.

**Note:** Native map CI requires the original 59 three-level building
LODs before the bake, or the 12 untouched landmark LODs plus exactly 47
persisted replacement home roots afterwards. Partial bake states fail
closed. Compile and Play Mode are still required for runtime proof.

**Known limitation:** Separate runtime settlement expansion still uses
its existing catalog/prefabs; this pilot edits the **native residential
scene only**. That path needs a separate, tested migration if all runtime
expansion houses must also become windowless.
