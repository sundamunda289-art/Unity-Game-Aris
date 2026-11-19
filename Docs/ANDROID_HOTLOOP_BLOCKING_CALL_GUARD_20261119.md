# Android hot-loop blocking-call regression guard — 2026-11-19

Goal: Prevent obvious avoidable frame-time stalls from entering Unity 6 offline Android gameplay scripts in future changes. This is a source-level CI rule, not proof that the original freezes are solved or that a phone was profiled.

## Audited findings

At the audit baseline, 35 gameplay scripts and 38 Update, LateUpdate, FixedUpdate, or OnGUI callbacks contained no direct calls matching the prohibited patterns. Some existing work uses asynchronous asset loading, paced HUD rendering, and resource caching. This does not establish that indirect helper calls, GPU rendering, or driver operations are inexpensive.

Unity documents that asynchronous asset loads read and deserialize on background threads but integrate objects on the main thread, while managed garbage collection can introduce frame stalls. Android logcat, Unity Profiler CPU/GC Alloc samples and real device traces are still required to attribute a freeze.

## Automated CI contract

Tools/Validation/check_android_hotloop_blocking_calls.py checks direct frame callbacks for synchronous Resources.Load or LoadAll, forced GC.Collect, blocking waits, synchronous disk I/O, and scene-wide object searches. It tracks nested braces and expression-bodied callbacks, skips comments and C# quoted strings, and reports source line numbers.

It deliberately allows Resources.LoadAsync and loading in Awake, Start, and event/transition handlers. It does not ban Resources.UnloadUnusedAssets in the existing low-memory path, which is separately paced and tested.

Tools/Validation/test_android_hotloop_blocking_calls.py contains 10 adversarial tests covering the real source, nested bodies, line numbers, banned calls, legitimate async requests, comments and string literals, and error detection. The Aaris Mobile Gameplay and Hotpath Regression workflow executes both scripts on PRs and main.

## Scope and limits

This guard does not follow the C# call graph, evaluate all conditional compilation branches, measure native allocations, memory or FPS, compile Unity, or build APKs. Future improvements should address actual measured bottlenecks without downgrading shaders, terrain, textures, mesh quality or game behavior.

Only tooling, CI and documentation files change. No runtime gameplay or visual assets change.

Official Unity references:
https://docs.unity3d.com/kr/6000.0/ScriptReference/Application-backgroundLoadingPriority.html
https://docs.unity3d.com/kr/6000.0/Manual/performance-managed-memory-introduction.html
