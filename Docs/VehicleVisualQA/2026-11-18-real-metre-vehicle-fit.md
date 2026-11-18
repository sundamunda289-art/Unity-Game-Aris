# Map-relative vehicle proportion and grounded wheel pass — 18 November 2026

This pass replaces the one-size-fits-all 1.88 m x 4.28 m vehicle scaling envelope
with eight vehicle-class *maximum visual footprints*. Source art is always scaled
uniformly; no axis-specific stretch or changed asset textures/meshes.

| Type | Maximum width | Maximum length |
|---|---:|---:|
| Sedan | 1.84 m | 4.42 m |
| SUV | 1.96 m | 4.50 m |
| Sports | 1.91 m | 4.40 m |
| GT | 1.92 m | 4.42 m |
| Hatchback | 1.74 m | 3.95 m |
| Van | 1.97 m | 4.57 m |
| Utility | 1.96 m | 4.57 m |
| Buggy | 1.80 m | 3.72 m |

The existing 1.78 m player and 2.28 x 4.70 m roadside collision reservation
anchor the envelope. The actual model can be smaller in one dimension to
preserve its original silhouette; the numbers above are caps, not forced
distorting dimensions. Older catalogues without these fields preserve
the previous 1.88 x 4.28 m fallback.

### Contact, alignment, and rendering
- Once scaled, embedded front/rear wheel mesh bottoms are measured in
  the chassis coordinate frame and an import-origin-independent vertical
  offset aligns their average to 10 cm above terrain at spawn. No altered
  Rigidbody velocities, no wheel mesh stretching, no per-frame correction.
- The real fitted bounds (including the shifted vertical centre) are now
  passed to the chassis BoxCollider, lamps, and exhaust; a shell is not placed
  high above the actual imported wheel geometry.
- Reject strange wheel diameters, differing ground planes and huge adjustments
  rather than damaging an unfamiliar FBX. Preserve the verified fallback chain.
- Premium automotive colours are muted ceramic/metallic/blue/green/red tones.
  GT and sports finish use bounded clear-coat; opaque-window materials named
  `_glass` now share the protected URP glass finish.
- Preserve the existing 10 streamed cars, 300 ms polling, shared material
  cache, WheelCollider ownership, physics budget and no per-frame allocations.

### Evidence and limits
These are source-level and deterministic geometry contracts only. Do NOT
claim photorealistic quality, wheel/road contact, Android FPS, or Unity Play
Mode until a native scene camera and physical drive have been tested. The
model's import orientation and source unit scale are verified by Unity at
runtime, with guard/fallback behaviour for malformed source geometry.
