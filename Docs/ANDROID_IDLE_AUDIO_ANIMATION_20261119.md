# Idle audio and animation work — 19 November 2026

Baseline: main `438e8e0`. Source inspection found repeated native calls even
after three owned audio loops had stopped, and repeated writes of unchanged
rival animation mixer weights. Their handset frame-time share is not measured.

## Audio: retire only fully settled silent loops

The plane, wind and engine loop sources belong to `AarisAudioDirector`.
Previously, inactive branches called AdjustLoop every frame even after volume
was zero, pitch had reached its exact target and playback had stopped.

AdjustLoop now reports when a silent source has finished both exact targets.
Two managed settled flags then skip source reads/writes until flight or driving
becomes active again. Both flight-loop fades run independently; neither is
short-circuited while the other is fading. A new active phase clears its flag
before existing clip/play/fade work. Missing clips/sources count as silent.

The original MoveTowards rates, audible stop threshold, pitch targets, active
loop behavior, landing/start cues and OnDisable stops remain. A future system
that directly edits these private loop sources would also need to invalidate
their settled state. Other one-shot/footstep/weapon audio is untouched.

## Rivals: skip unchanged mixer weights, keep graph time

The rival's exact MoveTowards blend calculation is retained. Only the pair of
SetInputWeight calls is skipped when the next blend equals the current value.
There is no epsilon, frame-rate cap, clip replacement or graph pause. Existing
GameTime evaluation continues; graph construction explicitly resets the blend
to match its initial idle/run weights. Moving and transition blends still
update at the existing simulation cadence.

## Executed validation

The unit runner injects actual production methods/branches into controlled C#
fixtures. It launches neither Unity Editor nor an APK build.

- **109,199 checks passed**: fade output/playback parity with the old behavior
  at 10/15/30/60/90/120 FPS, zero-delta frames, missing sources/clips, full pitch
  convergence, repeated flight/drive entry, landing cue/state and blend parity.
- Across **216,000 settled quiet test frames**, the three loop sources required
  zero further source-property reads/writes in the controlled backend.
- **4,000 steady mixer ticks** added zero weight writes; 20,000 varying blend
  steps matched the original weights exactly. Graph-reset behavior passed.
- All 27 current gameplay CI source checks passed locally. Vehicle audio,
  footstep gait, rival-distance and profiler-marker regressions passed. All
  38 gameplay C# files passed syntax parsing.

Reproduce with system `mono-mcs`, or set `AARIS_UNITY_EDITOR_DATA` to an
installed Editor's `Data` directory, then run:

```bash
python3 Tools/Validation/run_idle_audio_animation_csharp.py
```

No map, tree, mesh, texture, shader, material, render scale or animation quality
was reduced. Controlled API fixtures do not prove Unity audio output, rendered
animation or Android FPS. Real-device profiling and freeze/crash attribution
remain separate acceptance work; no Unity/APK build or licensing operation was
performed for this change.
