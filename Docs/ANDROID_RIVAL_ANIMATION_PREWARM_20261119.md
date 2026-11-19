# Rival humanoid animation first-spawn prewarm (2026-11-19)

Target Unity 6000.3 / Android 4GB Snapdragon 680 / Adreno 610.

## Verified path
The match controller loads a shared rival GameObject prefab before the first spawn,
but `ResolveRivalAssets()` still synchronously enumerated embedded Avatar
subassets and both idle/run clip imports during first rival creation. All six
rivals reuse those references. A first-spawn hitch is plausible, not confirmed.

## Narrow change
- Only on Android player builds: after the existing single-prefab async
  warmup, issue **one** `Resources.LoadAsync<AnimationClip>` for primary idle,
  wait for completion, yield a frame, then do the same for primary run.
  Both requests have low priority; never start more than one concurrently.
- Accept async clips only with the original humanoid, nonlegacy,
  positive-duration acceptance criteria.
- If `Resources.LoadAsync` does not return a usable imported FBX subasset,
  preserve `Resources.LoadAll<AnimationClip>` on first actual spawn, and the
  original primary-first then Kenney fallback order. No animation disappears.
- If the loaded prefab already has a valid humanoid Animator Avatar, reuse it;
  fall back to the original imported Avatar search only otherwise.
- No new animation graphs, bot AI, movement, match counts, maps, shaders,
  character rigs, textures, materials or graphics downgrades.
- Match start remains gated on the completion of visual prewarm, which now
  includes this extra bounded animation work on Android. This may add some
  startup delay and must be measured in a real phone playthrough.

## Tests and evidence limit
```sh
python3 Tools/Validation/test_android_rival_animation_prewarm.py
python3 Tools/Validation/test_android_rival_visual_prewarm.py
```

These are static behavior contracts, **not** proof of a successful Unity Editor
compile, animation retargeting, 60FPS gameplay, eliminated ANR, GPU reset, or
memory-pressure crash. Validate first rival's full animation, appearance,
spawning and camera on the target handset, and compare cold-boot / first-rival
Profiler frame times. Unity's async asset loading still does main-thread
integration; it is not a promise of zero stalls.

API reference:
https://docs.unity.com/en-us/engine/6000.3/script-reference/unityengine/resources/loadasync
