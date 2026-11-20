# PR #792 — actual Unity native partial QA: FAIL (20 Nov 2026)

**Status: native visual QA is NOT passed. DO NOT MERGE.** Screenshots below are actual Unity 6000.3.26f1 Play Mode captures from the isolated QA project. These are not AI-generated examples or Python simulations.

- Canonical integration: [Draft PR #792](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/792).
- Original merged main baseline: `d8bd3d56ef71659e0da6165ed1228ef43db57347`.
- Unity project: isolated `/tmp/aaris-gpt6-final-792-20261120`; owner worktree (61 dirty entries) untouched.
- Scene `Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity`, real player rig + weapons, actual Play Mode, standard four camera orbit angles.
- C# Editor preflight PASS: active Player; correct resolved C# class; prefab binding; missing root scripts=0; 2,650,092-byte hydrated binary `Player.fbx`.
- Both captures stopped after **5 of required 16 frames**. Standing front/right/back/left were captured; Crouch front immediately exposed geometry errors. No native acceptance evidence for remaining crouch angles, prone, get-up or motion.

## A. Original combined PR (before bounded foot correction)
- Production visual C# blob: `472cb4471eef79008af7dddd55187ba0b9d5a5e7`.
- Unity Editor returned code 2, 5 JPEGs.
- `LOW_STANCE_FOOT_SPLAY` (horizontal boot separation >0.62m) at first crouch frame; failure prevents other 11 frames.
- Actual failed contact sheet: [5-frame original capture](20261120_pr792_original_5frame_FAIL.jpg).

## B. Bounded CC0 crouch-foot retarget candidate
- Production visual C# blob: `6480c42e11bc8996196c6835e7a790ce755a921b`. Now staged on this **Draft PR only**; the source still needs broader native QA.
- Clamp horizontal CC0 authored foot displacement to a per-foot pelvis-width-derived allowance, bounded between 3.5 and 11cm. Fade on crouch entry/prone exit. Preserve existing terrain rays, two-bone leg reach and C# allocation budget.
- Unity Editor returned code 2, 5 JPEGs.
- **Measured foot span: 0.438m**, now within 0.62m QA bound; lateral knee offsets: 0.000m / +0.018m.
- **Still FAIL:** knee-height difference **0.129m**, exceeding 0.120m QA threshold by 0.009m.
- Crouch screenshot still shows uneven foot support; skinned mesh minimum Y -3.463m compared with actor terrain Y -2.659m, so penetration/rig-ground alignment requires confirmation. Render bounds alone are not proof of mesh triangles penetrating.
- Actual captured candidate crouch frame: [bounded crouch front](20261120_pr792_bounded_crouch_f0_FAIL.jpg).

## Required next root-cause and gates
1. Record per-foot and per-knee absolute world Y and per-foot terrain support hit or miss, *before* first-frame fail, and compare early transition vs settled crouch. The existing low-stance foot ray starts only 0.70m above the sampled (possibly below-ground) ankle; a ray miss is a hypothesis, **not yet proven**.
2. Full 16 settled pose photos; 16 timed transition frames; 30 actual motor locomotion frames including crouch-walk, crawl, prone-to-stand; per-shoe contact, reach, knee-height/lateral symmetry, yaw and slope/stair/ledge checks.
3. Run GitHub CI on the exact production candidate; check Unity compilation and Android performance separately. Do not merge any version that fails real visual/anatomy gates or remove QA guardrails to manufacture a PASS.

**File evidence and labels are snapshots of failing tests, not a release certification.**
