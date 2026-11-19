# Android performance + Unity 6 anatomical IK integration — 2026-11-19

## Exact source and purpose

This **draft** is the coexistence branch for:
- [PR #734](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/734): Android low-memory native import admission, staged weapons/vehicles/medical catalog, optional Foley, staged essential rig frame and one-shot diagnostics; pinned reviewed source: `6c20e6c`.
- [PR #736](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/736): anatomical upright knee basis and bounded prone/crouch transition foot-splay, native Editor visual QA; pinned reviewed source: `809c574`, based on `427083c`.

The implementation was built from the #734 source with **13 exact pose C# diff hunks**, **4 Editor camera QA hunks** and **3 pose test hunks** from #736. Applying those hunks against #736's base reproduced the corresponding #736 files byte-for-byte. The same exact-context patches then applied to #734 without conflicts.

This is deliberately a **new branch/PR**, not a reset or merge of the active Codespace worktree. Keep the original PRs available for comparison. For a release avoid merging all three independently, which would duplicate commits and risk conflicts; decide on one unified merge path during review.

## Critical Unity Editor compilation guard

The #734 Android loot manifest warmup method `AarisLootDocument.LoadForAndroidAsync` is defined only for `UNITY_ANDROID && !UNITY_EDITOR`. The caller coroutine `AarisWeaponLootWorld.InitializeAndroid` originally remained visible in Editor C# compilation. The integration starts from #734 after the caller received the matching conditional guard. The source contract `test_android_shared_loot_manifest_async.py` now rejects a regression that leaks this method into Editor compiles.

This is a **source-grounded compile fix**, not a claim that the complete Unity Editor project successfully compiled. Unity native import/build was intentionally not run in this integration pass.

## Gameplay and quality invariants

- `AarisCharacterVisual.LoadCharacterAsynchronously` still stages the **full-resolution** authored rig and the original multi-clip FBX animations, then reserves one 4GB-class Android frame against *new optional* imports/hierarchy builds before instantiating the essential humanoid.
- The same class retains `AnatomicalKneeForward`, symmetric per-shoe transition splay correction capped at **0.16m**, and the entry-prone knee bend-circle guard with original floor/leg-length constraints. Standing and prone keyframed source clips, motion/collision authority and existing stance-foot support rays are unchanged.
- Original `PlayableGraph`, pose evaluation and `SkinnedMeshRenderer.BakeMesh` validation are retained. Android JSON reports provide the one-off rig instantiation, graph preparation and BakeMesh durations.
- No direct changes to `ProjectSettings`, original terrain/trees, URP pipeline graphics, model art, or texture quality relative to `427083c`.

## Source test outcomes

Checked a **standalone source snapshot** produced from the immutable integration branch under `/tmp/aaris-pr734-pr736-integrated-20261119` in the Codespace. Its scripts and tests were read from Git objects; unchanged art was referenced separately. The active working files were not modified:

| Suite | Result |
| --- | ---: |
| `test_android_*.py` including 6 new cross-PR coexistence guards | **254 PASS** |
| `test_settlement_expansion.py` | **13 PASS** |
| `test_open_combat_houses.py` | **7 PASS** |
| `test_vehicle_*.py` | **153 PASS** |
| `test_roadside_bike_visuals.py` | **10 PASS** |
| `test_character_pose_and_curved_ice.py` | **PASS** |
| `check_weapon_loot.py` | **PASS** |
| `git diff --check 427083c..integration` | **PASS** |

These suites overlap; totals are per suite. They are source/simulation/invariant checks and are **not** Android FPS measurements, Unity C# compilation or fresh camera screenshots.

## Latest main IK reconciliation before final merge

On 19 November the latest main branch advanced with further native-reviewed
pose tuning. This integration PR adopts those **current main** guard values
instead of reverting them to the previous PR #736 values:

- Stationary transition foot splay begins at **0.50 m** and is trimmed
  symmetrically by at most **0.16 m per foot**.
- Entry-prone knee bend-circle turn limit is **55°**, with an anatomical
  lateral allowance of **0.26 m**.
- Existing tests for these exact values and the independent one-frame Android
  essential rig admission are retained together.
- Obsolete CI source-pattern checks were amended to follow the authored
  shared model gate and separate Android JSON decode paths.

These are source-level merge conflict resolutions. They are **not** new live
Unity pose screenshots, Android FPS tests or evidence of pixel-perfect IK.

## Native acceptance still required before merging

- Compile the exact **combined** commit in Unity 6, after the user's compile/license restrictions are resolved; address every Editor C# issue before trying Android packaging.
- Capture the same camera-angle and intermediate-time standing, crouch and prone poses as #736, including uneven roads/terrain, and inspect knees, ankles, boots and ground contact. #736's native screenshots measured the **pose-only** branch, not this combined build.
- Profile actual Snapdragon 680/Adreno 610 Android hardware at startup, dense settlement movement, aircraft/parachute, weapon pickup, vehicles and long thermal runs. Check p95/p99 stalls, native loading, character rig stages, shader compilation and Android ANR/LMK/crash evidence.
- Keep the active Codespace's independently changing local .meta/material/URP/lockfile edits intact. Do not cherry-pick or overwrite local dirty files just to run tests.

**Status: combined and source-regression verified, not build/visual/device certified.**
