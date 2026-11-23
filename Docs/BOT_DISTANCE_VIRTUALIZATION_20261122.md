# 100-bot distance virtualization — 2026-11-22

Target: Unity 6000.3.26f1, Android URP, 100 offline rivals, 2x2 km terrain.

## Runtime policy

- Preserve the existing 100 logical rival slots, health/death events, zone damage, loot and exactly-two player attacker selection.
- Keep the player-adjacent 90m full-frequency physical AI. Distant AI remains time sliced (0.35s cadence, phased by slot on Android).
- Cache the main camera. Offscreen distant 155-485m substitute meshes are deactivated, not merely hidden by camera frustum. 485m+ rivals get no authored visual. Existing close-up realistic rigs remain intact.
- Cull offscreen Humanoid Animator completely. Retire inactive expensive rigs as before. Camera relevance is probed every 0.12s per staggered rival, and never forces full-frequency distant AI.
- At >185m and outside the camera, disable the CharacterController and use terrain-aware, obstacle-index-aware mathematical movement, zone retreat, ammo/reload, medkits and lightweight rival-versus-rival damage. Restore collider by <=165m or on-camera for real hitscan/Red-Lock physics. Virtual combat never inflicts unphysical player damage or on-camera damage.
- Remote mathematical kills use the existing authoritative health/death callbacks, Alive count and an on-screen recent-kill text. Near fights retain normal physical LOS, cover, gun and 1-2 player attacker cap.

## Verified in connected Codespace

- Unity Editor batch QA: RESULT=PASS; both real-hit paths, Red-Lock, 100-slot selection, cover, 2 attacker limit, plus new remote rival elimination/damage attribution/no physical capsules/no imaginary player damage.
- Camera-instrumented 100-bot Linux Editor benchmark: RESULT=PASS; 100/100 moved; 71 of 97 remote-body candidates virtualized; 1.300s simulated; baseline CPU main mean 0.239ms, 100 bots mean 0.344ms (extra 0.105ms/frame). Baseline and loaded samples include Editor overhead.
- C# compiler errors: none during these Editor runs. Warnings in unrelated files remain.

These are isolated Linux Editor tests, **not** a full-terrain GPU or Snapdragon 680 Android FPS measurement. They do not prove zero total CPU load, or guaranteed smoothness on any phone. In particular, on-screen rivals intentionally keep collision/rendering cost for real player hit detection. On-device Development Build profiling is required before claiming final FPS/thermal wins.

## Camera/simulation separation follow-up — 2026-11-23

Previously a freshly visible 90m+ rival bypassed its 0.35-second math-AI gate in order to activate its renderer. That also advanced `lastSimulationTime`, reset `nextFarSimulation`, and executed extra abstract combat when quickly sweeping the camera over many bots.

The camera-entry fast path now admits only presentation and any required physical bullet receiver. Movement, AI decisions, ammunition, damage, and remote kill attribution are executed **only when the existing simulation tick is due**. Camera exits shut down far LOD meshes, offscreen 55m+ Humanoid rigs/PlayableGraphs and virtual-body hit receivers immediately on the staggered 0.12-second visibility probe, rather than retaining them until the next 0.35-second AI tick. The <55m close-threat exception and 165/185m collision hysteresis remain intact. No bot count, damage formula, remote combat frequency, scene or visual asset is changed.

`Tools/Validation/test_offline_rival_distance_budget.py` includes a 100-agent 30-FPS camera-sweep model that checks AI tick parity and source-contract assertions for early native culling. Static tests cannot prove actual Android CPU/GPU frame time. The safe-sync workflow must reject any overlap with other agents' tracked or untracked files; do not reset, stash, clean, or force checkout to bypass that protection.

## Immediate native eviction on distance transition — 2026-11-23

Previously, turning away from a close rival while backing across 55m could
leave a full Humanoid active until the next 550ms visual tick if the rival was
*already* marked camera-irrelevant. Likewise, leaving 485m in a vehicle or
after a teleport could retain an active distant mesh, stopped/not-yet-stopped
animation, or stationary hit receiver until a later simulation/visual tick.

The 120ms camera probe now evicts offscreen 55m+ full models even without a
fresh camera state edge. Exiting 485m evicts the local model, far LOD, playable
graph and any physical hit receiver immediately on the next 100ms dormant
proximity probe (or first active Update); the 350ms AI tick and abstract combat
damage remain independent and unchanged. The <55m close-threat exception,
165/185m hysteresis on return, physical scope-hit admission, authored model
quality, 100 logical slots, two player attacker limit and kill feed remain
unaltered.

The source-contract regression checks both paths. This is not an on-device
GPU/thermal measurement; native Unity compile and real Android profiling are
still required before claiming a complete fix.

## Hidden midrange frame budget — 2026-11-23

A 90-485m off-camera rival with a virtual body and no active renderers or
hit receiver skips unused render-frame distance/transform work until the
earliest 100ms proximity, 120ms camera, or 350ms simulation deadline.
Close physical encounters and all 100 logical fighters keep their combat
frequency, health, zone movement, kill feed and two-attacker limit.
Camera-only presentation no longer triggers an off-schedule loot attempt.
Tests cover the source contract and simulated tick parity; Android FPS and
thermal performance still require on-device profiling.

## Camera-visible far-bot native Update sleep — 2026-11-24

A visible 185–485m rival already uses 350ms abstract combat, shared unskinned
LOD and (when needed) a stationary physical bullet receiver. Previously its
MonoBehaviour still dispatched every rendered frame just to check deadlines.
The central scheduler now also owns these visible **virtual** actors, wakes
at the pre-existing ~120ms camera/proximity check or 350ms combat deadline,
and leaves the mesh and bullet collider active. The 90–185m physical band,
nearby <55m attacks, two player-attacker slots, 100 rival health slots,
zone, loot, kill-feed and authored 3D models are unchanged. An on-screen
far bot is *not* claimed to have zero GPU cost; an offscreen virtual bot
still consumes its time-sliced simulation CPU. Native compilation and Android
FPS/thermal measurements must be verified separately.
