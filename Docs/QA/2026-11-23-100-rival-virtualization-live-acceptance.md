# Live 100-rival virtualization acceptance — 23 November 2026

**Canonical source under test:** `sundamunda289-art/Unity-Game-Aris` commit `06420239c34fbc6b9a08e2e262fcc393bd0b98bc` (already on canonical `main`).  
**Test environment:** the existing Codespace `aaris-ai-ubuntu-minimal-gxpgxwqvv9rq29x46`, Unity **6000.3.26f1**, Linux/Xvfb, four logical CPUs.  
**Intent:** record native validation of the already-shipped render/simulation separation. **No shipping AI code, authored scene, model, material, or Android quality setting is changed by this QA report.**

## Verified production behavior

- `AarisOfflineBRDirector` retains exactly **100 logical rival slots**, health/death/kill-feed events, spatial-target lookup and a maximum of **two simultaneous player attackers**.
- Close threats inside 55 m retain the detailed rig and physical interaction. At 55–155 m the detailed rig is camera-relevant only; unseen rigs have their visuals/animation graphs disabled. Previously built hidden rigs are retired after a sustained absence and restored on return.
- Camera-facing 155–485 m targets use the shared lightweight unskinned LOD without shadows. Beyond 485 m no rival representation is admitted. Missing/disabled camera does not falsely admit distant renderers.
- Offscreen 90 m+ rivals can run virtual movement/combat with the heavy physical body disabled; 185 m+ uses virtual movement even if visible, with an optional visible hit receiver for scoped player shots. Native receiver admission and eviction are independent of the 0.35-second distant AI tick.
- Remote mathematical fighting updates the **real** rival health and elimination hooks but is restricted to hidden distant opponents. It never synthesizes damage to the player or to a camera-visible victim. Virtual movement remains terrain/zone/obstacle aware.
- Android distant AI ticks are distributed across bot slots. Camera sweeps may wake presentation but do not create extra remote combat or loot ticks. Expensive distant animation, humanoid skinning and gun meshes are not required for remote damage.

## Reproduced checks

| Check | Observed result |
|---|---|
| `python3 -m unittest discover -s Tools/Validation -p 'test_offline_rival_*.py' -v` | **40 tests, PASS** |
| `bash Tools/Codespaces/combat_acceptance_gate.sh --full` / combat source contracts | **63 tests, PASS** |
| Native Unity source-only compilation | **PASS; 98 game scripts, 0 C# errors** |
| Native PhysX combat integration | **PASS** (both damage directions, two-player-attacker cap, real remote virtual elimination, no imaginary player damage) |
| Real saved `TerrainDemoScene.unity` 100-bot soak | **PASS; 100/100 spawned on first pass**, 15-second requested loaded sample, real damage, four optical scope tiers, stance-aware hits and cover |
| Separate real-scene rig retirement and re-entry | **PASS; detailed humanoid and native hit receiver restored** |
| QA working-tree protection | **PASS; worktree unchanged**, existing 13 modified tracked files retained |

Isolated `AarisBotFrameBenchmark` (no real map meshes, Humanoid GPU workload or Android):
- Baseline main-thread mean **0.219 ms/frame**; 100 logical bots **0.320 ms/frame**, delta **0.101 ms/frame**.
- **100/100** rivals moved; **97/97** benchmark distant bodies were virtualized.
- GC mean baseline **704.0 B/frame**, 100-bot sample **705.3 B/frame**. These include Editor/harness allocations and **do not** prove game-wide zero GC.

The short real-scene Editor run also logged a `UnityEditor.Search.SearchDatabase` indexing `ArgumentOutOfRangeException` from Unity's editor subsystem; the native combat acceptance gate nevertheless finished **PASS**. Do not conflate this editor indexing problem with a C# compile error or silently claim that no editor diagnostics existed.

## Boundaries and reproduction

These are **Linux Unity Editor** checks, **not** Snapdragon 680/Adreno 610 Android FPS, GPU milliseconds, memory, 1%-low FPS or temperature results. Hidden distant models eliminate their model/shadow render work, but the continued virtual fighting necessarily uses some CPU. On-camera distant shared LODs deliberately still cost a small amount of GPU. Real-device Development Build profiling is required before claiming an Android zero-lag or zero-GC result.

Re-run the isolated native benchmark:

```bash
export TMPDIR=/tmp
xvfb-run -a -s '-screen 0 1280x720x24' \
  /home/vscode/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity \
  -batchmode -projectPath /workspaces/Unity-Game-Aris \
  -executeMethod Aaris.MapReview.Editor.AarisBotFrameBenchmark.Run \
  -logFile /tmp/aaris-bot-frame-benchmark.log
cat /tmp/aaris-bot-frame-benchmark/result.txt
```

Re-run the complete native combat gate:

```bash
bash Tools/Codespaces/combat_acceptance_gate.sh --full
```

All temporary logs/reports stay in `/tmp`. This validation did not create a new git worktree or reset, stash, or delete other agents' local work.
