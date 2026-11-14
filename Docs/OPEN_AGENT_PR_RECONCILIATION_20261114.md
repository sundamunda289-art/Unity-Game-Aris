# Open agent PR reconciliation — 14 November 2026

This is an **evidence snapshot**, not an automatic permission to close/merge 41 PRs. It exists to prevent future agents from repeatedly replaying superseded game patches.

## Canonical reference

- Canonical repository: `sundamunda289-art/Unity-Game-Aris`, branch `main`; known reviewed commit on creation: `4a0a9c4be1dfa1ae6c2a120953eaa4b139030dd9` (includes [#518](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/518)).
- As audited on 2026-11-14: **41 open PRs, 33 draft, 8 ready**. Most old PRs conflict with present main. This count will change; always refresh GitHub state.
- Old Draft [#454](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/454) contained 1,502 authored loot candidates versus the then-main 1,574. The remaining three 50m sample-grid gaps were selectively integrated via merged PR #518. New 1,577-candidate file preserves all previous candidate indices.
- Current Codespace checked: `aaris-ai-ubuntu-minimal-4qrrxr57r79jf79xj`, local `main` HEAD `a4587eda17bc`, **745 dirty files** at audit (731 modified, 14 untracked). Local checkout was 24 commits behind the pre-#518 main and is older still after #518. **DO NOT** push its full tree, reset, clean, force-push, or silently overwrite existing agents' work.
- A byte-identical blob SHA proves that specific file version exists in main. Different blob SHAs do **not** prove that an old fix is absent; a later fix may supersede it. Missing paths do **not** automatically justify import.

## Individual open-PR disposition

| PR | Initial classification | Evidence / action gate |
|---|---|---|
| [#513](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/513) | Overlap: #511 | Two-slot holster, fists, swap already on newer main; don't roll back |
| [#512](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/512) | Overlap: #514 | Interactive door spatial audio already integrated later |
| [#454](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/454) | Selective #518 merged | Old 1,502-candidate rewrite not applied; 3 native-valid grid holes added to 1,577-slot main |
| [#453](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/453) | Superseded coverage | Old 765 candidates had 174 uncovered 50m lattice points; newer 1,577 has zero planned holes |
| [#445](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/445) | Native scene gate | 47-house open-entry scene rewrite differs; verify colliders, interactive doors, LFS and 2GB FPS |
| [#391](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/391) | Partial overlap | 520/580 changed paths byte-identical on pre-#518 main; 60 differ; DO NOT bulk-merge scene/LFS |
| [#334](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/334) | New assets, blocked | 16 paths absent on pre-#518 main; Kenney door-leaf-free kit needs Editor import + decision on interactive doors |
| [#307](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/307) | Overlap/newer runtime | Core doors, loot, grenade, vehicle, gloo code since evolved; compare hunks individually |
| [#281](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/281) | Model superseded | Same 1,024 vertices/1,796 faces; main plane adds 1,220 normals; avoid old geometry rewrite |
| [#261](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/261) | Overlap | Main nearby-collider polls avoid global SyncTransforms unless explicitly requested |
| [#258](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/258) | Partial overlap | Shader already included; later HUD/animation/streaming logic supersedes older branch |
| [#241](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/241) | Ops review | Survey selector change must be reconciled with current Turbo machine registry |
| [#239](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/239) | Ops review | 13 snapshot workflow/scripts absent; avoid duplicating secure Turbo bridge without security review |
| [#238](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/238) | No present shape benefit | Live scene streamer has 95 mesh + 200 box refs; no sphere/capsule refs observed |
| [#237](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/237) | Native bake blocked | 21 new paths absent; native TerrainData bake and performance evidence required |
| [#235](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/235) | Overlap/newer code | Fatal health state before callbacks and immediate grenade collider disable exist on main |
| [#234](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/234) | Unsafe historical terrain note | Older branch assumed terrain SampleHeight world Y; newer main applies verified tile-origin correction |
| [#226](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/226) | Partial overlap | 79/104 changed paths identical pre-#518; newer audio clips/director diverge |
| [#211](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/211) | Newer settlement fixes | World expansion, red-bush shader and control scripts have later main changes |
| [#184](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/184) | Shader integrated | Bush_Red material exact-match in main; old test differs |
| [#165](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/165) | Ops overlap | Camera-frame collection paths evolved in newer Codespace infrastructure |
| [#148](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/148) | Ops overlap | Editor import diagnostics evolved; avoid old tool overwrite |
| [#86](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/86) | Ops review | AI hands/eyes diagnostic branch differs from current bridge |
| [#85](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/85) | Ops review | AI scene/diagnostic branch differs from current bridge |
| [#84](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/84) | Ops review | AI geometry bridge branch differs from current bridge |
| [#82](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/82) | Ops review | 2 old standalone AI tooling paths missing; examine newer bridge before importing |
| [#81](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/81) | Ops overlap | Legacy live Unity bridge differs from current owner-only control |
| [#80](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/80) | Ops overlap | Legacy live Unity bridge differs from current owner-only control |
| [#79](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/79) | Ops review | 3 legacy AI control paths missing; security and feature overlap review required |
| [#78](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/78) | Ops review | 4 old workbench paths missing; do not duplicate later AI bridge |
| [#75](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/75) | Native scene gate | 47-home scaling/route foundation scene rewrite conflicts with current saved-scene lineage |
| [#63](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/63) | Native tree gate | 6 old tree taper/importer paths missing; run native visual/perf proof |
| [#36](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/36) | Native tree gate | SpeedTree shadow changes could reduce visuals; user requires unchanged quality |
| [#35](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/35) | Overlap | Player terrain idle optimization differs; current movement/hotpath code later improved |
| [#34](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/34) | Ops superseded | Old studio wake/worker workflow likely superseded by secure Turbo bridge |
| [#33](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/33) | Hardware profile review | Old 4GB adaptive budget not equivalent to 2GB phone requirement |
| [#32](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/32) | Native LOD gate | Civic prefab/scene LOD changes need real visual and 2GB Android checks |
| [#30](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/30) | Native tree gate | Tree core bake proposal unexecuted; do not commit speculative mesh changes |
| [#24](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/24) | Hardware profile review | Early Cypress/low-memory settings diverge from current gameplay |
| [#20](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/20) | Hardware profile review | Old 2–3GB world budget differs; compare against latest visuals |
| [#17](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/17) | Hardware profile review | Old prefab/mobile geometry branch diverges from current approved assets |

## Proven integration in this reconciliation pass

PR [#518](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/518) was **merged to main** as `4a0a9c4be1dfa1ae6c2a120953eaa4b139030dd9`. Its three additive points were checked on real Unity 6000.3.26f1 via the existing private Codespace's `AarisWeaponLootWorld.TryResolve`: **3/3 PASS**, Unity exit code 0, no matched C# compiler errors. A fresh gun-door QA on that older Codespace returned PASS. GitHub Map explorer checks passed on the new PR, including authored loot source validation and gameplay/weapon transaction checks. This **does not** certify a new Android APK on the final commit or guaranteed loot visibility on every physical surface.

## Remaining native acceptance gates

1. **House scene variants (#445, #334, #75)**: reconcile desired interactive hinged doors with doorless/open-entry shells first. Import only verified CC0 assets; confirm all 47 residential replacements, walls, floor, roofs, stair/doorway traversal, nearby physics, source prefab/scene saves and Play Mode screenshots. Do not overwrite newer #505–#517 wall/door fixes.
2. **Forest and LOD (#237, #63, #36, #32, #30)**: verify true LFS asset hydration, Unity graphics, colliders, no tree-count increase, and 2GB Android FPS without visual degradation before TerrainData/prefab changes.
3. **Legacy multi-agent checkpoint (#391)**: inspect the 60 divergences (especially runtime scripts, 47-house scene, `.meta`, LFS terrain) by behavior; preserve 520 already-identical paths. Never bulk merge.
4. **Gameplay hot paths (#307, #235, #238, #261, #234)**: compare exact current method semantics. For #238, the examined live scene's streamer serialized 295 obstacles, all MeshCollider (95) or BoxCollider (200); its proposed Sphere/Capsule streaming changes currently have no observed direct target there.
5. **Ops automation (#239, #241, #34, #78–#86, #148, #165)**: current authenticated Cloudflare Turbo control is already functional; no extra insecure public tunnel, terminal API, or duplicate control plane should be merged.
6. **Audio, aircraft, HUD (#226, #281, #258)**: main has newer packaged audio and the aircraft mesh with identical vertex/face counts plus normals. Do not replace with stale assets.

Before every future merge: refresh main/PR state; compare semantics beyond changed-file counts; run static validations and relevant **native Unity** tests on the exact integration commit; preserve dirty LFS work; build and install a signed APK for final 2GB-device acceptance. Every merge must pin its expected PR head SHA.
