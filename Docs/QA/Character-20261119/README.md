# Standing, crouch and prone: native Unity multi-angle QA (19 November 2026)

**Evidence, not a final perfect-pose claim.** Produced by the real Unity Editor 6000.3.26f1 in Play Mode, with `AarisPoseFrames.CaptureMultiAnglePortrait` and a neutral QA-only camera/floor.

- **Capture origin**: existing Codespace `aaris-ai-ubuntu-minimal-p764pwx4q79536p95`; working branch `perf/android-global-async-io-gate-20261119`, HEAD `e56bccd`. This is **pre-PR #736 knee-pole fix**, and not an Android-device render.
- **Screenshots**: 12 real 640x720 JPEGs from 0°, 90°, 180°, 270° views of standing, crouching, prone. The attached 4x3 sheet rescales these frames for review.
- **Capture result**: `SUCCESS_16_ACTOR_MULTIANGLE_PORTRAITS` including four extra walk/sprint/jump frames (not shown in sheet).
- **Ground**: standing root/capsule gap +0.035m, left/right standing ankle +0.075m above terrain; crouch ankles +0.126m; prone ankles +0.087m / +0.091m. Ankles sit above terrain because bone pivots are not boot sole geometry.
- **Crouch**: ankle lateral separation 0.343m, knee lateral offsets +0.014m and -0.004m, left/right knee height difference 0.041m.
- **Prone**: ankle lateral separation 0.495m (current QA guard ≤0.62m). Hip bone 0.119m and head bone 0.329m above terrain.
- **Important caveat**: `SkinnedMeshRenderer.bounds` under crouch and prone exaggerate below-ground geometry; **do not interpret these alone as visible clipping**. Compare the pictured soles, actual bones and fitted mesh.
- **Status**: baseline multi-angle snapshots captured; review under different terrain slopes and transitions and capture again after applying PR #736, before final signoff.

Only neutral game-character screenshots are included; no personal, account, terminal or Unity credential UI.
