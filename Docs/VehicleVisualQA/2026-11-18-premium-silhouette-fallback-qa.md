# Premium real-metre silhouette guard and native measurement logs

This gate defends against a failure distinct from bad wheel axles:
an imported FBX can contain fully rendered material, four wheel names,
and valid tyre centres, yet still produce a **tiny or bizarre body silhouette**
after its Unity importer axes or model prefab scales change.

At spawn (and only for imported high-fidelity models), inspect the *final*
vehicle-space body dimensions after source-axis correction, class scaling,
chassis recentering and tyre-road correction, but **before** allocating the
Rigidbody vehicle motor. Reject/fallback when actual width/length shrink
below 74%/78% of the matched reference, exceed the reference by >8%,
the roof is implausibly below 0.82m or above 2.58m, or proportions imply
a wafer-thin/car-wide noncar shape.

Legacy verified Kenney fallback models are explicitly exempt, so a damaged
source model can still produce a visible drivable local car after failure.
No retry-loop force, WheelCollider change, new render mesh, material, or
Android frame-time allocation was added.

When Unity is actually running a **development build**, every successful car
spawn logs one `AARIS_CAR_RENDER_METRES id=... width=... height=... length=...`
entry. These numbers can be compared directly with the catalog envelopes to
prove real importer dimensions, independently of screenshots. There is
no per-frame mesh scan, and this log is skipped in production builds.

Static/geometry tests cover all eight actual catalog body targets plus
malformed, NaN/Infinity, oversized and undersized cases. Only native
Unity/Android Play Mode can certify on-screen appearance, contact and FPS.
