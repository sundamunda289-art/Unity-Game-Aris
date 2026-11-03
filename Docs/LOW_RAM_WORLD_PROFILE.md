# Android 2–3 GB world rendering profile

This is a **runtime** optimization of the saved 2 km × 2 km four-terrain map. It does not rewrite authored terrain, house, tree, rock, collider, prefab, or .meta files.

## Profile selection
On a real Android player (not in the Unity Editor), `AarisMobileRuntime` reads reported system RAM. Unknown RAM and devices above 3584 MB retain the original settings. The serialized `tuneForLowRamAndroid` field allows disabling the optimization after device comparison.

| Field | Saved scene | RAM ≤ 2560 MB | RAM 2561–3584 MB | Low-memory event |
| --- | ---: | ---: | ---: | ---: |
| Mip streaming memory cap (MB) | 384 | 96 | 160 | 64 |
| Grass / detail view (m) | 100 | 45 | 70 | 30 |
| Detail density | 0.65 | 0.42 | 0.55 | 0.32 |
| Billboard transition (m) | 100 | 60 | 85 | 45 |
| Maximum full-LOD trees | 50 | 18 | 32 | 12 |
| Terrain pixel error | 5 | 10 | 7 | 12 |
| Basemap distance (m) | 400 | 300 | 350 | 260 |
| Tree visibility distance (m) | 600 | unchanged | unchanged | unchanged |

All values are *limits*: the runtime does not increase preexisting quality settings. Source foliage is preserved. A low-memory notification also invokes `Resources.UnloadUnusedAssets()` with its existing cooldown. The per-tier streaming cap only governs textures with streaming mipmaps enabled; it does **not** cap all GPU texture memory.

Physics spatial indexing no longer calls `Physics.SyncTransforms` on every idle periodic check. It still synchronizes whenever collider activation changes and for explicit `RefreshNow()` calls such as player resets.

## Still to validate in the Unity Editor / on devices

- Actual phone p95 frame time, average FPS, Android PSS memory, texture memory and thermal behavior at 2 GB and 3 GB; use `AarisDiagnostics/current.jsonl` and compare the `graphicsTier` / `mipStreamingBudgetMB` fields. **60 FPS is a target, not a verified claim.**
- Camera clarity, foliage popping, mip texture softness, roof-to-terrain contact, mountain silhouettes, shadow quality and entrances, with side-by-side screenshots.
- The saved building and native terrain tree meshes are large LFS assets. Their true rendered trunk/canopy/door/window/collision dimensions require materialized geometry and an engine capture/probe before geometry changes. A mesh LOD bound is not a visible trunk diameter.
- Human controller reference: 1.78 m height, 0.28 m radius, 0.30 m step and 1.0 m jump. Candidate authoring guidelines (not audited dimensions): doors ~0.9–1.1 m clear width and ~2.0–2.2 m height. For vaultable windows, verify a genuinely open gap and collision clearance; decorative closed-glass windows should remain nontraversable.
- The earlier map layout reports 59 buildings and a minimum pair separation of approximately 21.5 m, but historical reports may be stale. Village clustering, door access, and trees beside roads need paged `site` / `describe` inspections followed by Unity physics and camera tests, not blind random relocation.

See `AGENTS.md` and `Docs/WORLD_AUTOMATION.md` for source-hash transaction, LFS, placement and engine-verification rules. No model downloads are included here; importing an external model requires asset rights, explicit source units, source hash and an import/LOD/collision check.
