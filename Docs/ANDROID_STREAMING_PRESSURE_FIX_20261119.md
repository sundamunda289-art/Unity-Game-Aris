# Android streaming pressure and GPU admission

Reviewed baseline: `863232dc264a19ab2bb80ac3df206b223e0ffbb3`.

## Source findings

These are verified scheduling gaps in source, not a measured diagnosis of a
particular phone's 3–30 second freezes. Current-device profiler/logcat evidence
is still required to separate CPU, GPU, memory pressure and driver failures.

| Trigger | Previous behavior | Revised behavior |
| --- | --- | --- |
| Adreno 610 / Mali-G52 with 6 or 8 GB RAM | Shared optional gate bypassed above 4608 MB despite the existing constrained GPU classification | Low RAM OR the existing exact GPU classification enables shared admission; policy cached once per subsystem |
| Optional Resources request is still running | New optional requests waited, but model hierarchy creation could proceed | Both optional requests and model builds wait behind the same in-flight request |
| Request completion observed | Another import/hierarchy could start on the frame that integrated the previous asset | That observed completion frame is reserved; retry next rendered frame |
| Previous frame exceeded 50 ms | New optional work could be admitted every rendered frame | At most two distinct attempt-frames defer, then one slot can proceed; a healthy frame resumes admission immediately |

The controller uses no per-frame device string matching, hierarchy searches,
new collections, logging, forced GC or synchronous disk access. Multiple callers
in the same frame share one pressure decision. Missing optional assets complete
their ResourceRequest normally and do not permanently retain admission.

## Gameplay and appearance

This change alters optional scheduling only. It makes no texture, mesh, LOD,
resolution, shadow, source scene, collider-shape or weapon-stat edits. Existing
GPU quality settings are not changed. Movement, shooting, essential weapon
preload, first-use sounds and starter-loot fallback keep their current direct
paths. Nearby static collider activation remains immediate for collision safety;
the possible dense-entry activation burst was observed but is not claimed fixed.

Optional vehicle/loot/rival models and Foley variants may become ready later
under sustained load. This trades loading latency for less concurrent work;
an individual Unity Instantiate or essential import can still exceed a frame
budget. This gate is not a guarantee of stable FPS or a GPU-driver crash fix.

## Verification

- 276 Android source/analyzer regression tests passed.
- All 34 existing mobile gameplay workflow Python checks passed.
- All 38 gameplay C# sources parsed successfully; hot-loop source guard passed.
- Entire production gate plus actual GPU-name detector executed in controlled
  C# API fixtures: Android 2727 assertions, Editor 401, desktop 401; all passed.
  Cases include 6/8 GB weak GPUs, stronger GPU prefix collisions, native device
  query caching, pending and completed imports, 300 slow frames with eight
  callers per frame, recovery, rig reservation, cleanup and subsystem reset.
- A pre-existing profiler-test false positive (`291 < 250`) was reproduced
  against the baseline test. It now checks the actual synchronous method body
  for the scope and rejects coroutine yields instead of limiting signature
  character length.
- Native Unity project compilation, Play Mode, visual A/B and physical Android
  frame-time/PSS tests were not run for this patch. No APK was built.

## Phone evidence

Existing `AarisDiagnostics/current.jsonl` gains
`optionalStreamConstrained`, `optionalStreamImportSettleDeferrals` and
`optionalStreamPressureDeferredFrames`. Existing import deferrals now cover
both request and model-build attempts. The offline diagnostics analyzer reports
only sanitized policy/counter summaries; counters are not durations or proof of
causation, and absent evidence stays unknown.

For device acceptance, compare the same route, camera, guns and graphics
configuration in cold/warm matches, including village entry, fast driving,
combat and app resume. Compare p95/p99 frame times, explicit stalls, CPU/GPU
timings and Android PSS/logcat; inspect close guns and map clarity visually.
