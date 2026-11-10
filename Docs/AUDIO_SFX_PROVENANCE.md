# Aaris offline mobile audio (review branch)

## What is actually included
27 **original procedurally synthesized** mono WAV clips (22,050 Hz/16-bit) under `Assets/Resources/SFX/`, with deterministic sample generation and matching committed Unity `.meta` files. Source/verification: `Tools/Audio/generate_sfx.py`; run `python3 Tools/Audio/generate_sfx.py --check` after generation.

These waveforms were freshly generated for this project using sine components and shaped pseudo-random noise. **No third-party audio was downloaded**. The intended dedication is CC0 1.0 where applicable, but no external asset's CC0 status should be assumed. These lightweight clips are prototype-quality, not professionally recorded explosions, weapons, tyres, or aircraft. Replace them before a commercial-quality audio pass. For source recordings, Kenney RPG Audio (https://kenney.nl/assets/rpg-audio) and Impact Sounds (https://kenney.nl/assets/impact-sounds) list CC0 on the publisher's site; other Freesound/Opengameart items require per-file verification. Record provider, author, URL, license, and any edits when replacing a clip.

## Verified genuine CC0 foley samples

In addition to the 27 synthetic fallback cues, this branch now embeds **15 real recorded Kenney Impact Sounds OGG files** in `Assets/Resources/SFX/CC0/`. These are three mixed punch samples, one soft impact landing, one light metal collision, five grass footsteps and five concrete footsteps, each with a stable Unity AudioImporter `.meta` file. Their original publisher https://kenney.nl/assets/impact-sounds lists **Creative Commons CC0**. Source for the exact original binary samples is pinned to the read-only community mirror at https://github.com/GeorgeQLe/assets-sfx-combat/tree/9662dc1bed3b06ae753045915f9accfac044c7ce/assets/kenney/impact-sounds/Audio .

The controlled importer `Tools/Audio/import_cc0_kenney.py` pins that full commit and **checks each blob's exact Git SHA-1 against the approved allowlist**, as well as the OGG header and size, before committing to the review branch. No unverified CC-BY, NC, ND, branded or commercial-game recordings are included. GitHub Actions run 42548737813 imported and verified all 15 files. Kenney attribution is not required under CC0 but is acknowledged here.

`AarisAudioDirector` preferentially uses varied grass/concrete footsteps, punch thuds and appropriate impacts from the recorded CC0 pack; all other source categories fall back to the original lightweight synthetic WAV files. None of the audio is downloaded during gameplay. This is an honest audio baseline, **not yet an assertion of AAA sound quality**. For production guns, propeller plane, parachute wind, vehicle tyres and explosions, use individual independently verified real-world recordings with provenance.

## Event authority
`AarisAudioDirector` is added once to the saved third-person player after scene load. No second movement, vehicle, aerial drop, weapon, or grenade authority is created. It preloads clips and uses 12 reusable spatial one-shot sources plus separate, volume-smoothed loops for airplane, fall wind and car engine. The existing controller owns real footsteps, jumping, landing, parachute, combat fire rate and car torque. Audio follows only their events and measured speeds.

- **Drop:** InPlane engine loop -> Freefall whoosh + crossfade -> Opening canopy -> Landing touchdown. Wind pitch scales with measured falling speed.
- **Footsteps:** actual grounded displacement accumulates a walking/stance-dependent stride distance. One downward raycast per played step distinguishes named road/concrete, water and dirt surfaces; generic terrain defaults to dirt. Extend classification with accurate Terrain layers/physics materials after live inspection. Random pitch 0.92–1.08.
- **Combat:** successful ammo-consuming gun fire triggers its category cue, not a hold-button audio loop; hits trigger single impact cue. Punches, grenade throw, collision bounces and detonation trigger separate sounds.
- **Vehicle:** only while occupied, one engine loop tracks WheelCollider-driven speed, throttle and four simulated acoustic gear bands; lateral slipping or hard braking triggers skid; high-speed collision triggers bounded crash.
- **Spatial:** pooled real 3D AudioSources use logarithmic attenuation, minDistance 1.5 m and maxDistance 65 m; no run-time audio network requests.

## 2 GB Android performance target
The samples are embedded in the APK. Short events use `DecompressOnLoad` + ADPCM; 1.6-second airplane/wind/car loops use `CompressedInMemory` + Vorbis as specified in committed AudioImporter metadata. Unity transcodes at import/build; verify actual Android import settings, memory, DSP CPU, and voices in Unity Profiler. The cap is 12 concurrent event sources plus 3 loop sources; there is no AudioSource spawn per shot. Consider voice priority/attenuation after measurements, not guesses.

## Latest native Unity 6000.3.26f1 evidence

On GitHub Actions [run 42552997584](https://github.com/sundamunda289-art/Unity-Game-Aris/actions/runs/42552997584), the licensed Unity Editor imported all **42 AudioClip assets** (27 original WAV and 15 recorded CC0 OGG) with the exact marker `AARIS_AUDIO_NATIVE_42_CLIPS_IMPORTED`. The same isolated-worktree run independently verified **793** native Git LFS objects and returned `AARIS_AUDIO_NATIVE_COMPILE_EXIT_ZERO_NO_CS_ERRORS`. The source workspace and its unsaved edits were not reset or overwritten.

This verifies actual Unity importer compatibility and C# compilation, **not** playback in Play Mode, stereo directional positioning on a phone, or Android decoded memory/DSP budget. The independent main-scene 60-frame Play Mode probe currently fails with one **UnityEditor lifecycle ARRAY_INDEX** exception; do not call the game fully Play-verified or hide this failure.

## Verification gate
The generator workflow validates WAV format and deterministic hashes, **not C# compilation or genuine Unity audio playback**. Require on the exact review branch: successful Unity 6000.3.26f1 Editor import with zero C# compile errors; genuine native-map scene render and Play Mode, then phone headphone-direction/volume review and low-memory Android profiling. Keep native scene/TerrainData/prefab/mesh/`.meta` edits isolated and manually review before merging. A stale/blocked Editor process is not evidence of a visual fix.

## Provenance / release policy
Keep short, latency-critical SFX in the Unity project, not fetched from GitHub Releases at gameplay time. GitHub Releases/CDN may be considered for larger optional language or music packs, but they are not required for this fully offline single-player game.
