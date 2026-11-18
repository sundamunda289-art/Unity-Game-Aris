# Premium closed-body Road GT — 18 November 2026

New GT vehicle entry uses an offline GLB model released by Innerscene under
CC0 1.0 Universal. Original source and exact sha256 are pinned in
Tools/MapBuild/convert_innerscene_road_gt.py; no runtime network access.

- Source: https://www.innerscene.com/tools/library/3d-parts/generic-mid-engine-supercar-73f252c5
- Verified GLB SHA-256:
  ad0fd94845edb9af474407fe08fb379b624c56ec4d4b4ad6b47822b65c6f8903
- Source 104,282 triangles, converted 67,598 total.
- Four separate animatable wheel pivots; existing WheelCollider physics,
  GT mass, RWD and driving, camera, sound and damage systems remain.
- Twelve 2048x2048 original flat-colour textures were converted into
  twelve shared URP Lit materials, without runtime texture atlas allocation.
- Quaternius GT is the immediate authored fallback; Kenney sports FBX is
  retained as the final drivable fallback.
- Three native Unity Editor views passed with 19 URP material slots.
- Limits: visual proof is not Android FPS or Play Mode drivability proof;
  wheel chrome merits further visual tuning.
