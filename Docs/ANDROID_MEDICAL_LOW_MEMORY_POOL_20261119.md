# Android critical-memory medical/helmet cache eviction (2026-11-19)

## Source-verified allocation

The original tactical HUD streams a maximum of eight active medical/helmet
3D pickup roots and saves inactive visual hierarchies into two small stacks
for allocation-free reuse. Both pools already release their children when
the scene is destroyed, but unlike vehicle/bike caches, they did **not**
react to Android's Application.lowMemory event during a match.

Inactive cached pickups are never needed for collection or visibility and
can release memory when the OS signals pressure.

## Gameplay-preserving fix

OnEnable/OnDisable subscribe and unsubscribe to Unity's low-memory event.
The callback ONLY flips a boolean: it never calls Destroy, GC or anything
blocking on the OS notification thread.

The next Update begins a 60-second recovery window. During that window:
- release no more than two **inactive pooled root objects per frame**;
- retain all activeMedicalSites and their real visible pickups, collision
  checks, health and inventory semantics;
- recycle subsequent out-of-range/taken objects by destruction rather than
  immediately refilling caches; return to ordinary pool reuse afterwards.

The existing medical/helmet source meshes, geometry, materials, colours,
shader choices and authored site data are unchanged. Unity Destroy normally
takes effect after the frame; this does NOT guarantee instant system RAM
reclamation or eliminate crashes caused by other sources.

## Validation limits

Static and deterministic checks:
- python3 Tools/Validation/test_android_medical_pool_low_memory.py
- python3 Tools/Validation/check_battle_hardening.py
- python3 Tools/Validation/test_android_medical_candidate_scan.py
- python3 Tools/Validation/test_android_frag_visual_pool.py
- python3 Tools/Validation/check_mobile_hotpaths.py

Real Android Unity compilation and before/after RAM, p95/p99 frame, LMK,
ANR and crash tests are still required. This is a targeted safety/peak RAM
recovery improvement, not evidence that 3–30 second stalls are cured.
