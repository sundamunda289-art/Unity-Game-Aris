# Aaris movement-only Locomotion Gym (Unity 6000.3)

This is a **separate, disposable Android QA build** for walking, sprinting, jumping,
standing/crouching/prone transitions and crawling. It uses the actual
`AarisPlayer.prefab`, `AarisThirdPersonPlayer`, `AarisBattleActions`,
and `AarisCharacterVisual`; the production map and its spawn systems
are not created. Its test environment is a few primitives (ground, steps,
ramp, low roofs, jump block). The original scene and prefab remain untouched.

## Build in an existing Codespace with Unity Android support

```bash
UNITY_BIN=/opt/unity/Editor/Unity bash Tools/CI/build_locomotion_gym.sh
```

Find `/tmp/aaris-locomotion-gym-output/AarisLocomotionGym.apk` and
`unity-gym-build.log`. The command stages a filtered Unity project under
`/tmp`, preserving dirty original files and expensive native map assets.
The staging project contains only runtime/editor C# code, one shared player
prefab, character + locomotion Resources, and tiny URP settings; it excludes
map terrain, buildings, vehicles and other optional Resource assets.
**Mono backend and ARM64-only** are intentionally used for quick testing;
the full production APK continues using its existing IL2CPP settings.
The gym has its own package name, `com.aaris.locomotiongym`.

Controls: move with the existing joystick or WASD; camera drag/right mouse,
Space = jump, T = sprint, C = crouch, Z = prone, R = stand.
Touch **CROUCH / PRONE / STAND** at top right; the existing jump/sprint
controls are retained.

Before claiming successful gameplay, verify the real APK launches on Android
and test 8-direction motion, airborne/prone rejection, headroom under both roofs,
ramp descent, and foot contact at different FPS. Editor compilation/CI alone is
not an Android animation validation.

## Source and licensing

Aris's existing animation resources are reused; this patch does **not**
import new motion capture or claim to have retargeted ACCAD data.
For follow-up directional clips use the 100STYLE dataset by Ian Mason,
Sebastian Starke and Taku Komura (CC BY 4.0, attribution required).
For prone/crawl transition candidates review ACCAD / Ohio State Open Motion
Project (CC BY 3.0, attribution required). ALS-Unity's MIT code is a design
reference only until a specific portion is independently validated and
integrated. This Gym does not vend or incorporate those upstream repos.

References:
- https://www.ianxmason.com/100style/
- https://accad.osu.edu/research/motion-lab/mocap-system-and-data
- https://github.com/schyro/ALS-Unity

IMPORTANT: A single-scene build from the full project is not necessarily
small because Unity includes all assets under Resources. Always use the
filtered staging script for a genuinely lightweight QA build.
