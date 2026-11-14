# Character visual upgrade — Unity QA (14 November 2026)

Tested with Unity 6000.3.26f1, real TerrainDemoScene character rig and the
in-game stance/movement/animation systems. The seven JPEGs show actual
Unity PlayMode camera readbacks, but deliberately use an actor-only render
with a neutral temporary floor rather than the full 2 km map terrain shader.
QA camera and ground are restored or destroyed after each photo.

## Verified

- Rig: Bamen military soldier, 78 joint humanoid, ~11,104 triangles,
  one skinned mesh with 11 authored PBR material slots, ~0.9MB FBX.
- New primary Resources model, old character remains fallback.
- Native SUCCESS_7_ACTOR_PORTRAITS after standing, crouch, prone,
  movement, jump takeoff and landing frames.
- Native low-stance headroom:
  crouch head 6.1 cm below controller top; prone head 10 cm below top.
- Android APK preflight only passed for license, scene, SDK, NDK and JDK.

## Still unverified

- Real 2GB Android device FPS, GPU power, thermals, installability; there
  was no connected physical device (adb devices returned empty).
- Fully photorealistic lighting/materials: this is improved low-poly PBR.
- Full-map frame rate, extreme slope stances and touchscreen edge cases.

Model: FREE [Military Soldier] RIGGED by BAMEN, CC BY 4.0.
See Assets/Resources/AarisCharacters/Primary/RealisticSoldier/ATTRIBUTION.txt
for creator, source, license and conversion attribution.
