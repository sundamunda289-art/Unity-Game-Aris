# Third-party notices

This project is an independent Unity prototype. It is not affiliated with or endorsed by the authors of
Advanced Locomotion System, ALS-Community, Epic Games, Quaternius or Mesh2Motion.

## 1. Source code: ALS-Community (MIT)

- Project: Advanced Locomotion System - Community Version (`ALSV4_CPP`)
- Reference repository: <https://github.com/PanicPetal/ALS-Community> (commit `d044fcd`, 2024-07-11),
  a copy of <https://github.com/dyanikoglu/ALS-Community>
- License: MIT (source code only)

The C# scripts under `Assets/ALS/Scripts/Runtime` are a new Unity implementation, but their structure,
state model, tuning values and parts of their comments are translated from the C++ sources of ALS-Community:

| Unity script | Based on |
|---|---|
| `ALSCharacter.cs`, `ALSMovementSettings.cs`, `ALSEnums.cs` | `ALSBaseCharacter`, `ALSCharacterMovementComponent`, `ALSCharacterEnumLibrary`, `ALSCharacterStructLibrary` |
| `ALSCharacterAnimation.cs` | `ALSCharacterAnimInstance` (essential values, lean, turn in place, foot IK) |
| `ALSCameraController.cs` | `ALSPlayerCameraManager` |
| `ALSMantle.cs` | `ALSMantleComponent` |
| `ALSMath.cs` | `ALSMathLibrary` and the Unreal `FMath` interpolation helpers it relies on |

`CalcVelocity` / `ApplyVelocityBraking` in `ALSCharacter.cs` re-implement the ground velocity integration that
ALS relies on (Unreal Engine's character movement behaviour) for Unity's `CharacterController`; no Unreal Engine
source code is included.

ALS-Community license, reproduced as required:

```
MIT License

Copyright (c) 2020 Doğa Can Yanıkoğlu & LongmireLocomotion as developer of
the blueprints used as reference for source code

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### What was deliberately not taken from ALS-Community

The repository's `Content` folder (mannequin, animations, animation blueprints, curves, sounds, maps) is **not**
covered by the MIT license above: the README of ALS-Community licenses the source code only, and the content
comes from the Advanced Locomotion System V4 marketplace asset and Epic Games' mannequin. None of those asset
files are included here, converted or otherwise. The numeric tuning defaults (gait speeds, acceleration /
friction and rotation rate curve keys, mantle trace sizes) follow the values ALS ships with.

## 2. Character and animations: Quaternius (CC0 1.0)

Files: `Assets/ALS/Art/Quaternius/Mannequin_UAL1.fbx`, `Assets/ALS/Art/Quaternius/Actions_UAL2.fbx`,
`Assets/ALS/Art/Quaternius/Locomotion_UAL1_Backward.fbx` (derived from `Mannequin_UAL1.fbx`, see section 4)

| Pack | Source | Used clips |
|---|---|---|
| Universal Animation Library [Standard] v3.0 | <https://quaternius.itch.io/universal-animation-library> / <https://quaternius.com/packs/universalanimationlibrary.html> | mannequin mesh and rig, `Idle_Loop`, `Walk_Loop`, `Jog_Fwd_Loop`, `Sprint_Loop`, `Crouch_Idle_Loop`, `Crouch_Fwd_Loop`, `Jump_Start`, `Jump_Loop`, `Jump_Land`, `Roll` |
| Universal Animation Library 2 [Standard] | <https://quaternius.itch.io/universal-animation-library-2> / <https://quaternius.com/packs/universalanimationlibrary2.html> | `ClimbUp_1m`, `LayToIdle` |

- Author: Quaternius (<https://quaternius.com>)
- License: CC0 1.0 Universal, as stated on the pack pages and in the `License.txt` bundled with each
  download (copied to `Assets/ALS/Art/Quaternius/License.txt`).
- Downloaded on 2026-10-02 from the free "Standard" tier on itch.io. SHA-256 of the downloaded archives:
  `cc73fc4e495b82958207316596317a3f40b9fa38065bde1027937452da537724` (UAL1) and
  `4008ea208a604773a2b2177d965f0f5d3195498b5bf838c3f5785d68e95f2a68` (UAL2).
- Note: quaternius.com has since introduced a separate "Quaternius Asset License" for some newer packs. The
  two packs above were published and obtained under CC0; re-check the license before replacing these files
  with a newer download.

## 3. Animations: Mesh2Motion (CC0 1.0)

Files: `Assets/ALS/Art/Mesh2Motion/M2M_Addon.fbx`, `Assets/ALS/Art/Mesh2Motion/M2M_Mocap.fbx`

- Project: Mesh2Motion (<https://github.com/Mesh2Motion/mesh2motion-app>), commit
  `79f3f61a9852ef70234a5a4a7c13ed87f7a71833`
- Source files: `static/animations/human-addon-animations.glb` (`Strafe_left`, `Pushup`; the FBX
  also holds `Strafe_right` and `Walk_Backwards`, which are not imported) and `static/animations/human-mocap-animations.glb` (`Turn_Left_90`,
  `Turn_Right_90`, `Turn_Left_180`, `Turn_Right_180`)
- License: the repository states that all 3D models, rigs and animations are CC0 1.0 Universal
  (`LICENSE-CC0.MD`, copied to `Assets/ALS/Art/Mesh2Motion/License.txt`). Its code (MIT) is not used.

## 4. Modifications to the art files

The four FBX files were produced from the original GLB files with Blender 5.2 using
`Tools/blender/convert_glb_to_fbx.py`: only the clips listed above were kept, the unused `*_leaf` end bones
were removed, a keyed rest pose (`A_TPose`) was added as the first take, the rig was turned to face +Z and the
result was exported as FBX. No animation data was edited in these four files.

`Locomotion_UAL1_Backward.fbx` holds three clips derived from `Mannequin_UAL1.fbx` with
`Tools/blender/make_backward_clips.py`: `Walk_Bwd_Loop`, `Jog_Bwd_Loop` and `Crouch_Bwd_Loop` are `Walk_Loop`,
`Jog_Fwd_Loop` and `Crouch_Fwd_Loop` reversed in time and shifted by half a cycle, with the forward lean of the
spine reduced (walk and jog) and the neck and head counter-rotated. They remain under CC0 1.0.

`Tools/blender/README.md` lists the exact commands.

## 5. Everything else

- `Assets/ALS/Art/Textures/T_Grid.png`, the materials, the label shader, the test scene, the prefab and the
  Animator Controller are generated by the editor scripts in this repository and are covered by the project
  license.
- `.gitignore` and `.gitattributes` are the Unity templates from `github/gitignore` (CC0 1.0) and
  `gitattributes/gitattributes` (MIT).
- Unity packages (Universal Render Pipeline, Input System, ...) are referenced through
  `Packages/manifest.json` and downloaded by the Unity Package Manager under their own licenses; they are not
  redistributed here.
