# Android forest-clearance CPU audit — 19 November 2026

Canonical source was inspected at `79371f0` and reconciled with `5a8ad4b`,
including selective tree retention #726, rival overflow pooling #725 and the
frame-callback blocking guard #723. The existing Codespace responded to a
private terminal command. Candidate tests used isolated `/tmp` paths and
preserved its working files. This is source/algorithm evidence; the handset's
dominant bottleneck still requires a device capture.

| Area | Current source evidence | Action or limitation |
| --- | --- | --- |
| Tree clearance CPU | Each footprint/relocation scanned per-tile retained lists. The `List<TreeClearanceSample>` loop incorrectly used `.Length`. | Replace with exact spatial queries; preserve #726's selective retention and remove the invalid list member. |
| Native tree initialization | 256-record batches did not account for CPU throttling or allocation cost. | Add a cooperative 1 ms deadline checked every 16 records; retain the hard count limit and forward progress. Individual native calls cannot be preempted. |
| Model startup | Character/animation, aircraft, weapon and vehicle code already uses async prewarm and separate work limits. | Preserve existing work. Limits can overlap; trace actual combined cost before delaying player-visible content. |
| Physics | Terrain/player stay physical; static cover has spatial streaming and inactive-bounds fallbacks. Vehicles and physics catch-up have constrained-device limits. | Preserve collision shapes, fixed timestep, nearby safety and fallback probes. |
| Bots/loot/vehicles | Six rival slots, 40 live weapon pickups, ten nearby vehicles; distance-aware rival work and visibility-paced loot. | Existing source contracts pass. Counts alone do not establish a multi-second stall cause. |
| GPU/clarity | Native scene has `adaptiveAndroidResolution: 0`; existing constrained-GPU runtime policies alter MSAA, foliage, shadows and wind. | No quality/render policy changes here. This patch does not restore identical desktop fidelity. |
| RAM/exits | Existing async diagnostics distinguish low-memory, long frames, cleanup and previous Android process exits. | No handset report was available; GPU deadlock, LMK, ANR and native crash remain unconfirmed. |

Each retained tree clearance record now occupies one 32 m world cell. Queries
visit the rotated footprint's projected extents plus the maximum 3.8 m tree
margin and one guard cell for float rounding. Candidates use the original
strict X/Z comparisons. Per-terrain seam coverage, all relocation offsets,
foundation/collision checks, house order and temporary-cache cleanup remain.
The native forest, scene, TerrainData, meshes, materials, textures, tree count,
render ranges and resolution are untouched by this change.

Selective retention limits the snapshot to all relevant sites; the new index
narrows each individual query further. Dictionary buckets add temporary memory
overhead relative to small flat lists. A very dense single cell can still
require a linear scan. These are explicit tradeoffs, not a guaranteed FPS gain.

## Executed validation

The production `AarisTreeClearanceIndex.cs` compiled with the installed Unity
6000.3.26f1 Mono C# toolchain and ran in a standalone differential harness:

- **24,758 comparisons passed** against an independent list-scan predicate,
  including negative cells, seams, touching edges, yaw extents and relocation
  offsets. The harness stores every finite input record exactly once.
- Synthetic forest: 7,503 records, 3,366 occupied cells, maximum ten per cell.
- One shared-VM run, 5,000 queries: **353.34 ms full forest scan; 13.26 ms indexed**.
  This intentionally unfiltered baseline is not #726's smaller retained list,
  an Android benchmark or a claim about final device FPS.
- Tree initialization/retention, expansion geometry, inactive cover, async
  prewarm and profiler marker checks passed. All 36 gameplay C# files parsed;
  38 frame callbacks passed the blocking-call guard.

Native Unity 6000.3.26f1 additionally compiled all 36 gameplay scripts in an
isolated scripts-only project: Editor exit **0**, zero C# errors and a real
`Assembly-CSharp.dll`. The two changed source blobs matched the published PR.
This did not load the native map or run Play Mode.

The separate Android Release script-compile attempt did **not** pass. This
Editor's `PlaybackEngines/AndroidPlayer` module is absent; Unity's Burst editor
package reports `CS0234` for missing `UnityEditor.Android.AndroidExternalToolsSettings`.
Do not classify this tooling failure as a handset crash or claim an Android
assembly/APK was produced. No Editor/module reinstall or original-project
rewrite was performed to hide the result.

The settlement CI now compiles and executes the production C# index. Run:

```bash
python3 Tools/Validation/run_tree_clearance_csharp.py
python3 Tools/Validation/test_android_tree_clearance_streaming.py
python3 Tools/Validation/test_android_selective_tree_retention.py
python3 Tools/Validation/test_settlement_expansion.py
python3 Tools/Validation/test_settlement_streamed_obstacles.py
```

The runner accepts system `mono-mcs` or `AARIS_UNITY_EDITOR_DATA` pointing to
the installed Editor's `Data` directory. Standalone C# execution and syntax
checks do not prove Unity scene rendering, Android player compilation or FPS.

## Device acceptance still required

Build an APK from the final integration commit. Capture cold launch, dense
forest/settlement landing, driving across cell boundaries, combat and repeated
match transitions on the actual 4 GB phone. Inspect main/render/GPU timing,
p95/p99 frame time, PSS and Android exit evidence; verify houses/trees/cover
remain visible and solid. No guaranteed 60 FPS, resolved phone freeze or
eliminated crash is asserted.

Primary references: [Unity GetTreeInstance](https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/terraindata/gettreeinstance)
and [async loading integration](https://docs.unity.com/en-us/engine/6000.0/script-reference/unityengine/application/backgroundloadingpriority).
