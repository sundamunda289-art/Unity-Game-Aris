# Premium eight-colour automotive palette

Date: 18 November 2026

## Problem found
The body paint collection used eight hash-selected shades, but four
dark grey, blue, green and teal options visually converged under flat
roadside daylight. The smallest **normalised RGB** separation between
two paint shades was 0.0906, for Graphite and Petrol Teal.

## Correction
Retain five refined neutrals and original deep burgundy. Distinguish
three formerly overlapping shades with more natural automotive hues:

- Midnight sapphire blue: (0.12, 0.20, 0.47)
- Refined olive-bronze: (0.49, 0.48, 0.25)
- Deep petrol teal: (0.11, 0.48, 0.53)

The minimum pairwise normalised RGB separation among all eight shades
is now **0.2534**, a 2.80x separation compared with the baseline.

Only body paint swatches changed. No new texture, shader, mesh,
rendering material, runtime allocation, draw call, or HDR effect.
The existing deterministic FNV paint index remains unchanged.
Production URP PBR clearcoat class tuning, road-facing wheel rubber,
independent tinted glass, and head/taillight material slots remain.

## Verification
- Four source/geometry and stable paint checks in
  Tools/Validation/test_vehicle_premium_paint_separation.py
- Editor native capture tool AarisPremiumGtPaintQA captures eight variants
  using the *actual production* PaintVerifiedBodySlots and StablePaintVariant
  methods through temporary editor reflection; never saves a modified prefab.

The RGB spacing is a measurable source-code property, not a guaranteed
perceptual contrast ratio under all game lighting. Native Unity output,
runtime colours and Android screenshots must be examined separately.
