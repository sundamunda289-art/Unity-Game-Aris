# Android settlement expansion catalog prewarm — 2026-11-19

Target: Unity 6000.3 / URP 17.3 / Snapdragon 680 / Adreno 610 /
4 GB RAM, native 2 km x 2 km map.

## Verified source-level risk
Before this change, `AarisSettlementExpansion.Start()` called two
`Resources.Load` operations in the first scene phase: the JSON expansion
plan and the serialized `AarisExpansionCatalog`, which references prefab
assets. The operation may cause asset integration on the Unity main
thread and compete with initial terrain/player rendering. This is
not a confirmed explanation for phone-wide freezes or unexpected exits.

## Narrow mitigation
- Android players yield the first frame, request the original plan with
  `Resources.LoadAsync<TextAsset>`, wait for completion, yield again,
  and then request the original serialized catalog with
  `Resources.LoadAsync<AarisExpansionCatalog>`. Both requests have low
  priority. No parallel resource request queue is introduced here.
- Only after both succeed do we call the **same** schema validation,
  four-tile terrain lookup, bounds-streamer discovery, player lookup
  and the existing `PlaceSitesAcrossFrames()` routine.
- Disabled/destroyed components do not resume placement from the async path.
- Desktop and Unity Editor still use the original synchronous Resources
  load calls. Missing assets, malformed JSON or wrong map still fail
  with the same diagnostic messages and without placement.

## Visual and gameplay invariants
All authored buildings and meshes, 21 site intents, coordinate validation,
tree clearance, physics overlaps, interactive-housing logic, prefab selection,
source TerrainData, graphics, draw distance and visibility remain unchanged.
The prewarm can delay settlement creation by additional rendered frames;
measure when buildings appear before release, especially during flight.

## Evidence and QA
```sh
python3 Tools/Validation/test_android_expansion_catalog_async.py
python3 Tools/Validation/test_android_tree_clearance_streaming.py
```

Static source contracts / deterministic simulations and CI are necessary
but not sufficient. A licensed Unity Editor compile and Android build should
check asset resolving, no missing buildings, scene-start timing and native
memory/graphics readings. Never claim the 3-30 second lag or crash was fixed
without real device profiler and logcat evidence.
