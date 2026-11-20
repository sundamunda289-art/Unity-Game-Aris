# Android settlement relocation frame slicing — 2026-11-20

## Identified source-level frame spike
The runtime settlement placement coroutine on the 2 km map already budgets one
attempted site per Android frame and time-slices native tree enumeration.
However, a **blocked interactive house** calls
`TryRelocateInteractiveSite` synchronously. That fallback can test up to
**12 positions**; each tries terrain foundation height/slope samples and
tree/collider clearance in a single rendered frame. This work bypasses the
per-site frame budget and can coincide with optional vehicle/terrain work.

## Minimal implementation
- Keep the **identical ordered 12 candidate coordinates** and all original
  ground, slope, foliage and physical-obstacle predicates.
- Use an Android player-only iterator: at most **one fallback candidate** per
  rendered frame; unsuccessful candidates yield before testing the next.
- Retain the synchronous Editor and non-Android path.
- Reuse one static offset table rather than allocating the same vector array
  for each blocked house.
- Preserve relocation results, deterministic log, site placement, and cleanly
  restore original coordinates on failed or cancelled searches.
- Do not modify original TerrainData, trees, prefabs, materials, LOD, map
  clarity, vehicle counts, or player collision geometry.

## Validation and limitations
`python3 Tools/Validation/test_android_settlement_relocation_frame_slice.py`
performs source contract assertions. This is **not** a Unity Editor compile,
real GPU measurement, Android startup trace, or proof of better FPS.

Run Unity Editor compile and Android IL2CPP build, then compare frame-time p95/p99,
stalled frames, startup duration, memory peak, and all planned interactive
house coordinates before/after. The maximum search now takes extra **rendered
frames**, which can slightly delay a blocked interactive house's appearance
but prevents all 12 native probe rounds from stacking in one frame.

Unity background integration documentation:
https://docs.unity3d.com/ScriptReference/Application-backgroundLoadingPriority.html
