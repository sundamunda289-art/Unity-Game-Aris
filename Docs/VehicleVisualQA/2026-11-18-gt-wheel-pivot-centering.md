# Premium GT wheel-centre pivot correction — 18 November 2026

## Verified source geometry

The source OBJ wheel mesh vertices are not centred on their Unity wheel
object pivots. The offsets below are **local mesh X centres**, measured directly
from all 5,000-ish wheel OBJ vertex positions, not inferred from screenshots:

| Tyre mesh | Pivot-to-visible-centre X |
|---|---:|
| Front left | +0.09365 m |
| Front right | -0.09365 m |
| Rear left | +0.12185 m |
| Rear right | -0.12185 m |

The rear tyres are wider (0.330 m versus front 0.273 m), but their
rolling-plane diameters are circular: 0.722 m versus 0.686 m.
Those modelled staggered tyre widths **must not be stretched** or replaced
with identical primitives.

Previously `SyncVisibleWheels` placed the **wheel object Transform** exactly
at the WheelCollider-provided centre. When an artist supplies non-centred
local geometry, the visible centre then moves 9–12 cm inside the real wheel
position, especially noticeable when camera distance and wheel angle change.

## Runtime correction

At attachment (not per frame), cache the vector from each wheel object
pivot to the measured visible mesh centre, in real world metres but expressed
in the wheel's rest orientation. This works for both embedded authored tyres
and separately instantiated fallback tyre prefabs. The real WheelCollider
still owns physics; on every existing wheel pose update:

```
renderRotation = wheelPoseRotation * authoredRestRotation
renderPivot = wheelPosePosition - renderRotation * cachedMeshCentreOffset
```

All four wheel visual centres thus track their collider positions during
spin, suspension and steering, without duplicating physics or introducing
four extra GameObjects, meshes, materials, raycasts, or frame allocations.

## QA boundaries

Offline tests verify actual OBJ mesh centroids, the compensation formula
across multiple wheel-spin angles, source wiring and no per-frame mesh queries.
Unity Play Mode and Android footage/FPS are still needed for visual confirmation.
