# Premium GT true road-size and full-fleet visual QA

18 November 2026. Measurements in real Unity world metres.

## Corrected model fit

The shipped vehicle catalogue sets GT maximum width 2.08 m, length
4.48 m. The imported CC0 shell measures roughly 2.486 m wide,
1.189 m high and 4.631 m long. Scaling uniformly to its width cap
shrinks the GT to approximately 3.87 m long and 0.995 m tall,
despite its actual long-coupe shape.

For ONLY the verified embedded-wheel GT, scale X to target width,
and scale Y and Z equally to target length. This preserves the YZ
rolling-plane circular tyre cross sections while every other car
keeps the original isotropic per-class scaling.

### Native Unity verified result
- Width 2.080 m, height 1.150 m, length 4.480 m
- Four real linked WheelColliders; mass, RWD and suspension unchanged
- Native Editor: AARIS_ROAD_GT_METRIC_PASS, four-wheel-physics=PASS

The already-merged source-axis detection, wheel-bottom grounding,
real-metre collision and corner probes, long pickup collision shell,
paint safety and asset fallbacks remain unchanged.

## All eight native catalogue-based car photos

The Unity studio uses the actual vehicle_catalog.json from Resources
instead of stale separate studio dimensions. It measures source FBX
importer axes, shows eight current cars on a seven-metre roadway and
correctly adds the actual Kenney wheel mesh + UV atlas to sedan views.

See Docs/VehicleVisualQA/2026-11-18-eight-cars-actual-catalog-scale.jpg.

Native Editor: AARIS_VEHICLE_STUDIO_PASS 8 genuine Unity photos.

This does NOT prove Android FPS, in-game car handling, live runtime
paint variants or AAA photorealism; source art still needs polish.
