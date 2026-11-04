# Saved SpeedTree distant-shadow budget (2026-11-04)

## Inspected source
Six original saved Terrain SpeedTree prototypes, not generated replacements:
`Conifer`, `Cypress_Forest_Desktop`, and `Pine_A/B/C/D`.
Each source prefab contains four MeshRenderers, one for each LOD0–LOD3.
All four originally had `m_CastShadows: 1`.

## Core asset change
On each of the **six saved source .prefab files**, set only
`m_CastShadows: 0` on the renderers named `*_LOD2` and `*_LOD3`.
Retain realtime shadows on LOD0/LOD1 and retain `m_ReceiveShadows: 1`
on all four renderer levels.

This targets shadow-caster work from distant forest representations while
keeping their meshes and billboard visibility untouched. It is deliberately
**not** a new LOD generator and does **not** change tree height, canopy,
bark profile, colliders, wind, GUID, imports, native TerrainData instances,
tree rotations or their positions. No `.st.meta` or terrain binary is
rewritten. Unity's LOD thresholds and far cutoff are preserved.

### Trade-off
At certain lighting/camera angles the transition to a lower LOD can make
a ground shadow disappear even though the visible tree remains. This is
intentional and reversible. Near-level tree shadowing remains enabled.
This optimization is global to these shared prototypes, not conditional
on a device memory class. Do not claim zero visual change.

## Verification
Run in the repo checkout:

```bash
python -m unittest Tools.World.tests.test_speedtree_far_shadow_budget -v
python -m unittest discover -s Tools/World/tests -v
python Tools/World/check_contracts.py
python Tools/World/world.py verify --project-checks
```

The first check reads the **saved source prefabs** and verifies
LOD0/LOD1 shadow casting on, LOD2/LOD3 off, all receiving, and the
existing four-stage descending LOD thresholds with billboard end.
Compare the six file diffs to ensure exactly two scalar shadow-field
changes each. No scene rewrite or LFS materialization is necessary for
these text-only checks.

The actual shader/shadow pass cost, profile CPU/GPU frame times,
world-scale visual consistency, LOD transitions and memory usage
**require a matching Unity 6000.3.26f1 Android build**, with real LFS
assets, a camera walkthrough and preferably a 2 GB as well as a 3 GB
device. Use real device profiling and thermal runs before merging or
asserting FPS improvement. Compare shadow-map draw calls, P95 frame
time, and visibility of distant forest at the same camera points.

## Why this scope
The current branch already has parallel draft PRs modifying the
mobile runtime, house LOD thresholds, native mesh bake and tree
silhouette. Those changes are intentionally not rebased or merged here.
This independent scalar edit is reversible and avoids guesswork
about building door/window geometry or proprietary SpeedTree bytes.
