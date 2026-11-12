# Offline gameplay sound effects

Unity packages 44 mono AudioClips under Assets/Resources/SFX. No clips are downloaded during play.

- 29 deterministic original WAVs use 22,050 Hz, 16-bit mono PCM. Tools/Audio/generate_sfx.py generates the clips and their AudioImporter metadata. Run it with --check to verify their bytes. The Gloo Wall deployment clip was created for this integration.
- 15 recorded Kenney Impact Sounds OGG clips provide grass and concrete footsteps, punch impacts and landing or metal impacts. The [publisher lists CC0](https://kenney.nl/assets/impact-sounds). The binaries were carried over unchanged from review/offline-mobile-audio-20261109. Their original blob hashes were checked in [run 42548737813](https://github.com/sundamunda289-art/Unity-Game-Aris/actions/runs/42548737813) against the [pinned sample mirror](https://github.com/GeorgeQLe/assets-sfx-combat/tree/9662dc1bed3b06ae753045915f9accfac044c7ce/assets/kenney/impact-sounds/Audio).

The saved player creates one audio director and uses the gameplay camera's AudioListener. Four local voices play accepted jumps, touch-downs, footfalls and the player's combat feedback without shoulder-camera attenuation. Eight pooled spatial voices play impacts, ricochets, crashes and explosions. Low-priority tails yield to transition cues.

The aircraft engine, speed-driven freefall wind, canopy flutter and vehicle engine use four loops. The wind drops briefly when the canopy opens so the deployment snap and fabric can be heard. On-foot and parachute touch-down have separate clips and triggers.

Unity's batch gameplay verifier checks clip imports, the saved camera listener and gameplay cue routing. Android speaker volume and sound quality require a phone playback test.
