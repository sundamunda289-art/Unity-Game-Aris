# Mobile canopy and ground foliage — 2026-11-25

## Scope

The permanent base was main 472e4a07. The existing Codespace's related
8114cfe8 wind/contact commit is included, not replaced. The native 2 km
island, four TerrainData tiles, tree/plant counts, source meshes, LOD groups,
textures, collision geometry, shadow budgets and URP render scale are unchanged.

- The custom SpeedTree shader computes an outward, ellipsoid-inspired leaf
  lighting frame from stable packed card anchors. It preserves the authored
  local normal detail, reprojects the tangent, excludes branch/cap geometry,
  and shares the forward and DepthNormals orientation. The same alpha clip
  and wind displacement remain in depth/shadow/selection passes.
- Sun transmission reuses the URP main light already carrying shadows;
  roughness/metallicity are bounded for dielectric leaves.
- Seven native ground-foliage materials get profiles based on their measured
  mesh bounds. The active TerrainGrass graph shares canopy normal/tangent
  reconstruction, root height contact, stable instance tint and bounded
  pseudo-transmission. Ordinary grass has the neutral profile. Existing
  animation-distance fade skips expensive canopy reconstruction far away.
- Bush_Red previously used generic URP Lit. It now uses the existing
  TerrainGrass graph, retaining its material GUID, original base/normal/mask/
  thickness references and .5 alpha cutout. The same material is used by
  the standalone red shrub prefab. The existing global player/vehicle vector
  bends anchored roots/tips; large shrubs react at their outer vertices.
- Player/vehicle contact now uploads inverse interaction-radius-squared once
  from C#. The Terrain detail vertex path normalizes squared distance with
  one multiply, removing the previous per-vertex radius square and divide
  while preserving the exact 1.25 m foot and 2.6 m vehicle falloff.
  This continuation was revalidated in Unity 6000.3.26f1: the disposable
  native C# source project exited 0 with zero compiler errors, and a separate
  disposable GLES3x/Android shader gate compiled 10 selected TerrainGrass
  combined-stage variants plus 35 selected SpeedTree variants with zero
  failed variants and zero GLES3x shader-message errors.
- Cypress cutout materials use the shader's AlphaTest queue. Bush_A's
  texture preserves coverage at its actual .482 threshold.
- A separate experimental change to six 3D leaf alpha-mip reference values
  (.5 to .3333) was compiled and rendered. Same-camera native frames showed
  visibly thinner distant tree silhouettes. It was rejected and each
  original importer file restored byte-for-byte. The accepted shader's
  runtime alpha clip stays .3333; the authored tree mip fullness stays .5.


- Authored distant 3D SpeedTree leaf materials use the Fastest/default wind
  variant. That variant now takes a normalized hemispherical canopy frame
  instead of rebuilding the full ellipsoid crown gradient. Distant chroma
  variation now uses the stable tree pivot instead of a full per-leaf world
  anchor transform; near leaves keep exact per-leaf variation, the full
  volumetric canopy normal and micro-flutter. Billboards are unchanged. The accepted candidate compiled 48 Android GLES3x variants with
  zero shader/C# errors. Same-camera tree A/B readbacks stayed effectively
  unchanged (close RMS 0.163/255, wide-forest RMS 0.196/255) while removing
  crown-gradient and extra normalization work from the distant leaf vertex path.

## Evidence

The existing Codespace's Unity 6000.3.26f1 / URP 17.3.0 completed the
independent native-scene candidate batch with exit 0, zero C# compiler
errors, zero shader errors and 12 distinct material/pass compilation
requests. 41 referenced materials were examined, including 5 shaders.
The baseline and accepted candidate both had 7,508 painted trees, four
terrains and 30 occupied bush/fern/heather prototype slots; all 30 use
Terrain mesh GPU instancing. The six occupied undergrowth names account
for 57,023 painted density units, including 179 Bush_Red units; these are
density-map units, not measured simultaneous visible instances.
The accepted source asset hashes match the zero-error candidate following
the byte-for-byte rejection of the thinner-mip experiment.

The static workflow hydrates only the small native detail material LFS objects, not the full terrain or textures.

Source regression commands:

- python3 Tools/Validation/test_mobile_foliage_illusion_contract.py
- python3 Tools/Validation/test_mobile_tree_shadows.py
- python3 Tools/Validation/test_tree_proportions.py

The 12 foliage groups include 12,000 numerical canopy/tangent stress
cases, continuous wind/interaction and graph bindings, in addition to
3 native shadow tests and 10 tree proportion tests. Actual native painted
tree, terrain, shrub and red shrub camera readbacks were taken using Unity.
The separate QA project is temporary, not a Git worktree; no unrelated
local project changes were staged.

## Rendering budget and limits

No new runtime C# component, collider, physics query, per-renderer
MaterialPropertyBlock, geometry layer, fullscreen pass, or additional
shadow map lookup was introduced. Tree and ordinary ground shader sample
sets stay unchanged. Red shrub now uses the existing foliage texture set
rather than basic Lit, so it has additional actual shading work; distant
canopy math is bounded. The shared ground graph keeps material instancing
and the existing SRP batcher arrangement. Native SpeedTree continues its
own instanced path. These are architecture and source checks, not a measured
mobile GPU benchmark. Unity validation used OpenGLCore on the Codespace;
no measured Adreno 610 frame time, real-device thermals, or Android player
shader-build/FPS guarantee is asserted.

Public technique references: NVIDIA GPU Gems 3 chapters 4 and 16 (SpeedTree
and procedural vegetation), Unity 6 draw-call/terrain detail guidance, and
Arm mobile art/material guidance. This is an original implementation, not
a claim to possess Rockstar's proprietary foliage code.

## Continuation: Conservative Red Bush contact broad-phase

A live Unity 6000.3.26f1 AssetDatabase probe (temporary editor script removed
afterwards) inspected all four meshes sharing the Red Bush material:

| Mesh | Vertex count | Center XYZ | Half-extents XYZ |
| --- | ---: | --- | --- |
| Painted Bush_Red Mesh | 8,887 | (0, 1.017657, 0) | (2.201980, 1.114524, 1.784371) |
| Standalone Red_Bush LOD0 | 8,887 | (-0.024445, 1.017657, -0.627658) | (2.201980, 1.114524, 1.784371) |
| Standalone Red_Bush LOD1 | 2,636 | (-0.035351, 1.014833, -0.627658) | (2.218102, 1.122842, 1.784370) |
| Standalone Red_Bush LOD2 | 1,697 | (-0.035351, 1.014833, -0.596702) | (2.218102, 1.122842, 1.815327) |

All authored rest vertices are conservatively enclosed by absolute object
coordinates (2.30, 2.20, 2.50), including LOD centers and small safety
margins. The vertex-only interaction HLSL uses object-to-world matrix rows
to project this bound into world XZ for arbitrary rotation/tilt/nonuniform
scale. The interactor radius is recovered from the already supplied inverse
radius squared. Only the authored 2.13m red-bush material profile uses this
broad-phase; all other ground details retain their original behavior. The
branch skips the per-leaf world-position transform only when even the nearest
possible card cannot be contacted. It makes no changes to regular wind,
geometry, alpha clipping, lighting, depth/shadow passes, or material bindings.

The 13th source/math test group checks 10,000 seeded random affine transforms
and reachable interactor positions across all four measured mesh envelopes.
All 13 groups, three tree-shadow tests and ten tree-proportion tests passed.
The live Unity native-project batch import exited 0 with zero reported C# or
shader errors. This is source/Editor verification, not an Android GLES3x variant
exhaustiveness guarantee or a measured Adreno 610 FPS/thermal improvement.

## Continuation: sqrt-free shrub interaction broad-phase

A final vertex-hot-path audit found two mathematically redundant operations in
the terrain-detail interaction path. The per-instance Terrain detail pivot is
now read directly from the object-matrix translation column instead of running
a generic object-to-world transform on the zero vertex. The measured Red Bush
broad-phase now computes squared distance from the interactor to the closest
point of its conservative projected XZ AABB. This replaces the previous
radius-expanded box plus reciprocal-square-root recovery with abs/max/dot/
multiply math. It is a tighter conservative reject: any reachable leaf remains
accepted, while corner-only positions outside the circular contact envelope can
skip the later per-leaf world transform earlier.

The change adds no texture sample, pass, varying, material variant, collider,
physics query, runtime allocation or draw call. The regression model continues
to stress all four measured Red Bush mesh envelopes under arbitrary rotation,
tilt, mirroring and non-uniform scale, and additionally proves that 10,000
reachable randomized contacts cannot be culled by the squared circle-vs-AABB
test.

Post-change verification used Unity 6000.3.26f1 in disposable projects under
/tmp. The native source-only project imported 104 game scripts, executed its
gate method and reported zero C# compiler errors. A separate shader-data gate
compiled 10 TerrainGrass and 28 AarisSpeedTree8Mobile GLES3x variants for the
Android build target: 38 requests, zero failed variants, zero shader compiler
errors and zero C# errors. The installed Editor does not include the Android
playback module, so this is a direct GLES3x shader-compiler gate rather than an
APK build or measured Snapdragon/Adreno device benchmark.


## 2026-11-26 neutral-profile fragment fast path

The dense ordinary-grass draw uses the neutral canopy profile (`Profile.x == 0`),
so its old shrub color-variation expression always evaluated to zero. The
ground-detail fragment helper now uses one material-uniform `UNITY_BRANCH` to
skip that shrub-only chroma ALU for the neutral profile. Positive shrub
profiles execute the exact previous expression, including the same seed,
saturation and RGB coefficients; non-positive profiles remain algebraically
identical to the old zero-variation result. This adds no shader keyword,
material, texture sample, shadow lookup, render pass, physics query or C# update.

Validation on the connected Unity 6000.3.26f1 Codespace used only disposable
`/tmp` projects/overlays and restored the live target files byte-for-byte.
The foliage contract now has 14 source/math groups and passed together with the
3 native mobile-shadow tests and 10 tree/proportion tests. The native C# source
sandbox compiled 104 game scripts with Editor exit 0 and zero C# errors. The
GLES3x/Android shader-data gate compiled 10 selected TerrainGrass requests and
28 selected AarisSpeedTree8Mobile requests: 38 total, zero failed variants and
zero shader compiler errors. This is compiler/source evidence, not a measured
Snapdragon 680 frame-time, thermal, battery or Android-player benchmark.


## 2026-11-26 view-aware transmission + zero-wave torsion

- SpeedTree leaf transmission reuses the existing view direction so the strongest golden-green fake translucency appears when the sun is truly behind a leaf from the camera. This adds one dot product and a few half-precision ALU only; no texture, varying, pass, material, shadow lookup, or draw call.
- Fast leaf wind keeps its single smoothed triangle wave. A tiny signed torsion is derived from the same bend angle, with opposite sign for the two authored leaf groups; no new trig, noise sample, physics, collider, or per-leaf C# update.
- Existing distance exits, instancing, root AO/ground blend, world-position macro colour, bark normal relief, and hemispherical sky-bounce paths remain intact.

Validation for this continuation on the live Codespace: `git diff --check` passed; the foliage contract reported all 15 source/math groups passing, alongside 3 mobile-shadow tests and 10 tree/proportion tests. The disposable Unity 6000.3.26f1 GLES3x gate compiled 10 TerrainGrass plus 28 SpeedTree requests (38 total) with zero failed variants and zero shader errors. A separate disposable `/tmp` C# project completed Unity batchmode successfully with no `error CS...` diagnostics. This is compiler/source evidence, not a measured Adreno 610 FPS, thermal or battery benchmark.
