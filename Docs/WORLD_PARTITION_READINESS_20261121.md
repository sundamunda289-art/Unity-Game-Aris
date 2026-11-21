# Unity 6 world partition readiness (21 November 2026)

The canonical scene `Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity`
was inspected without rewriting any scene, prefab, collider, texture or render
settings. Run the current source-level preflight:

```bash
python3 Tools/Validation/world_partition_readiness.py --json > /tmp/aaris-world-partition-readiness.json
python3 -m unittest discover -s Tools/Validation -p 'test_world_partition_readiness.py' -q
```

The **fail-closed** optional `--require-safe` exit status is 1 until a district
can be moved without breaking collider authority, LOD ownership or serialized
Unity object references.

## Actual current scene inventory

| Category | Count |
| --- | ---: |
| GameObjects | 878 |
| MeshRenderers | 600 |
| LODGroups | 128 |
| MeshColliders | 95 |
| BoxColliders | 200 |
| Terrain tiles | 4 |
| District groups | 13 |
| GameObjects beneath `BattleRoyaleProps` | 303 |

Every district has both LODGroups and physical MeshColliders. More
importantly, the persistent `MobileRuntime` object has Inspector references
to the **individual building colliders** in each district, through the
`AarisNearbyColliders.obstacles` serialized array. These are 95 collision
dependencies across the 13 districts, not optional graphics. Moving an entire
district to an additive scene without first transferring/rebinding those
colliders makes persistent physics streamer references invalid and can allow
players, cars, rival AI or Gloo Walls to pass through missing cover.

## Required pilot before any authored scene extraction

1. Start with a small isolated district (e.g. `ForestLookout`, one
   building/LODGroup/collider), not all 13 or the full 2 km terrain.
2. Preserve a collider-identical persistent proxy and **rebind the existing
   serialized collider array** before extracting the corresponding building.
   Check non-physics interactions and cross-scene references, not just the
   building's renderer list.
3. Preserve a faithful far-distance silhouette from the original last LOD,
   with correct material/mesh scale. Activate full-detail objects
   asynchronously with a visible overlap; avoid distance/teleport gaps,
   blocked player movement and duplicate solid collisions.
4. Keep native terrain tiles, roads, foundations, vehicle spawn authority and
   globally visible island/ocean/sky in the persistent scene. Do not migrate
   them merely because they are scene roots.
5. Bake any *applicable* occlusion data with all relevant scenes open, validate
   cross-scene visibility, and compare near/far frame screenshots.
6. Test the generated scene with the actual Unity 6000.3.26f1 Editor,
   existing C# QA, Android APK, and on-device CPU/GPU frame times.
   Avoid claims of FPS gains from source-level checks alone.

The read-only audit purposely generates **no new worktree, scene or runtime
bootstrap**. It is the prerequisite safety gate, not a completed world-stream
migration. All asset/visual fidelity policies remain unchanged.
