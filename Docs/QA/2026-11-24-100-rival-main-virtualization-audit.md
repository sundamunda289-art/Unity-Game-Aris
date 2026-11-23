# 100-rival mobile virtualization: independent main-branch audit (2026-11-24)

## Source and scope

- Authoritative repository: `sundamunda289-art/Unity-Game-Aris`.
- Source under test: `main` commit `247ca00dd714026ce768ce321c99ff03eb624d5a` (includes AI virtualization PRs #1062–#1065).
- Unity Editor: 6000.3.26f1, Linux / Xvfb, source-only sandbox.
- No production C# file, scene, prefab, material, mobile-quality setting or bot count is modified by this audit. It would be incorrect to claim a new Android FPS improvement from this documentation-only change.

## Existing shipping architecture verified in source

`Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs` is an offline-only match authority with exactly 100 logical slots, spatial-hash target discovery and a maximum of two simultaneous player attackers. It already splits rendering, physical receiver, and combat simulation:

| Player-relative state | GPU presentation / CPU behavior |
| --- | --- |
| Under 55 m | Local detailed humanoid, physical hits and normal combat. Off-camera detailed renderers are explicitly suppressed; animation graph stopped. |
| 55–155 m | Detailed rig only while camera relevant; hidden rig/graph disabled. |
| 155–485 m | Camera-relevant targets use one shared shadowless unskinned distant mesh. At 185 m+ movement and combat use the abstract path; physical receiver is admitted only for camera-visible shot reception. |
| 485 m+ | Neither detailed nor distant mesh is displayed. Logical position, safe-zone retreat, health and virtual opponent damage continue on the scheduled simulation clock. |

Additional guards: distant virtual ticks run on a 0.35 s cadence with Android slot phases; central dormant scheduling suppresses per-rendered-frame `MonoBehaviour.Update` dispatch, including camera-visible 185–485 m virtual actors; remote combat never applies synthetic player damage. Actual opponent HP, deaths and kill feed are maintained. `AnimatorCullingMode.CullCompletely` and `SkinnedMeshRenderer.updateWhenOffscreen=false` protect hidden humanoid work. Rigs and bullet receivers have bounded retirement/re-entry logic.

`AarisRivalGunfireVfx.cs` pools six close-combat tracer/flash slots and twelve independent casing slots with an idle-sleeping manager; distant virtual shots emit no presentation effects. `AarisDeathCrateWorld.cs` retains up to 100 logical drops, limits on-screen crate presentation to 16 pooled objects within 95 m and defers distant floor raycasts until near-player admission.

The distance bands are policies rather than proof that all occluded actors have zero GPU overhead; especially, a camera-visible 155–485 m shared LOD intentionally consumes some GPU. Far-away active AI has nonzero CPU cost because it must continue to simulate combat.

## New validation evidence (canonical main source, not an old feature branch)

The canonical main C# source and regression tests were exported into a disposable `/tmp` directory via `git archive`, without a new worktree or a checkout switch.

- `python3 -m unittest discover -s Tools/Validation -p 'test_offline_rival_*.py' -q`: **92 passed**.
- `python3 -m unittest discover -s Tools/Validation -p 'test_ai_distance_virtualization.py' -q`: **5 passed**.
- `python3 -m unittest discover -s Tools/Validation -p 'test_combat*.py' -q`: **68 passed** after restoring the authored `Assets/Resources/AarisWeapons/weapon_catalog.json` into the temporary source snapshot. The first attempt without that test fixture produced a FileNotFoundError; it was a missing snapshot input, not a failed gameplay assertion.
- Native `Tools/Codespaces/native_source_compile_sandbox.sh` using the audited main source as SOURCE inside `/tmp`: **102 C# game scripts; Unity Editor exit 0; compile method ran; zero C# errors; source_only_pass**. This is source-only compilation, not an Android APK build.

The three unittest command invocations have overlapping cases; the sum 165 should not be called 165 distinct tests. Source-only import omits unused VFX and a copied package lock **only in the disposable sandbox**, leaving authored production files unmodified.

## Workspace and evidence limits

An existing Codespace was inspected through the private terminal. Its active branch was a separate, older feature branch, with **40 modified/untracked entries** belonging to other agents. The canonical main was already present as its local `main` ref at audit start. The audit did not switch branches, reset, clean, stash, merge into or overwrite the active working tree, and the dirty count remained 40.

The 2026-11-23 QA document records an earlier real-map Editor soak with 100 rivals and rig re-entry, but this 2026-11-24 audit did not repeat full-scene PlayMode and does **not** establish Android frame timing, real-device GPU load, temperature, 1%-low FPS or game-wide zero allocations.

Before declaring the Snapdragon 680 / Adreno 610 4–6 GB target fixed, record a **Development Build on the real handset**: CPU Main/Render Thread, GPU frame ms, skinned mesh/rigger counts, physics time, GC/frame, p95/p99 frame time, first-launch versus warm-launch, throttling/temperature, 100-bot combat kills and camera-turn re-entry. Do not promise zero CPU for living remote fighters or compare multiplayer/server replication with standalone offline AI as if they were identical.

## Technical references

- Unity 6 Animator culling: https://docs.unity.com/en-us/engine/6000.0/script-reference/unityengine/animatorcullingmode
- Unity 6 Skinned Mesh Renderer: https://docs.unity.com/en-us/engine/6000.3/manual/assets-and-media/asset-types/mesh/components-reference/class-skinned-mesh-renderer
- Unity CullingGroup: https://docs.unity.cn/6000.1/Documentation/Manual/CullingGroupAPI.html
- Unreal Mass Gameplay simulation versus representation LOD: https://dev.epicgames.com/documentation/en-us/unreal-engine/overview-of-mass-gameplay-in-unreal-engine
- Unreal Fortnite Replication Graph (networked game, not an offline AI implementation): https://dev.epicgames.com/documentation/unreal-engine/replication-graph-in-unreal-engine
