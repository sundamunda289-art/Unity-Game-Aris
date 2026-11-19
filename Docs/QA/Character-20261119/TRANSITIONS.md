# Native transition screenshots and measured regression (2026-11-19)

**Pre-fix evidence.** Unity 6000.3.26f1 actual Play Mode 16-frame transition run (not synthetic illustration), existing Codespace branch `perf/android-global-async-io-gate-20261119`, before PR #736's transition splay correction.

- `02_Crouch_f0..f3`: entering crouch, four time-sampled side portraits.
- `03_Prone_f0..f3`: crouch-to-prone blend at approximately 0.10s, 0.28s, 0.46s and 0.64s sampling offsets.
- `04_Walk_f0..f3`: returning from prone to standing, same side view. Filename retained from the existing QA harness; these four frames are **standing transition**, not proof of a walking gait.
- Real Editor marker: `SUCCESS_16_STANCE_TRANSITION_FRAMES` (12 transition frames + 4 others).
- Native logged ankle spread: crouch settled 0.342–0.344m; entering prone reaches **0.702m** at Prone frame 1; return to standing reaches **0.769m** at frame 0.
- Native lateral knee offsets: Prone frame 1 **left -0.527m, right +0.374m**; Standing transition frame 0 left -0.520m, right +0.397m.
- `TRANSITION_CONTACT` reports both low-stance world foot locks released in every sampled **blending** frame.
- Native ankle-to-terrain minimum clearance during transitions ranges from **-0.045m to +0.129m**; beware ankle pivot is not boot outsole.
- Evidence demonstrates *transient frog-splay*, not an already-fixed defect. Follow-up source in PR #736 now uses a bounded symmetric inward foot shift and continuous sagittal knee-pole support; do not declare visual PASS without a new patched run.

The photographs contain only the 3D game character and a neutral QA floor.
