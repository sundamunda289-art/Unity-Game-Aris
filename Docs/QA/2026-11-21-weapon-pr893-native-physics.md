# Native Unity ground-loot support regression — PR #893

Date: 2026-11-21. Editor: Unity 6000.3.26f1, Linux Codespace, Unity Personal authentication and license verified. Scene: temporary in-memory native PhysX test; no game scene or asset saves.

## Root cause
`AarisWeaponLootWorld.TryResolve` previously selected the ray hit with the smallest absolute distance from native terrain height. A TerrainCollider has zero height difference, so a road 13 cm above terrain lost selection even when its collider was valid. A later terrain-vs-solid clearance check alone could not repair the selected hit.

## Reproduced failure
Initial PR implementation; native Unity Editor process **exit 1**:
```
UNITY=6000.3.26f1
GRASS_ROOT_Y=0.280
ROAD_ROOT_Y=0.280
RESULT=FAIL
InvalidOperationException: ROAD_COLLIDER_LOST_TO_UNDERLYING_TERRAIN
```

## Verified repair
Latest PR revision `ceb3dacee9871ee94bf82c4d7b2a7188257adcde` uses the same native test on an isolated `/tmp` project. **Exit 0**, zero `error CS` lines:
```
UNITY=6000.3.26f1
GRASS_ROOT_Y=0.280
ROAD_ROOT_Y=0.170
INDOOR_ROOT_Y=0.170
RESULT=PASS
```

The test builds actual `TerrainCollider`, 13 cm elevated `BoxCollider` road and ceiling in an unsaved Editor scene. Near-grade static support must outrank terrain, but only within -0.02 m to +0.22 m of sampled terrain. Terrain grass retains 28 cm visual root elevation; hard surfaces/indoor retain 4 cm. The pre-existing 2.5 cm exact-mesh clearance is unchanged.

A committed regression lives in `Assets/AarisWorld/Editor/AarisWeaponSupportPhysicsQA.cs` (and its Unity .meta). Python `Tools/Validation/test_weapon_floor_support_clearance.py` passed all five cases. Weapon Regression, Loot Streaming, Mobile Gameplay/Hotpath and Map Explorer CI were green at `ceb3dacee9`.

## Actual earlier Unity grass photographs (baseline; not PR visual validation)
- [GroundLoot_Before_Site_1.jpg](https://github.com/sundamunda289-art/Unity-Game-Aris/blob/main/Docs/QA/2026-11-21-weapon-ground-loot/GroundLoot_Before_Site_1.jpg)
- [GroundLoot_After_Site_1.jpg](https://github.com/sundamunda289-art/Unity-Game-Aris/blob/main/Docs/QA/2026-11-21-weapon-ground-loot/GroundLoot_After_Site_1.jpg)

These are **real Unity Play Mode screenshots from a prior main validation**. They show that grass height matters, but **do not depict the PR #893 hard-surface changes**.

## Gates not yet satisfied
- No genuine new PR #893 URP Play Mode screenshots of road and indoor loot, held gun, ADS, muzzle flash or reload.
- No Android-device FPS or GC measurements. The Codespace OpenGL renderer is Mesa llvmpipe, not Adreno 610.
- The full isolated project copy under `/tmp/aaris-weapon-pr893-native-20261121/Project` disappeared between the successful Physics pass and attempted graphical run; Unity then failed to open the project path. Do not interpret these path failures as gameplay failures or visual passes.
- The live Codespace contains 23 tracked dirty files and 82 untracked files belonging to other agents. Do not reset, stash, clean, or overwrite them.

**Status:** Native PhysX and C# compile gate passed; visual gate pending. Keep PR Draft and unmerged until genuine visual evidence is available.
