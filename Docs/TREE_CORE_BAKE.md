# Save the inspected tree shape in core mesh assets

Status on 2026-11-04: **not baked**. The workspace has no Unity Editor. Repository availability run [41758501296](https://github.com/Aaris-khan-don/Unity-Game-Aris/actions/runs/41758501296) found no configured Unity runner, Editor path or hosted license secret. The existing automatic `AarisTreeShapeImporter` remains necessary until real corrected mesh bytes have been saved and verified. Removing it now would lose the radial shape correction.

`Tools/World/bake_tree_core.py` provides explicit authoring for the six existing SpeedTree prototypes and their 24 mesh LODs. Its C# helper lives in `Tools/World/UnityTreeCoreBake.cs`, outside `Assets`. Unity does not compile or execute that helper in the normal project. The launcher injects it only into a disposable project for an explicit `-executeMethod` invocation. Neither helper registers import, build or startup callbacks.

The first Editor session imports the existing tagged sources with the reviewed correction active, clones complete native meshes, saves six `.asset` containers and checks all vertex streams, indices, submeshes, bounds and readability against the corrected imports. Packed wind/morph/anchor attributes survive; meshes are not reconstructed from positions and UV0. Each container holds the four existing LOD meshes.

In the disposable project, only the exact four mesh references per prefab are retargeted. All other prefab bytes, GUIDs, local IDs, colliders, transforms, wind references, materials and LOD thresholds remain. Existing `.st.meta` bytes change only by clearing the custom shape tag. The proprietary sources remain standard Unity wind/material dependencies. Native terrain instances, terrain heights, houses, roads and the saved scene are not rewritten.

Only then does the launcher remove the automatic importer from the disposable project, delete its import cache and run a second, completely cold Unity import. This verifies native mesh fingerprints, all prefab LOD bindings, wind/material dependencies and source reimport independence. Unexpected source changes reject the result. Failed imports never remove the importer from the source checkout.

Use a complete LFS checkout and the installed/licensed **Unity 6000.3.26f1** matching `ProjectVersion.txt`:

```bash
export UNITY_EDITOR_PATH=/absolute/path/to/Unity
python Tools/World/bake_tree_core.py status
python Tools/World/bake_tree_core.py prepare --out /absolute/path/outside-project/tree-core-review
```

Read `review.json`, its mesh/binding evidence and the resulting native assets before applying. `prepare` changes only disposable projects and writes its bundle outside the repository. The original map and importer stay intact. The supplied workflow performs this preview on the configured trusted Linux runner; it does not apply, commit or merge the outputs.

```bash
python Tools/World/bake_tree_core.py apply --bundle /absolute/path/outside-project/tree-core-review
python Tools/World/bake_tree_core.py apply --bundle /absolute/path/outside-project/tree-core-review --write
```

Apply rechecks the complete project snapshot, exact bundle bytes and permitted edits. It writes only the six native mesh containers and metadata, six prefab mesh references, six retired profile tags, the completed migration contract, and removes the old importer plus its metadata. A write failure restores original files. New native assets require their new Unity-generated metadata; existing metadata is preserved. Review the Git diff, stage the new meshes through Git LFS and run the source/native checks before merging.

The small completed contract is generated only after real Unity execution. Historical tree checks reverse exactly its hash-bound reference/tag changes before checking the earlier LOD contract; unreviewed geometry, collider or metadata drift still fails. Historical reports are not rewritten.

Transaction tests use a simulated engine and clearly identify that limit. C# parsing is syntax checking, not Unity compilation. Even a successful bake/import proves geometry and dependency persistence; it does not establish rendered wind/billboard quality, collision routes or a 2 GB phone's FPS. Those require actual Editor/player/device checks.
