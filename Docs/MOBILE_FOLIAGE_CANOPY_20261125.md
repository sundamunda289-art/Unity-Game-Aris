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
- ShadowCaster, DepthOnly and SceneSelection now pack only UV + vertex alpha
  into one half3 varying and no longer calculate/interpolate an unused camera
  direction. DepthNormals uses the same UV/alpha packing and carries only the
  normal/tangent basis consumed by its fragment stage. ForwardLit, alpha clip,
  wind deformation, shadow bias and depth-normal output are unchanged; this is
  a vertex/interpolator cost reduction with no intended visual tradeoff.

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
