# Android shared streaming integration budget — 2026-11-19

## Measured vs source-audited

Observed user symptoms: multi-second stalls, launch freezes and occasional Android home-screen exits on 4GB-class Snapdragon 680 hardware. These symptoms are not yet tied to a single Unity subsystem by an Android Profiler capture.

The source audit found a cross-system failure mode that isolated per-system limits do not prevent: AarisVehicleWorld can build vehicle hierarchies, AarisWeaponLootWorld can build loot hierarchies, and AarisRoadsideBikeScenery can initialize URP materials or build bike hierarchies in the **same rendered frame**. Each subsystem already had local budgets, pooling, and asynchronous source loading. When those budgets align, their native creation costs can still stack.

## Surgical low-end admission control

AarisMobileStreamingFrameGate gives optional native model/material setup in these three streamers a **single shared admission slot per rendered frame** when Unity reports 1–4608 MB of system RAM on an Android player. There is one integer frame marker, reset through SubsystemRegistration. No per-frame ticking, allocation, sorting, async job queue, display reduction, or new prefab. Desktop, Editor, Android phones with more than 4608 MB, and unknown RAM retain original model build behavior.

- Vehicle streaming validates a candidate and its parking/collision ownership, then attempts admission before Build. If another system claimed the current frame, it retries at the next normal 0.3s poll without evicting an occupied or parked original.
- Optional dropped/planned weapon visuals must claim before creating a GameObject. Declined requests return the existing pending-stream status, preserving the candidate, pickup, transaction and ammo state. The authoritative emergency starter-loot path is deliberately **not** throttled.
- Motorcycle source asset integrates asynchronously as before. Its shared URP material setup claims a slot only when the async request has finished. Recycled visible/pooled rigs remain immediate, and only a never-instantiated new bike needs a slot. A declined new-bike attempt replaces the 3s probe penalty with a 0.4s retry.

This bounds one class of cross-streamer model-integration coincidences on 4GB phones, **not** every native Unity allocation. Hero character initialization, terrain rendering, physics, GPU driver stalls and other systems may still dominate. The gate does not claim fairness across a nonparticipating renderer or guarantee FPS; the existing separate local budgets remain enforced.

## Validation

- New Tools/Validation/test_android_shared_streaming_frame_gate.py checks low-RAM condition, same-frame claim exclusivity, editor/large-phone bypass, static reset, vehicle ownership and no premature eviction, optional loot admission, emergency starter exemption, pooled bike reuse and model/asset contracts.
- Existing mobile loot frame pacing, roadside bike recycling, vehicle streaming priority and mobile hotpath tests continue unchanged.
- CI source tests and parser are not native Unity compilation, a real handset trace, or a claim of actual frame-time gains. Device testing must compare CPU Profile model-integration markers, stalled-frame counts, 1% lows and Android memory pressure before and after.

All original meshes, materials, water, foliage, movement and visuals remain authored. Only optional **timing** changes on constrained Android hardware.
