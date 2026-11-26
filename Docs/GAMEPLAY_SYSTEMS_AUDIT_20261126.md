# Player stance, rival tactics, and aircraft rendering audit — 2026-11-26

**Native verification complete for the source snapshot identified below.** This report records the implementation, failed checks that led to corrections, final accepted evidence, and the limits of each measurement. The preserved pre-existing importer assertion is reported separately from native results.

Canonical repository: `sundamunda289-art/Unity-Game-Aris`. Starting main commit: `7e39775bb9525e0278caedb7c67d4425ec81242c` (PR #1117).

The changes address natural movement, dependable low stances, and inexpensive aircraft presentation. The evidence below comes from source review and the real Unity **6000.3.26f1** Editor on the existing Codespace. Graphics checks use software OpenGL. Android shader bytecode checks cover GLES3 and Vulkan, but are not measurements on a Snapdragon 680 phone.

## Scope and preservation

The complete player locomotion/stance, rival movement/combat, and aircraft/drop systems were reviewed before implementation. Work used the existing workspace and temporary staging under `/tmp`; no new Git worktree was created. Existing tracked and untracked user work was backed up privately before targeted updates. Byte preservation was explicitly verified before commit, as recorded in the final section. Main merge and post-sync evidence belong to the separate delivery record.

The implementation retains the imported soldier, authored animations, aircraft body, existing material maps, and existing detailed/distant representation policy. It does not obtain its savings by deleting the aircraft body, substituting a lower-detail player, removing the near soldier's PBR materials, or reducing world art. The aircraft's native source and instantiated body both contain **42,230 triangles**. Its OBJ contains **42,238 triangular faces**; the native import count is the relevant rendering count. Both source topology and retained native mesh identities are checked.

## 1. Player stance and grounded weapon pose

### Problem and correction

Stance changes involve several authorities: requested posture, capsule geometry, movement limits, camera eye height, the evaluated humanoid pose, and the later weapon arm solve. A correct value in one subsystem does not prove that the final rendered character clears the floor or ceiling.

The stance implementation keeps capsule height and center consistent with a fixed foot position, smoothly adjusts camera eye height to the current capsule, and retains conservative movement restrictions while a rising transition is incomplete. Standing or jumping from a low posture must pass the actual clearance query. Crouch and prone movement remain **2.4 m/s** and **1.1 m/s**; their step allowances are restricted independently from standing movement.

Ceiling checks use reusable physics buffers and ignore the player's own body. A full small buffer retries the existing larger reusable buffer; if that too saturates, the existing rare allocating `Physics.OverlapCapsule` fallback remains so every contact can be examined. This headroom path is not universally allocation-free and does not simply reject all saturated queries. Integrating continuous capsule resizing exposed a compatibility issue in the existing clearance formula: during small intermediate height increments, its sphere could extend above the proposed capsule top and prematurely block the new transition beneath a legitimate ceiling. The previous stance implementation resized instantly; this issue was caught while introducing the continuous path. The corrected upper sphere center is bounded by the proposed top minus radius, and the lower center is clamped to that upper center. The native regression includes a real roof only **40 mm above the final standing capsule**.

Prone support uses a cached, bounded ground normal and verifies the extended head/leg footprint against the support plane. Its new support-ray query fails closed if its unordered result buffer saturates. The visual rig aligns to that plane while the CharacterController remains upright. The support checks reject unsuitable slopes rather than forcing a pose through unsupported geometry. Existing authored clips, crossfades, and foot correction remain part of the final pose.

Gun stability now follows the grounded physical stance. At rest, crouch recoil/spread multipliers are **0.82 / 0.78** and prone multipliers are **0.62 / 0.55**. Movement progressively releases these benefits; an airborne or inactive motor receives the neutral multiplier. This prevents a requested low stance from granting grounded stability before the physical body supports it.

### Native rendering found an additional hand problem

The initial actual prone capture measured the left wrist **40.4 mm below the flat support plane**. The body solve ran at execution order +200, but the weapon grip solve at +310 subsequently pulled that hand back down. Fixing only the body could not solve the final rendered result.

The inventory now raises the complete gun/grip frame along the existing support normal when necessary, capped at **18 cm** and further limited by both arms' reachable spheres. Both grip targets move together; aim rotation remains unchanged. The ordinary weapon attachment reconstructs its base pose each frame, so the correction does not accumulate. No extra floor raycast or body lift is added.

The seven actual Play Mode pose cases after the first grip correction initially reported JSON **PASS**, exit code 0, and no runtime errors: armed flat, +12°, −12°, +24°, and +12° cross-slope; unarmed flat and +12° cross-slope. **Independent visual review rejected that initial result as insufficient**, leading to the further correction and stricter final acceptance below.

The rejected +24° pose had a hovering torso: hips were **273 mm above support**, and elbows were **288 / 156 mm above support**. The +12° and cross-slope poses also sat too high. Conversely, the flat right elbow was **18 mm below support**, and the downhill left elbow was **20 mm below support**. The earlier JSON checks recorded elbow values but did not constrain them, and did not reject the excessive positive torso gap. Passing those assertions therefore did not establish an acceptable grounded prone pose.

The corrective implementation now reuses the actual cached support hit point. It lowers excessive settled/still CC0 prone pelvis clearance toward a hip-width-based **130–170 mm envelope**, bounded by head/chest and unarmed-hand clearance, before the existing leg IK. The visual root is reconstructed each frame before this smoothed correction, and the offset eases away during movement/get-up and resets on presentation handoff or teleport.

The final armed elbow solve selects a point on the exact two-bone bend circle that clears the support plane by 35 mm when feasible. In an infeasible configuration it selects the highest reachable point without stretching the arm or independently moving its wrist. Independent mathematical/source review found no blocking defect; a deterministic 20,000-case numerical mirror checked segment lengths and feasible/infeasible plane selection. This is mathematical evidence, not a substitute for native skin/pose observation.

The strengthened native gate rejects hips above **190 mm** and elbows below **5 mm**, retains the existing foot/hand/grip limits, and records support-point, calibration/drop, and maximum reachable elbow-height telemetry. The final update-8 rerun passed **all seven strict cases in 348.53 seconds**, with zero runtime errors or C#/shader exceptions. All seven final image pairs were inspected in both implementation and independent review and accepted as grounded poses; no further concrete skin or sole-hover blocker was found.

| Final prone case | Hip joint above support | Lowest elbow joint above support | Largest wrist-to-grip error |
|---|---:|---:|---:|
| Armed, flat | 106.055 mm | 34.995 mm | 0.0106 mm |
| Armed, +12° | 129.999 mm | 35.000 mm | 0.0191 mm |
| Armed, −12° | 84.291 mm | 34.984 mm | 0.0188 mm |
| Armed, +24° | 129.999 mm | 34.994 mm | 0.0079 mm |
| Armed, +12° cross-slope | 130.000 mm | 35.013 mm | 0.0254 mm |
| Unarmed, flat | 149.341 mm | 61.502 mm | Not applicable |
| Unarmed, +12° cross-slope | 148.071 mm | 60.642 mm | Not applicable |

The +24° hip gap improves from approximately **273 mm to 130 mm**, with a bounded **143.62 mm** body correction. The largest measured support-point error is about **0.00023 mm**, and support normals agree to floating-point precision. The final ankle joint gaps are approximately **69.46–75.03 mm**; these are **ankle joint measurements, not shoe-sole clearance measurements**. Wrist-to-grip errors likewise measure analytic targets, not the entire skinned palm surface. Actual image inspection complements these numerical checks.

The final source-matched transition portrait gate passed with its exact **16-frame success marker**, exit 0, in **113.63 seconds elapsed**, with no C#/shader exceptions. It produced **16 images**, **12 timestamp/contact records with distinct game frames**, and **8 records with actual blending active**. Implementation and independent image review found no new visible get-up, contact, or limb-inversion blocker. These final portraits show **unarmed transitions**; earlier captures with different gear are not a controlled armed before/after comparison. Armed settled contact and grip behavior is covered by the separate seven-pose fixture above.

Primary files: `AarisBattleActions.cs`, `AarisThirdPersonPlayer.cs`, `AarisCharacterVisual.cs`, `AarisEquippedGun.cs`, and `AarisWeaponInventory.cs`.

## 2. Rival locomotion, spacing, ranged combat, and presentation

### Movement and combat

Rivals read the actual enforced player locomotion profile: **4.2 m/s jog, 6.5 m/s unarmed sprint, 5.7 m/s armed sprint, 2.0 m/s combat movement, 13 m/s² acceleration, and 19 m/s² braking**. Velocity persists through physical/virtual representation changes. Near rivals still move through CharacterController collision; animation blending and run playback follow actual travel rather than an unfulfilled desired speed.

A fixed 100-actor spatial structure adds local separation using **8 m cells**, a **6 m influence radius**, and at most **32 visited candidates per separation calculation**. Physical/virtual refresh intervals are 180/350 ms. Spawns reject occupied positions within 8 m. Cover selection checks both current occupancy and active 4 m cover reservations, preventing two approaching actors from selecting the same still-empty shelter. Reservations expire with the existing cover deadline and cease to block when the reserving actor dies. These checks use bounded managed arrays rather than adding an overlap query to every bot frame.

Weapons use tactical range caps while respecting their authored maximum range: shotgun 22 m, pistol 30 m, SMG 40 m, rifle 65 m, and DMR/sniper 80 m. Unarmed reach remains 2.25 m. Armed rivals periodically stop to aim and require actual ground speed at or below **0.45 m/s for at least 120 ms** before a ranged shot. Range holding and hysteresis reduce close pursuit and oscillation. Cover and zone escape retain movement priority.

The existing fairness and obstruction rules remain authoritative: at most two reserved player attackers, real near-target LOS and muzzle checks, post-movement range/facing checks, finite ammunition/reload, and virtual terrain/building cover. Abstract combat cannot damage the player, nearby rivals, or a victim that requires physical simulation. Longer-range virtual cover checks retain bounded terrain sampling.

### Contact shadows and distant readability

Near grounded visible rivals share a soft contact quad and one instanced draw batch. Ground probes are limited to **two per frame**, with at least 250 ms between probes for one actor. Distance fading and per-actor eligibility prevent contacts from being drawn for hidden or airborne actors. Near soldier materials and their existing shadows remain intact.

Independent review caught a starvation bug in the initial first-come probe admission: at 30 FPS, the first approximately 16 persistent actors could consume every permit and prevent later actors from ever obtaining a floor sample. The corrected scheduler centrally chooses the oldest due eligible deadlines. A native fixture submits all 100 contacts in fixed order at 30 Hz and verifies eventual service of all 100 without exceeding the two-probe budget; it also tests removal of the first and last slots.

The existing **84-triangle distant rival mesh** receives inexpensive vertex-evaluated sun, ambient, rim contrast, and fog. It is still the existing mesh and simulation LOD.

### Real rendering caught a depth-priming failure

The first actual distant-material capture added **zero object pixels**, even though a stock URP Unlit material on the exact same enabled, visible mesh added **29,068 pixels**. The active Forward renderer uses automatic depth priming and no SSAO. The custom opaque shader lacked the depth pass needed by that pipeline.

The shader repair adds a DepthOnly pass with the exact same position calculation and shared material layout as its color pass. A compatible DepthNormalsOnly pass preserves the stock material's capability when requested by renderer settings. It does not enable SSAO or add a second color pass. Review checked instancing, shared constants, and normal packing. The final native rerun passed **all 42 requested combined shader programs** across OpenGL, GLES3, and Vulkan, and **19 render checks**. In a **close inspection of the production 84-triangle distant LOD**, the repaired shader adds **29,072 object pixels**, compared with **29,068** for the stock Unlit control on the same mesh. This proves the mesh's shading and fog integration in that fixture; it is not a native 155–485 m gameplay-distance readability test and does not prove transition quality, visibility through foliage, or photorealism. The contact and 100-actor probe-fairness checks also pass.

A separate build-retention review found `m_InstancingStripping = 0` (`StripUnused`). Runtime-only instancing-enabled materials do not provide a serialized build reference retaining their instanced variants. Two new Resources templates, `AarisRivalContactMaterial.mat` and `AarisRivalReadabilityMaterial.mat`, explicitly reference their shaders with instancing enabled. Runtime code clones each template once. Native checks verify these references and flags. Existing material assets and global stripping/render settings remain unchanged.

Primary files: `AarisOfflineBRDirector.cs`, `AarisRivalPresentation.cs`, `AarisRivalContact.shader`, and `AarisRivalReadability.shader`.

## 3. Aircraft flight and GPU propellers

The original aircraft already followed a kinematic mathematical flight path. That working path and **76 m/s** speed are retained. The implementation does not claim to have removed an existing heavyweight flight simulation that was not present. It enforces a render-only aircraft hierarchy, prevents imported colliders/rigidbodies from becoming flight work, and creates marker geometry from cached primitive meshes without manufacturing unnecessary live colliders.

Two preferred-aircraft rotors previously used **264 triangles and four renderers**, with CPU rotation of their transforms every frame. They now share one static **48-triangle mesh and one renderer**. A GPU shader supplies low-RPM blades, a high-RPM semitransparent exposure disc, and sun-responsive glint. Unity supplies shader time; runtime C# does not rotate the rotor transforms or upload an angle every frame.

Both wingtip wakes share a **16-triangle renderer**. They are short, tapered shader ribbons, with no particles, TrailRenderer history, or mesh rebuild loop. Thus the new rotor-plus-wake effects total **64 triangles and two renderers**, compared with four renderers for the old rotors alone. These are verified geometry/renderer counts, not a claim that the complete scene has only two draw calls.

The authored aircraft body, five PBR finishes, normal maps, and material values remain intact. Runtime-only material copies avoid modifying saved assets. Both effect renderers omit shadow casting and additional light components.

After ejection and touchdown the plane continues along its established path. Retirement requires distance of at least **850 m**, conservative aircraft/wake bounds outside the camera frustum, and **two continuous seconds** outside view. Looking back resets that grace. The former arbitrary 4.5-second disappearance is removed. Once landed and the plane has retired, the drop Update returns immediately.

### Rendering fixture correction and result

The first isolated rotor fixture incorrectly assigned the plane to layer 26. The live URP opaque and transparent masks were both **375**, excluding that layer despite the camera's own mask. Its solid-blue frames also exposed a faulty nonblank assertion that compared different RGB channels rather than variation across pixels.

The corrected fixture preserves production layers, derives camera placement from the actual transformed rotor mesh, verifies the frustum, and requires spatial image variation. The original motion threshold was retained. The actual rerun passed **10 render checks**, including **507 changed pixels** between GPU time offsets while rotor transforms remained static, plus a visible stopped-blade/running-disc difference. Front, rear, and departing views were rendered using the actual imported aircraft and scene lighting. Private images were inspected; they are not embedded in this repository report.

Independent inspection of the final native montage confirms the retained aircraft silhouette, centered soft running discs, clearly visible stopped blades, and thin wingtip wakes. No missing/pink mesh or detached rotor is apparent. The aircraft body remains predominantly gray with modest material contrast under the scene sun; these images do not establish photorealistic art or prominent metallic sun glints. The resized montage also cannot demonstrate the small A/B phase change by eye; the 507-pixel native comparison supplies that specific evidence.

Primary files: `AarisOfflineDrop.cs`, `AarisOfflineDropVisuals.cs`, `AarisAircraftPropeller.shader`, and `AarisWingtipCondensation.shader`.

## Native verification ledger

The final stance, seven-pose, transition, and full-scene results use the same verified update-8 source snapshot. Its 44-file manifest digest and pre-commit preservation result are recorded under **Source verification and delivery**. Earlier compatibility passes are identified explicitly below.

| Gate | Verified result | Meaning and limit |
|---|---|---|
| Actual Unity compilation | PASS; zero C# and shader errors in the latest completed compile | Update-8 prone correction also passes its native geometry and inspected-image gate |
| Player stance native QA | **Latest rerun: 3,749 checks PASS; steady stance GC 0 B; 18.42 s elapsed** | Includes latest four-file correction; allocation measures only the settled stance delegate specified below |
| Existing StanceTurn integration | Earlier compatibility run PASS | Predates the final grounding correction; final transition/source-matched scene checks are listed separately |
| Existing CombatIntegration | Earlier compatibility run PASS | Predates the final grounding correction; final seven-gun/three-stance physical scene checks provide the later integration evidence |
| Player seven-pose visuals | **Final strict native and independent visual PASS; 348.53 s, zero errors** | All seven accepted; joint/analytic-target measurements and their limits are recorded above |
| Existing 16-frame transition portraits | **Final native and inspected-image PASS; 113.63 s** | 16 unarmed images, 12 distinct timestamp/contact records, 8 records with active blending |
| Rival native tactics | **67 checks PASS** | Profile, range, settle, spacing, native LOS, cover reservations and retained instancing-template checks |
| Rival actual contact effect | PASS: **2,494 darkened pixels** | Actual instanced contact effect, with matched control image |
| Rival probe fairness | PASS: all **100** persistent actors served within fixed budget | Actual production scheduler and native ground rays |
| Rival final depth shaders/render | **42 shader programs and 19 render checks PASS** | Close inspection of production distant LOD: 29,072 pixels versus stock 29,068; shading/fog passes |
| Aircraft native runtime | **41 checks PASS** | Retained body/materials, geometry, render-only hierarchy, flight, culling and bounded helper allocation |
| Aircraft native shader checks | **14 checks PASS**, including 12 compiled variants | OpenGL/GLES3/Vulkan base and fog/instancing coverage |
| Aircraft actual renders | **10 checks PASS** | Includes real shader-time motion with static rotor transforms |
| Full-scene 25-second run | **Final source-matched PASS; exit 0, zero C#/shader exceptions** | 25.000 simulated seconds, 138.65 s elapsed, 100/100 spawned and 98 alive; details below |
| Logical candidate benchmark | **PASS; 18.37 s elapsed** | All 100 actors moved; all 97 expected virtual actors remained virtual; scoped comparison below |
| Changed source-validation gates | **13 of 14 PASS in live workspace** | Remaining gate encounters pre-existing user importer metadata; see the preservation note below |
| Main merge and Codespace synchronization | Delivery record is separate from this pre-merge audit | Canonical merge/HEAD identifiers and post-sync preservation outcome will be recorded in the PR/final delivery report |

Entry points are `AarisPlayerStanceQA`, `AarisPlayerSlopeVisualQA`, `AarisRivalTacticsQA`, and `AarisAircraftIllusionQA`, alongside the existing integration gates. Raw logs, authentication data, private screenshots, and license material are not publication artifacts.

### Source validation and preserved importer metadata

Thirteen of the fourteen changed source-validation gates pass against the live workspace. The remaining gate contains an existing `hasTranslationDoF` importer assertion: five user-modified `.meta` files already contain `hasTranslationDoF: 0`, whereas canonical main contains `1`. Those pre-existing user files were not overwritten to make the assertion pass.

A separate plain temporary overlay under `/tmp` used the exact candidate code and hydrated FBX inputs with the five canonical metadata versions. **All 43 offline asset assertions passed** in that controlled overlay. This isolates the source assertion's importer-data dependency; it does not turn the live failure into a pass or establish that the user's importer values should be changed. Native candidate tests use the actual workspace. Preserve this distinction in the final merge report.

### Actual full-scene integration

The final source-matched update-8 scene soak passed **25.000 simulated seconds**, exited 0, and completed in **138.65 seconds of elapsed execution**, including setup and other verification work. No C# or shader exceptions were reported. All **100 of 100** rivals spawned on the first attempt; **98 remained alive** at the end. Final visible representation counts were **10 total: 8 close and 2 far**. The harness reported peaks of **14 near, 8 close, and 11 visible**, and recorded **52 actual player-damage events**. The loaded scene supplied **1,419 sampled frames** and **70 visual samples**.

The integrated gate also passed all **seven guns**, **four scope tiers**, standing/crouching/prone physical shots and LOS, obstruction by a streamed wall, and health/HUD integration. These are actual scene behavior checks; they do not establish that every terrain arrangement or combat situation has been exhaustively tested.

## Performance evidence and its boundaries

The native warmed allocation assertions cover **1,000 settled `AarisBattleActions.AdvanceStanceTransition` delegate calls at each of 10/30/60/120 Hz**, **10,000 rival spacing/velocity/admission helper iterations**, and **2,048 aircraft flight steps plus 2,048 aircraft visibility checks**. These measured intervals allocate zero managed bytes. The stance measurement does **not** include the complete `AarisCharacterVisual` or weapon IK pipeline. None of these intervals establishes zero allocation across the entire match, rare saturated headroom overlap fallback, asset initialization, Unity internals, audio, rendering, or all bot combat paths.

### Logical AI benchmark

The logical candidate benchmark completed in **18.37 seconds elapsed**, with **4,419 loaded samples across 1.3 simulated seconds**. It observed movement for all **100 actors**, consisting of **3 near and 97 virtual actors**, and preserved the expected simulation modes. This is an **unarmed scheduling fixture**: it creates no rendered meshes or animations/contact rendering, assigns no guns, and prevents loot acquisition with the loot deadline set to its maximum. It therefore does not exercise armed ranged firefights, gun-dependent cover behavior, or the full presentation workload.

The before/current Editor counter samples are:

| Logical fixture | Main Thread mean | Main Thread p95 | GC mean |
|---|---:|---:|---:|
| Prior baseline, no bots | 0.208 ms | 0.249 ms | 704 B/frame |
| Prior baseline, 100 bots | 0.290 ms | 0.330 ms | 705.2 B/frame |
| Current candidate, no bots | 0.220 ms | 0.253 ms | 704 B/frame |
| Current candidate, 100 bots | 0.293 ms | 0.328 ms | 705.2 B/frame |

The candidate loaded GC maximum is **1,162 B/frame**. The loaded Main Thread mean changes from 0.290 to 0.293 ms while p95 changes from 0.330 to 0.328 ms, with the same reported mean allocation. **Recorded counters are similar in this unarmed scheduling fixture; they do not establish causal improvement or absence of regression for armed combat or the rendered game.** Do not convert these isolated Editor counter samples into claimed phone FPS.

### Final source-matched full-scene profiler samples

| Current full-scene fixture | Main Thread mean | Main Thread p95 | GC mean | GC maximum |
|---|---:|---:|---:|---:|
| Scene baseline without bots | 16.817 ms | 16.719 ms | 166,077.4 B/frame | 236,601 B/frame |
| Loaded scene | 16.666 ms | 16.720 ms | 153,918.2 B/frame | 230,208 B/frame |

The frame-delta mean/p95 values are **16.743 / 16.735 ms** without bots and **16.667 / 16.743 ms** in the loaded run. The rendered run is paced near 60 Hz, so values near 16.67 ms include that pacing and cannot support a before/after speed claim. This is a same-candidate scene baseline/loaded comparison, not a pre-change/new-code comparison. Different randomized soak runs are not used as an optimization comparison.

The scene reports substantial nonzero managed allocation in both conditions, including Editor/QA overhead; these are not game-only allocation measurements. The lower loaded mean does not identify its cause or prove an allocation optimization. These whole-scene numbers must remain separate from the zero-allocation helper intervals and the lightweight unarmed scheduling benchmark.

All values above are **Editor evidence**, not phone frame-time guarantees or full device GPU costs. Software OpenGL rendering and Android shader compilation cannot establish Snapdragon 680 FPS, thermal behavior, power use, or device GPU cost. The work demonstrates bounded new mechanisms and verified behavior; it does not establish whole-game zero GC or literal zero rendering cost.

## Source verification and delivery

The final stance, seven-pose, 16-frame transition, and full-scene runs use the verified update-8 **44-file source manifest** with aggregate SHA-256:

`d1482455e98b91b8988c850190f65dc2b286f332d4e2500ccbfe8eb3b0ae52f5`

The aggregate is calculated from sorted repository-relative paths, each followed by NUL, its file SHA-256, and newline. It identifies the selected tested inputs independently of the eventual documentation/merge commit. The aircraft and rival code/shader evidence above applies to their retained verified files; the legacy compatibility runs are qualified in the ledger. Native results do not erase the preserved importer-data distinction in the source tests.

Before commit, **all 84 pre-existing user-file hashes matched the initial snapshot**. Four known incidental serializations introduced by the Editor verification were restored to their verified initial bytes. One newly generated Editor metadata file was backed up and removed. No Editor process was left active at this preservation check. These checks explicitly preserve the user's original tracked and untracked work; no existing material or importer metadata was changed to force a test to pass.

The canonical main merge SHA, synchronized Codespace HEAD, and post-sync preservation result will be recorded in the PR and final delivery report after those actions complete. This audit establishes the tested source identity and verified pre-commit state; it does not invent a future merge SHA or make its own committed content depend on that SHA.
