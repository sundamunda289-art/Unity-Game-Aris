# Unity 6 native pose after-fix QA — 2026-11-19

## Real Play Mode evidence
Unity Editor **6000.3.26f1** on the existing Codespace `aaris-ai-ubuntu-minimal-p764pwx4q79536p95`. A dedicated Xvfb OpenGL run executed the project's own `AarisPoseFrames.CaptureTransitionPortrait` and `CaptureMultiAnglePortrait` on the exact reviewed pose C# revision `5eba8e4f2c40cac13fc4c55b9493dd7c55c9ae8d`.

Both suites completed with real renderer readbacks and a total of 32 frames:
- `SUCCESS_16_STANCE_TRANSITION_FRAMES`
- `SUCCESS_16_ACTOR_MULTIANGLE_PORTRAITS`
- `FINAL_SOURCE_REGRESSION_PASS`

The two attached contact sheets show (a) standing/crouch/prone from four directions and (b) four time-samples of crouch, crouch→prone and prone→standing. Actual Unity GameObject skin, IK, capsule and authored animations were used. Contact-sheet collages resize the 640×720 original Unity photographs; they are not AI-generated. The real terrain is used by motion/IK, whereas the portrait camera uses a collider-free neutral floor to keep character visibility consistent.

## Matched before/after native metrics (metres)

| Frame | Baseline ankle spread | Final patched ankle spread | Relative result |
|:--|--:|--:|:--|
| Crouch→Prone at Prone frame 1 | 0.702 | 0.591 | -0.111 |
| Crouch→Prone at Prone frame 2 | 0.646 | 0.513 | -0.133 |
| Prone→Standing at Standing frame 0 | 0.769 | 0.638 | -0.131 |
| Prone→Standing at Standing frame 1 | 0.744 | 0.734 | -0.010 |
| Prone→Standing at Standing frame 2 | 0.716 | 0.714 | -0.002 |

The first intermediate prone frame still has large sideways knee deviation: left **-0.526m**, right **+0.366m**. The bounded ankle-spread correction improves the feet without entirely correcting this transitional knee artifact, so **do not call the characters perfect from every angle**. Return-to-standing's previous temporary knee-pole regression was avoided by **reverting** the early pole strength change; original settled prone knee pole remains untouched.

Grounded capsule terrain clearance was +0.035m in all three settled stances. Renderer bounds below terrain in crouch/prone are not by themselves proof of visible geometry penetration; actual pixel/vertex and bone evaluation is necessary. Final fixed multi-angle frames passed the current project's Crouch knee alignment/spread guards and Prone spread guard.

## Safety, limitations and repeatability
- The live Codespace was working on a **separate, concurrently advancing** performance branch; its active branch was not reset, rebased, stashed, or overwritten.
- To isolate the pose change, the reviewed `Tools/Validation/pose_temporary_after_capture.py` uses verified clean source blobs to temporarily overlay `AarisCharacterVisual.cs`. The other in-progress performance branch has an unrelated Editor compile failure: `AarisLootDocument.LoadForAndroidAsync` is defined only for Android while its calling coroutine was not; the QA helper temporarily guards that coroutine out of Editor compiles. **Neither temporary file change ships in this PR's gameplay source**. The helper validated both source files against their clean HEAD and restored both originals; terminal returned `QA_RESULT success=True originals_restored=True`, and subsequent `git status --porcelain --` for both files was empty.
- These are neutral-floor, flat-ground Editor renders; natural motion on uneven terrain, turns, stairs, prone collisions and actual Android builds remain **unverified**. Don't merge as fully visually certified unless those pass.
- Native screenshot evidence differs from a phone/device test and makes no FPS claim.

For past baseline contact sheets see `prepatch-standing-crouch-prone-12-angle.jpg` and `prepatch-12-transition-frames.jpg`. For current reviewed source see `Assets/AarisWorld/Scripts/AarisCharacterVisual.cs` on this PR.
