# PR #792 — ALS crouch native Unity evidence, 20 November 2026

**Exact tested runtime source:** `712db8e693e6085c0bae5e670afb2211d902a34c` (Unity **6000.3.26f1** on a fresh isolated Codespace project, full **2,650,092-byte Player.fbx** and correctly bound player/prefab scripts).

## Actual 16-angle native PASS

- Unity Editor batch exit code **0**, exact marker `SUCCESS_16_ACTOR_MULTIANGLE_PORTRAITS`; **16 genuine JPEG screenshots** captured: 4 angles each of Standing, Crouch, Prone, plus walking, sprinting and jumping stages.
- Crouch left/right knee-height delta **0.034 m** at first angle, **0.033 m** settled.
- Crouch boot spread **0.367 / 0.365 m**; knee lateral offsets within **0.005 m**.
- Settled Prone ankle-to-terrain: left/right **0.075/0.075 m**; knee-height difference **0.074 m**. All six pose stages accepted.
- Original CC0 selected crouch failed strict 16-view QA, knee-height delta **0.126–0.130 m**. Selecting the already included ALS idle/moving crouch pair while retaining CC0 fallback is the only production animation change in this A/B comparison.
- Latest branch had 4/4 GitHub CI workflows PASS; native test was on the exact code revision above.

## Actual 16-frame native transitions: marker PASS, quality warning

- Editor batch exit code **0**, marker `SUCCESS_16_STANCE_TRANSITION_FRAMES`; **16 JPEG frames** of time-sampled stance switches.
- Low-stance foot locks correctly released when blends are active; boot terrain gap on early prone frames **0.075–0.100 m**.
- **Unresolved:** Crouch-to-Prone early blended frame (`proneBlend=0.445`) produced **right-knee lateral 0.411 m**, left -0.036 m. At next frame both knees were approximately ±0.230 m; at settled prone they reduced to ~0.028 m each.
- Return-to-standing stance transition included lateral knee deviations up to approximately **0.277 m** and foot separation up to **0.618 m**. The QA currently *logs*, rather than rejects, excessive knee excursions in these crossfades. **Do not count success marker as AAA-quality transition approval.**

## Separate runtime/error caveat

One `ArgumentOutOfRangeException` in the Editor log comes from `UnityEditor.Search.SearchDatabase.EnumerateAll` during QuickSearch startup indexing, not the runtime animation classes. No C# compile errors were observed during accepted runs.

## Release gate

Keep PR #792 **Draft** and do not merge into main until transition knee flare, 30 actual-motor motion frames, and full source/visual regression are checked. Real Android GPU/CPU/FPS/thermal profiling is not implied by these Editor results. Native JPEG files were produced in the isolated Codespace test output directory but have **not yet been published to GitHub**: the Codespace's own git push credential was rejected with 403. The connected GitHub review/comment tools are still available; do not claim that a screenshot link exists until verified.

**Worktree safety:** owner main checkout and 30 modified entries remain untouched; no new git worktree was created for this snapshot.

## Actual 30-frame locomotion — screenshot marker PASS; bone audit unresolved

- Native Unity 6000.3.26f1: **30/30 frames** captured for Standing, Walking, Running, Sprinting, Crouching, CrouchWalking, Prone, Crawling, GettingUp and Jumping. Editor batch exit code **0**, marker `SUCCESS_30_MOTION_MATRIX_FRAMES`.
- **Important separate failure:** `Tools/Validation/audit_motion_matrix_bones.py` rejects the report: first Standing frames 0 and 1 both stamped `Time.unscaledTime=33.219`. Editor QA's `EditorApplication.update` can capture twice in the same actual game frame; the screenshot success marker does **not** certify 30 unique chronological bone samples.
- Exploratory inspection with **only the duplicate timestamp changed in-memory**, without overwriting native logs, identified four `knee_lateral > 0.30m` flags: Crouching frames 1/2 (max **0.348 m**), CrouchWalking frame 2 (**0.324 m**), and Jumping frame 2 (**0.343 m**, potentially authored airborne movement).
- Stationary Prone knee lateral max **0.028 m**; Crawling max **0.230 m**; GettingUp max **0.065 m**. The large early prone crossfade flare remains a distinct issue.
- A **separate isolated A/B candidate**, not yet committed, now tests a geometry/error-responsive existing two-bone-knee circle correction only on grounded Crouching/Prone plus an Editor `Time.frameCount` capture gate and explicit >0.30m grounded knee rejection. Results pending. **Do not merge #792 on the strength of image counts alone.**

## Isolated adaptive knee A/B: **rejected, do not merge**

A geometry-triggered stronger two-bone knee-circle correction was **tested locally only**, with additional Editor rejection for >0.30 m knee lateral excursion. Native Unity 6000.3.26f1 compiled and started, but the strict transition capture **FAILED** at frame 6, Unity batch **exit code 2**, reason `TRANSITION_KNEE_SIDEWAYS_OVER_30CM`: early Prone (`proneBlend=0.445`) still had **right knee side 0.411 m**. Stronger IK angular authority alone was not sufficient. **No production patch from this A/B was committed or merged.**

The QA frame-timestamp fix (`Time.frameCount` gate) was also prepared in this throwaway copy but had **not completed a separate native 30-frame validation**. Do not claim the timestamp bug fixed based on the source edit.

After this failed A/B run, Codespace Cloudflare heartbeat became stale and the same already-authorized Issue #77 Turbo-start recovery was triggered; transient `/tmp` evidence is at risk across restart. Retain this GitHub diagnosis until any new native run proves a better contact-preserving transition solution.
