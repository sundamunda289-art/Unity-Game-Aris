# SpeedTree v3 — Natural species-aware trunk taper

## Implemented in the Unity import pipeline

`Assets/AarisWorld/Editor/AarisTreeShapeImporter.cs` now uses a bounded,
species-aware shaping curve for the six existing reviewed SpeedTree source
profiles. This builds on the v2 **base thickness and collider** upgrades already
in `main`: v3 keeps the same base diameter, crown diameter, imported height,
collider, LOD thresholds, billboard presence, material assignments and terrain
placements. The heavier lower **and middle** trunk tapers smoothly toward the
same upper crown; no whole-tree Terrain TreeInstance width scaling is applied.

For normalized tree height `u ∈ [0,1]`:

- `s = u*u*(3 - 2*u)`
- `contrast = clamp(baseScale/crownScale - 1, 0, 1)`
- `bias = 0.35 + 0.25*contrast` (different tree species, controlled taper)
- `t = s - bias*s*(1 - s)`
- `radial = lerp(baseScale, crownScale, t)`

`t` is continuously monotone on [0,1], begins at zero and ends at one.
Therefore the base and crown **never exceed the already-approved v2 radial
limits**. Every species retains its distinct reviewed silhouette, including
relatively slimmer Cypress and some Pine variants rather than one identical
shape for every tree. Existing individual Terrain TreeInstance width variation
is preserved. The extra middle-trunk fullness does not make distant tree
crowns disproportionately large.

One analytic derivative is used for the normal and tangent transformations;
the same radial shape is applied to SpeedTree UV2 next-LOD positions and
UV1/UV2 wind/leaf facing anchors. Original authored UV0, wind weights and
vertex topology are not replaced. The importer version increases from 2 to 3
to request a clean reimport of these prototypes in Unity Editor.

## Source scope and tests

- Changed implementation: `Assets/AarisWorld/Editor/AarisTreeShapeImporter.cs`.
- Existing six `.st.meta` profiles and six `.prefab` files retain their exact
  v2 reviewed Git hashes; no `.st` binaries, `.asset` TerrainData, `.unity`
  scenes, material files or GUID `.meta` identities change.
- `Tools/World/verify_tree_realism_v2.py` retains hash/GUID/collider/LOD
  validation and now recognizes the version 3 importer.
- `Tools/World/tests/test_tree_taper_v3.py` checks that all six tree families
  have natural differences, that the taper is monotone with unchanged
  endpoints, and that analytic derivatives match finite differences.
- `.github/workflows/tree-realism-v2.yml` runs the new tests and allows
  importer-only changes (zero edited source tree assets), while rejecting
  partial/unapproved source-asset edits.

## Mandatory Unity release gate

Passing Python source checks is not the same as imported 3D visual quality.
In the safe `/tmp/aaris-unity-batch/project` copy, trigger Unity 6000.3.26f1
SpeedTree reimport; inspect tree bark, full lower trunk and crowns at forest,
road and slope cameras. Compare same-camera screenshots, LOD and wind
transitions, check tree collision and mobile FPS/thermals. Keep the original
dirty `/workspaces/Unity-Game-Aris` and every original Git LFS asset intact.

The same six reviewed prototype meshes are shared with 7,508 native terrain
trees; there is no source scene reserialization or per-tree push. Avoid
claiming an observed AAA-quality rendered outcome until the Editor confirms it.
