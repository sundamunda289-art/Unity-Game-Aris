# Android Gloo Wall preview first-frame deferral — 2026-11-19

Target: Unity 6000.3 URP, Snapdragon 680 / Adreno 610 and 4 GB Android.

## Source finding
`AarisOfflineGlooWall.Start()` synchronously constructed the preview, invoked
`AarisGlooWallVisuals.Create`, imported a bundled authored model using
`Resources.Load<GameObject>`, constructed a full visual hierarchy and
performed renderer/material work in the same opening scene phase.
The preview is only needed when aiming a wall. A main-thread startup risk is
verified in source; a 3–30-second freeze from this source is NOT verified.

## Change
- On **Android player builds**, the original preview initialization moves
  behind one rendered frame and **sequential low-priority** async asset
  prewarm. The original ice prefab is requested first, then the ArcticBlue
  and FrostHighlight material assets. Only the existing original assets
  are used.
- If the player begins aiming before asynchronous loading completes,
  `BeginAim()` calls `EnsureGhostCreated()` synchronously to preserve
  immediate red/blue preview and unchanged player control. The deferred
  coroutine checks whether the preview already exists before creating it:
  no duplicate preview hierarchies.
- The authored prefab and two finishes are reused through a
  session-scoped cache. Ghost preview no longer synchronously loads the
  two deployed-only finish materials. Desktop and Editor still construct
  their preview immediately in `Start()`.
- Gloo Wall collision BoxCollider, damage, timeout, preview tinting,
  hologram transparency, source model, material colors, support probes,
  map, terrain, URP settings and resolution are unchanged.
- The patch avoids one heavy scene-start path. Asset integration and ghost
  GameObject instantiation still have **main-thread cost** later.
  Missing assets retain the original fallback geometry.

## Verification
```sh
python3 Tools/Validation/test_android_gloo_preview_warmup.py
```
Run existing HUD/weapon/mobile regressions and GitHub CI. Native Unity compile
and real-device tests must confirm correct first-look render, wall alignment,
collision, no missing preview on an early WALL press and stable FPS/Android PSS.
No blanket claims that freezes, RAM kills, ANRs or GPU driver resets are fixed.
