# Mobile world residency and visual-preservation audit — 2026-11-26

## Outcome and evidence status

This change hardens the existing streamed world and distance-based simulation, fixes a real foliage shader compilation failure, removes unnecessary Android memory-pressure queries, and corrects the allocation counter used to diagnose frame stalls. It preserves the authored world and its visual settings. It does not introduce a new whole-world terrain streamer or establish an Android FPS/RAM improvement.

- Canonical repository: `sundamunda289-art/Unity-Game-Aris`.
- Audited starting source: `ca7b838d03b8c780043b391df923e630a0c1c4d0`.
- Native verification environment: licensed Unity **6000.3.26f1** on the existing Linux Codespace, using `/workspaces/Unity-Game-Aris`.
- User's target device: **Snapdragon 680 / Adreno 610, 4–6 GB RAM**. No physical-device performance acceptance is implied by Linux Editor tests or cross-platform shader bytecode compilation.
- Scope: source fixes, regression fixtures and documentation. Native world assets, terrain data, materials, meshes and serialized quality settings are preserved.

**Verification scope:** The individual native collision, combat, diagnostics and shader gates passed. The original scene's 25-second combat soak and the isolated 100-bot benchmark also passed with clean raw logs after resolving a pre-existing Editor Search startup race. The earlier failing logs and the recovery are described below; no exception filter or game code was changed to conceal them. Physical Android performance remains unmeasured.

## 1. Architecture already present before this change

The saved world already separates visual detail, static collision and logical gameplay state. The audit checked the serialized scene and build manifest rather than inferring activation from the presence of a streaming class.

| Layer | Verified existing implementation | Consequence |
| --- | --- | --- |
| Persistent world | Four Terrain components in `Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity`; terrain, foliage, roads, original collision proxies and distant silhouettes remain in the persistent world. | District scene retirement does not remove the terrain beneath gameplay. It also does not make all terrain and vegetation assets eligible for memory reclamation. |
| District detail | Thirteen serialized, built additive scenes: twelve districts plus ForestLookout. Together they contain **94 LODGroups, 282 MeshRenderers and 282 MeshFilters**. | The existing partition can retire building detail without deleting the entire district's gameplay state. |
| Chunk ownership | These thirteen scenes contain **zero MonoBehaviours, Rigidbody components, CharacterControllers, Box/Mesh/Sphere/CapsuleColliders, lights or cameras**. | No bot or car is parented to a detail scene that disappears during an ordinary chunk retirement. |
| Scene admission | `AarisAdditiveChunkStreamer` uses authored **90 m load / 170 m unload margins around chunk bounds**, movement/load-time prediction and camera prefetch. The Android speculative residency cap is two cells. | These are margins around authored bounds, not a new universal 350 m sphere around the player. Hysteresis prevents boundary oscillation. |
| Transition scheduling | Android scene operations share a global in-flight gate. Completion frames are reserved before optional imports or teardown work continue. Existing ownership is rechecked at actual transitions. | Async requests do not justify overlapping every expensive native integration operation on the same frame. |
| Distant silhouettes | `AarisChunkFallbackVisibility` tracks scene paths and loaded scene handles. The first loaded detail instance hides its fallback; the last unloaded instance restores it. | The ordinary active-manager path avoids drawing detail and its fallback together. No new impostor assets were generated in this change. |
| Static collision | `AarisNearbyColliders` indexes authored static shapes in 250 m X/Z cells. Player admission is 55 m with a 6 m retention margin; oversized shapes use a bounded overflow list. | Visual residency and collision residency are separate policies. A detail scene can disappear while exact near-player collision remains available. |
| Vehicles | `AarisVehicleWorld` already uses **105 m load / 160 m unload radii**, with pooling and staged model work. | A real vehicle can remain resident after player-only static collision would otherwise be retired. This mismatch required the support fix below. |
| Offline combat | The director has **100 logical rival slots**, spatial target indexing and centrally scheduled dormant actors. Far simulation uses a **0.35 s interval beyond 90 m**, with separate visibility, detailed-rig and physical-receiver decisions. | A living far bot retains health, movement and combat state without requiring a full animated, physically moving humanoid every rendered frame. Its logical work still has a nonzero cost. |
| Pooled presentation | Bot gunfire already has six shared flash/tracer slots and twelve independent casing slots. Death crates keep up to 100 logical records while admitting at most sixteen pooled visuals within 95 m. | Existing pooling must be preserved. These bounded systems do not prove that every allocation in the entire game has been removed. |

Repository evidence: [main scene](../Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity), [build manifest](../ProjectSettings/EditorBuildSettings.asset), [detail scenes](../Assets/AarisWorld/Scenes/Chunks), [additive streamer](../Assets/AarisWorld/Scripts/AarisAdditiveChunkStreamer.cs), [fallback visibility](../Assets/AarisWorld/Scripts/AarisChunkFallbackVisibility.cs), [nearby colliders](../Assets/AarisWorld/Scripts/AarisNearbyColliders.cs), [vehicle world](../Assets/AarisWorld/Scripts/AarisVehicleWorld.cs), [rival director](../Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs), [bot gunfire VFX](../Assets/AarisWorld/Scripts/AarisRivalGunfireVfx.cs), and [death crates](../Assets/AarisWorld/Scripts/AarisDeathCrateWorld.cs).

### Scene retirement and asset memory are different operations

Unity documents that `SceneManager.UnloadSceneAsync` removes scene objects but does not itself unload their referenced assets; completion timing is also asynchronous. Asset reclamation requires a separate unused-asset pass. See [Unity 6: SceneManager.UnloadSceneAsync](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/SceneManagement.SceneManager.UnloadSceneAsync.html).

The project already signals completed retirement of an owned additive scene to `AarisMobileRuntime`. Its proactive cleanup waits for memory pressure, a two-second calm period, an acceptable paced frame and the shared work gate, with a thirty-second cooldown. Persistent proxies, terrain, foliage and shared material/mesh references can keep assets alive. No measured reclaimed-byte total is claimed here. See [mobile runtime](../Assets/AarisWorld/Scripts/AarisMobileRuntime.cs) and [streaming frame gate](../Assets/AarisWorld/Scripts/AarisMobileStreamingFrameGate.cs).

The actual restart path in [AarisOfflineMatchEnd](../Assets/AarisWorld/Scripts/AarisOfflineMatchEnd.cs) reloads the world with `LoadSceneMode.Single`. The audit found no additional demonstrated scene-ownership or fallback-lifecycle bug that justified a speculative streamer rewrite.

## 2. Concrete defects and fixes

### 2.1 Virtual actors could pass through active walls

**Cause:** `WouldCrossInactiveCover` asked the spatial index only about disabled colliders. That is appropriate for a physical mover whose native capsule handles enabled geometry. A virtual rival moves its Transform directly, so an active wall had no corresponding native movement sweep. Large authored bounds near the observer, or colliders still active during startup, could therefore be invisible to virtual movement.

**Change:** The cached obstacle query now has an explicit `includeActive` option, defaulting to `false`. Only virtual combat movement opts in. It considers active authored solids and solids disabled by this streamer while ignoring triggers, inactive GameObjects and independently disabled solids. Physical movement, spawn and vehicle callers retain their previous default semantics. A nonempty incomplete index holds virtual motion rather than treating incomplete cover data as an empty world.

This uses the existing bounds/index and does not wake PhysX, create a CharacterController, toggle renderers or allocate per probe. See `HasPotentialInactiveObstacle` in [AarisNearbyColliders](../Assets/AarisWorld/Scripts/AarisNearbyColliders.cs) and `TickVirtualCombat` in [AarisOfflineBRDirector](../Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs).

### 2.2 Short virtual steps could tunnel through a thin wall

**Cause:** The original minimum look-ahead box was centered 0.72 m ahead with a 0.36 m horizontal half-extent. Its near edge began at 0.36 m. For steps of 0.35 m or 0.70 m, a thin wall immediately beyond the actor's real 0.31 m radius could be missed before the early return for short movement.

**Change:** Virtual movement first checks a conservative swept AABB spanning the whole actual displacement, using the real receiver's **0.31 m radius** and existing vertical footprint. It then retains the original padded steering probes. Using 0.36 m padding at the sweep's starting point would incorrectly trap an otherwise clear actor next to a wall even while moving sideways away from it; the regression therefore requires a nonzero tangent escape as well as a blocked forward step.

The sweep is conservative broadphase safety, not exact capsule-versus-triangle collision. It preserves the existing bounded rejection of an unverified very large step and leaves physical-mover look-ahead behavior unchanged. Native tests cover both short distances, an intermediate diagonal footprint, active/streamed-out walls, oversized bounds, triggers and removal of cover. See [native bot integration QA](../Assets/AarisWorld/Editor/AarisBotCombatIntegrationQA.cs) and the [controlled actual-body fixture](../Tools/Validation/BoundedColliderProbeTests.cs).

### 2.3 A still-resident vehicle could lose its bridge or floor

**Cause:** Static collision retained only by the player's 61 m boundary could retire while a dynamic vehicle remained resident out to 160 m. Freezing the motor component alone does not stop an active native Rigidbody from falling or rolling.

**Change:** Configured vehicle motors register bounded native-physics residency records. The collider manager includes small support/obstacle footprints around active vehicle bodies, with a conservative 4.25 m base extent, a short velocity lead and one metre of envelope hysteresis. The collider manager's execution order **−250** precedes the motor's **−40**, so the next support surface is admitted before simulation even when the player has not moved. Sleeping cars with an unchanged envelope avoid a fresh grid scan.

All startup, immediate, forced-queue and deferred-queue retirement paths check support ownership. Configured records remain until destruction; inactive pool objects emit no pins. This also covers a motor that remains component-disabled across a GameObject pool cycle, where Unity does not send that disabled component a new `OnEnable` even though native physics resumes.

The change preserves real Rigidbody and WheelCollider authority. It does not silently change suspension, mass, braking, wheel models, moving-car physics or visible rendering. See [vehicle motor](../Assets/AarisWorld/Scripts/AarisVehicleMotor.cs), [collider manager](../Assets/AarisWorld/Scripts/AarisNearbyColliders.cs), and [native vehicle residency QA](../Assets/AarisWorld/Editor/AarisVehiclePhysicsResidencyQA.cs). Its 19-check native acceptance is recorded below. The new fixed-step poll is bounded but nonzero work; unchanged sleeping footprints avoid additional refreshes from this path while the existing periodic player refresh remains.

### 2.4 Android memory queries ran without reclamation work

**Cause:** The proactive cleanup condition could ask Android for memory pressure before checking whether an owned additive scene had actually retired. At cold start or during stationary play with no retirement debt, the result could not schedule this cleanup but still incurred the external query.

**Change:** Check `RetiredAdditiveSceneCount > 0` before device classification and `AarisAndroidMemoryPressure.TrySample`. Once genuine retirement exists, the previous memory-pressure, calm-period, cooldown and frame-admission policy remains active. Critical `Application.lowMemory` cleanup remains independent. The controlled regression proves zero memory-query calls when there is no retirement debt; it does not estimate milliseconds saved on the handset. See [mobile runtime](../Assets/AarisWorld/Scripts/AarisMobileRuntime.cs).

### 2.5 The allocation overlay labelled a timing marker as bytes

**Cause:** The overlay opened `GC.Alloc` and treated its raw last value as allocated bytes. On the actual tested Unity Editor this marker reported **`TimeNanoseconds`**.

**Change:** Record **`GC Allocated In Frame`**, require `ProfilerMarkerDataUnit.Bytes`, and require a completed sample. Invalid, unsampled, wrong-unit or negative data returns unavailable (`-1`) instead of an invented zero. The native diagnostics verified the corrected byte unit and rejection of timing data. This makes future allocation investigations meaningful; changing the measurement is not itself evidence of reduced allocation. See [performance overlay](../Assets/AarisWorld/Scripts/AarisMobilePerfOverlay.cs) and [native diagnostics QA](../Assets/AarisWorld/Editor/AarisMobileDiagnosticsQA.cs).

### 2.6 The wind-disabled SpeedTree variant referenced a missing wind buffer

**Cause:** `_WINDQUALITY_NONE` still compiled the leaf-color path, but URP omitted the wind constant buffer for that variant. The crown-exposure code read `_ST_WindGlobal.z`, producing a genuine shader compilation error in the baseline native run.

**Change:** Guard that read with `defined(ENABLE_WIND) && !defined(_WINDQUALITY_NONE)`, matching the availability of the URP buffer. Wind-disabled leaves retain their local and macro variation with neutral crown exposure. The five enabled wind modes retain their previous preprocessed math. No wind feature was removed to obtain a compile pass.

The native shader QA compiles six wind modes across ForwardLit, ShadowCaster, DepthOnly and DepthNormals for OpenGLCore, GLES3 and Vulkan: **72 bytecode variants, zero errors, 60 compiler warnings**. These warnings remain part of the evidence; this is not a zero-warning claim. Android backend bytecode compilation does not establish an APK build, installed Android player modules or Adreno driver performance. See [SpeedTree pass include](../Assets/TerrainDemoScene_URP/ShaderGraphs/CustomFunctions/AarisSpeedTree8MobilePasses.hlsl) and [native variant QA](../Assets/AarisWorld/Editor/AarisSpeedTreeVariantQA.cs).

## 3. Visual quality and rendering decisions

The implementation preserves tree and bush placement, foliage geometry and alpha coverage, native terrain data, original building LODs, weapon art, lighting/shadow settings, render scale and serialized URP quality settings. It adds no global shadow-distance reduction, density reduction or texture downscale. Support pinning and virtual cover checks change correctness and residency ownership rather than the authored appearance. The shader fix restores a valid variant of the intended foliage shading.

GPU instancing is useful when repeated objects share compatible mesh/material state. Unity also documents that URP custom-shader instancing and the SRP Batcher have path-selection restrictions, and that ordinary MeshRenderer instancing does not apply to SkinnedMeshRenderer. Merely enabling two options does not prove that both benefits apply to the same draw. This change therefore preserves existing batching choices instead of disabling the SRP Batcher indiscriminately. See [Unity 6: GPU instancing](https://docs.unity3d.com/6000.0/Documentation/Manual/GPUInstancing.html).

Instancing reduces submission overhead; it does not by itself eliminate foliage fragment work, alpha overdraw or shadow rendering. No zero-overdraw claim is made. Before further rendering changes, use the target player's GPU timings and Frame Debugger/Profiler evidence to identify the actual expensive passes. A source-preservation review cannot establish pixel-for-pixel visual equivalence; the fresh native photographs and their limits are described below.

Saved high-quality URP settings and the effective Android policy are different. The pre-existing constrained-device policy already uses 2x MSAA, 120 m shadows with two cascades, 80 m grass distance, an 85 m tree billboard transition and a cap of 50 full-LOD trees. This change does not lower any of those values or disable the depth/opaque textures required by the existing water rendering.

## 4. Relation to large mobile games

The verified GDC 2023 session **PUBG MOBILE: Performance Optimization on Mobile Platform**, presented by engine team lead Fan Zhang of LIGHTSPEED STUDIOS, describes a multi-level strategy for varied phone capabilities and changes across rendering pipelines and the engine. That supports treating residency, rendering and simulation budgets as separate engineering concerns. It does not document an exact transferable Unity implementation, a universal 350 m radius, or zero-cost remote AI. See the [official GDC Vault session](https://www.gdcvault.com/play/1029294/LIGHTSPEED-STUDIOS-Developer-Summit-PUBG).

The application to this repository is an engineering inference: retain the required visible representation, simulate distant logical state at a bounded cadence, and keep native physics only where its gameplay ownership requires it. Public material reviewed for this audit does not establish Rockstar's or Garena's proprietary internals. A networked 100-player mobile client is also not equivalent to simulating 100 complete offline AI controllers locally. No GTA 6 visual or frame-rate equivalence is asserted.

## 5. Verification ledger

Results below refer to the candidate source tested during this integration. Different fixtures have different scopes; their counts must not be added together as a count of unique game behaviors. The full bot integration run includes the new virtual-movement checks.

| Verification | Recorded result | What it establishes |
| --- | --- | --- |
| Native Unity C# compilation | **Zero C# errors** | The tested source compiles in the licensed Unity Editor. |
| Native virtual movement cover QA | **16 checks passed** | Real Unity objects/index data and actual virtual movement respect cover and permit clear tangent escape. |
| Native complete bot combat integration QA | **115 checks passed** | Existing combat integration plus the added virtual-cover regression passes in this environment. |
| Native diagnostics QA | **Passed**; new allocation counter is `Bytes`; old `GC.Alloc` observed as `TimeNanoseconds` | The allocation display uses a valid byte counter and treats unavailable/timing data honestly. |
| Native SpeedTree backend compilation | **72 variants compiled; zero errors; 60 warnings** | The exercised wind/pass/backend combinations produce bytecode. |
| Controlled production-body cover fixture | **3,317 assertions passed** | Cached grid/exhaustive consistency, active/disabled-cover semantics and continuous movement safety in deterministic Unity substitutes. |
| Controlled short-sweep baseline | **Failed at the expected thin-wall regression** | The new regression detects the short-step defect in the intermediate implementation without the continuous guard. |
| Controlled diagnostics fixture | **15 assertions passed** | No retirement debt means no proactive memory query; byte-unit and unavailable-sample rules hold. |
| Focused source regression gate | **143 tests passed** | Required source contracts remain intact; these are not native physics or GPU timing tests. |
| Deferred-release production-body fixture | **Passed** | The actual injected coroutine preserves pins, rechecks pins after yields and retires requeued unpinned solids. |
| Native vehicle residency QA | **19 checks passed** | Real isolated PhysX bodies keep support while parked, rolling, landing and resuming from the pool. |
| Original-scene coordinate photograph | **Passed**, two real camera readbacks | Four terrain tiles, trees and buildings render without the original magenta shader failure. |
| 25-second original-scene combat soak | **Passed with clean raw log** after Editor startup recovery | Real world combat, all seven weapon/scope combinations and four scope tiers ran successfully. |
| Isolated logical-actor benchmark | **Passed with clean raw log** after the same recovery | All 100 bots moved and 97/97 remote actors stayed virtual. |

### Native vehicle physics evidence

The first harness invocation used a Play-only scene API that was rejected in EditMode before behavioral assertions ran. The corrected harness creates an Editor preview scene and verifies that its native PhysicsScene is valid and different from the default physics world. A local raycast proves that the elevated floor belongs to that isolated world. It then simulates actual Rigidbody/WheelCollider objects. No authored scene is saved or simulated by this fixture.

All **19 checks passed**: elevated parked car, sleeping-envelope stability, stale forced/deferred release queues, rolling car with stationary observer, airborne landing, component-only disable, inactive-pool pin removal, reactivation with the motor still disabled, and destruction removing the registry record. The parked body settled at Y=20.610 m above a floor at 20 m; the rolling body reached X=115.459 m while retaining Y=20.607 m; the airborne body landed at Y=20.609 m. These positions validate support in the fixture, not subjective vehicle handling on a handset.

Entry: `Aaris.MapReview.Editor.AarisVehiclePhysicsResidencyQA.Run`. Report: `/tmp/aaris-vehicle-physics-residency-qa/report.txt`.

### Native photographs and full-scene soak

The unmodified baseline photograph failed with `FRAME_MAGENTA_SHADER_FAILURE` and the `_ST_WindGlobal` compilation error. After the guard fix, `AarisCodespaceSceneProbe.CaptureAt` passed at X=350, Z=220, yaw=90 degrees, pitch=25 degrees and distance=55 m. It produced genuine 1600x900 detail and context camera readbacks from all four native terrain tiles. The log contained no C# or shader errors. Both frames were visually inspected for terrain, foliage, buildings and the absence of magenta fallback rendering.

These are Editor camera views of the persistent world, not a complete player residency capture or a matched quantitative image-quality comparison. The baseline did not produce a valid reference image. Different detail/context camera positions and LOD coverage must not be treated as a before/after comparison, and no numerical "1% visual equivalence" measurement is claimed. Reports/images remain in `/tmp/aris-engine-audit-20261126/fixed-photo-*` on the tested Codespace.

The final clean run of `AarisFullSceneCombatSoak.Run`, with `AARIS_FULLSCENE_SOAK_SECONDS=25`, spawned **100/100** actual rivals in the original scene and sampled **1,400 loaded frames over 25.009 game seconds**. The encounter sample ended with 93 living rivals, sixteen visible bodies, eight close rigs and eight far LODs. Across the sample, peaks were eleven nearby living rivals, nine close rigs and eighteen visible bodies. All seven weapon/scope shot checks and all four scope tiers passed, as did crouch/prone/standing fire, health UI, damage and blocked shots through active or streamed-out walls. The fixture recorded 55 player-damage events. Its process returned zero, the report returned `RESULT=PASS`, and the raw log contained no C# errors, shader errors or exceptions.

The first attempt's raw Editor log contained **two `ArgumentOutOfRangeException` events** despite passing gameplay assertions. The stack was `UnityEditor.Search.SearchDatabase.EnumerateAll` -> `GetDefaultSearchDatabase` -> `SearchInit.IndexationOnStartup` -> `EditorApplication.Internal_CallDelayFunctions`. The same stack appeared in the actual pre-change benchmark and the first post-change isolated benchmark, with no game-script frame. The external aggregate check correctly failed these first attempts.

Investigation found no `UserSettings/Search.index` and no `Library/Search`, while the default startup indexing callback ran after the QA had already requested Play. Unity 6000.3.26f1 declines to create the default index while entering Play, then its iterator accesses an empty database list. The exact-version reference is [UnityCsReference, SearchDatabase.cs](https://github.com/Unity-Technologies/UnityCsReference/blob/c57f9f00d22b3794130a9bfa76dd7c8b32e4f2f1/Modules/QuickSearch/Editor/Indexing/SearchDatabase.cs).

A normal Edit Mode startup, without an immediate Play method or `-quit`, allowed Unity to create its default Search settings and emit `[Indexing] Starting Initial Indexing for Assets`. Only the deliberately launched process was stopped afterward. No existing cache was deleted, indexing was not disabled, and no exception was suppressed. This startup message proves initialization began, not that all indexing finished. Rerunning the unchanged soak and benchmark then produced clean raw logs. On a fresh Codespace, allow this ordinary Editor startup before immediate batch Play tests rather than hiding the exception.

Final clean Editor timings are retained only as diagnostic context: the real-scene 100-bot Main Thread sample averaged 17.363 ms, with p95 16.716 ms and approximately 187,643 allocated bytes/frame including Editor/world/test overhead. Its zero-bot real-scene baseline averaged 16.801 ms and approximately 158,226 bytes/frame. These observations do not establish phone performance or zero GC. The isolated logical-actor benchmark moved all 100 bots, with 97/97 remote actors virtual; Main Thread mean was 0.274 ms in the original baseline and 0.269 ms in the clean final run. That small difference is not a demonstrated performance gain and excludes the map, humanoid rendering, Android GPU and thermals.

### Source and publication tracking

The tested candidate starts at `ca7b838d03b8c780043b391df923e630a0c1c4d0`; the runtime, shader and native QA source is recorded in [candidate commit a85132a8](https://github.com/sundamunda289-art/Unity-Game-Aris/commit/a85132a8c7af23656929415364c4cf088fcba424). The subsequent evidence update changes only this report. Each transferred source file was SHA-256 checked before native execution; the eight unrelated pre-existing tracked changes remained byte-identical. Temporary payloads, reports and fixtures used `/tmp`; no Git worktree was created. [Pull request #1117](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/1117) and the final handoff record the final head, CI status, merge commit and independently verified Codespace `main` synchronization. This source report does not substitute for those publication checks.

## 6. Device acceptance still required

There is no physical Snapdragon 680 / Adreno 610 run in the evidence above. Native Editor correctness, software/desktop graphics captures and successful GLES/Vulkan compilation cannot establish mobile CPU/GPU frame times, driver behavior, peak resident memory, thermal stability or battery load.

The next device acceptance run should use the same authored visual settings and a reproducible route through dense foliage, district boundaries and bridge/vehicle interactions, with a full 100-rival match. Record cold versus warm traversal, rapid camera turns, physical-to-virtual re-entry, gunfire/loot activity and repeated district entry/exit. Capture CPU Main/Render Thread and GPU frame time where supported, p95/p99 frame duration, stutter events, PhysX time, allocations per frame from the corrected byte counter, Unity/OS memory, and performance after thermal warm-up.

Until that evidence exists, the supported conclusion is: **specific residency, collision, shader and diagnostic defects are corrected and covered by the recorded tests; the requested handset performance outcome remains unmeasured.** No FPS increase, RAM saving percentage, universal zero-GC guarantee or complete elimination of loading hitches is claimed.
