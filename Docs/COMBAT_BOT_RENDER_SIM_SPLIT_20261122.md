# 100-bot distance virtualization: GPU/PhysX split (2026-11-22)

## Existing baseline preserved

`AarisOfflineBRDirector` owns 100 independent HP/kill-feed/zone participants, staggered spawns, two-player-attacker reservations, target selection, medkits, finite gun ammunition and `TickVirtualCombat`. Beyond 90 m, decisions and movement are time-sliced at 0.35 s per rival, phase-distributed on Android. Far bot-vs-bot damage is approximate, rather than a replay of actual bullets/line-of-sight; nearby fights still demand real colliders and clear LOS.

Existing render budget: humanoid rigs inside 105 m (shown to 155 m), a shared non-skinned distant visual visible within 485 m in the camera frustum, and no visual beyond. Hidden humanoid rigs beyond 190 m retire after five seconds. This patch does **not** reduce the close rig's meshes/materials/texture quality or lower the 100-bot count.

## Corrected expensive behavior

Before: camera relevance at 185–485 m prevented `virtualBody`, leaving a distant scoped bot with a live `CharacterController` and full `Move`/LOS/cover processing when its shared unskinned LOD was sufficient.

After:
* Above 185 m, **always** enter math simulation even if the camera can see the bot. Between 165 and 185 m, prefer physics for a currently visible near combatant and virtualization if off-screen. Exit virtualization at 145 m, or under 165 m when visible. The differing bands prevent repeated PhysX component re-registration.
* A cheap, disabled-by-default `CapsuleCollider` is created during staggered spawn and enabled only for distant on-screen/scoped targets within 485 m, or near the 220 m physical-combat boundary. `CharacterController` is disabled for virtual bots, so no movement queries or per-frame physics movement are performed for remote scoped targets.
* The far visual remains one shared, instanced, shadow-free 3D mesh (not a humanoid rig). Beyond 485 m, off-camera, no bot mesh is enabled. Inside 155 m, rigs farther than 48 m are also hidden when offscreen, while close-combat rigs remain ready to prevent popping during immediate contact.
* Imported `SkinnedMeshRenderer.updateWhenOffscreen` is now explicitly false; `AnimatorCullingMode.CullCompletely` is set even on rigs without valid playable clips. This removes unnecessary animation/skinning work when renderers are not visible.
* All existing bot health, remote hit chance/ammo/reload/movement, zone damage, kill-feed reports, two player attacker slots and shot damage receivers remain in use. Scope hitboxes reuse the existing parent-health hierarchy and do not instantiate projectiles.

## Validation boundaries

The deterministic `Tools/Validation/test_bot_render_sim_split.py` checks the above thresholds in a 100-bot model, and the mobile GitHub Actions workflow parses runtime C# and executes existing combat/LOD source contracts. These are **static / simplified policy tests**, not Unity native compilation or Snapdragon 680 measurements.

**Do not claim zero CPU** for simulated bots: math combat, transforms, health and scheduling necessarily cost CPU. Zero render submission for culled bot meshes is the goal; actual GPU workload and FPS must be measured by the Unity Profiler on a physical device.

### Required real-device acceptance

Run the Unity 6000.3.26f1 Editor compile/Play Mode, the full 100-bot integration soak and a Development APK on Snapdragon 680 / Adreno 610. Inspect 1% low FPS, Main Thread/P95/P99, skinned mesh updates, shadow passes, physics callbacks, GC/frame, thermal trend and memory. Confirm that a far visible target can be hit by 4x rifle, that off-camera rivals still eliminate each other, that close transitions do not clip through houses, and that prone/crouch player hit positions still come from the live player `CharacterController` center. No APK FPS conclusion is possible from static tests.
