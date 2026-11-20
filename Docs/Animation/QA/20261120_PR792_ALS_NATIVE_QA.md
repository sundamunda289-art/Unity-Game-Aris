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
