# Weapon & Ground Loot visual QA — 2026-11-21

These are actual Unity Editor Play Mode screenshots captured from `Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity` using Unity **6000.3.26f1** on the project's Cloudflare-connected Codespace. They are not AI-generated or promotional mockups.

- `ground-loot-before-after.jpg`: same weapon and camera angle in the same Play Mode run. Left temporarily reproduces the previous outdoor root clearance (4 cm); right restores the new 28 cm outdoor presentation clearance. Indoor ground clearance remains at 4 cm. The QA routine restores the source gun transform after each view.
- `held-dmr-side.jpg`: real runtime Precision DMR held-gun side screenshot, after allowing normal Unity animation/IK frames to advance.

## Checks

- Native Unity ground loot test: exit 0; zero C# compilation errors; 3 active nearby pickups; 0 invalid-size models. Two different screenshot files: 84,415 bytes and 84,351 bytes. Pixel differences verified; this is visual evidence, not a guarantee of collision clearance on every terrain point.
- Native six-weapon gun test: exit 0, zero C# compilation errors; SMG, Assault Rifle, Battle Rifle, Sniper, Shotgun and DMR each accepted firing and reload in Play Mode.
- Python/CI: Weapon Regression, Loot Spatial Streaming, Mobile Gameplay/Hotpath, and Map Explorer checks passed on feature commit `cb36d92e`.
- The DMR intentionally reuses a locally packaged licensed rifle model with a distinct semi-auto balance profile.
- Bullet tracers use one reused LineRenderer per equipped gun. Damage registration remains hitscan; this is **not** true projectile-flight ballistics.

Limitations: Pixel color/variance and scripted Play Mode QA are not equivalent to human Android touch gameplay testing. Snapdragon 680/Adreno 610 FPS and per-shot GC allocations have not been measured on a physical phone.