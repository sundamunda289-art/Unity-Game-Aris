# Vehicle import-axis and real-road parking envelope QA — 18 November 2026

The five original 1990s-* and 1980s-* CC0 imported FBX model raw geometry
has its long axis on source **Y** and height on source **Z**. Raw body dimensions
measured directly from compressed FBX Vertices (in metres) are approximately:

| Body mesh | Width X | Long axis Y | Height Z |
|---|---:|---:|---:|
| 1980s van | 2.05 | 4.93 | 1.71 |
| 1990s hatchback | 1.86 | 4.06 | 1.33 |
| 1990s pickup | 2.03 | 5.32 | 1.60 |
| 1990s sports | 1.88 | 4.42 | 1.06 |
| 1990s SUV | 1.95 | 4.44 | 1.61 |

Unity FBX importing MAY already convert source axes. The runtime spawn checks
visible bounds: rotate source -90 degrees X only if Y is convincingly the car's
long axis. The sedan, GT and already-corrected FBX must NOT rotate again.
Wheel forward-axle orientation and uniform per-class fit run AFTER this check.

Reviewed real-metre upper envelopes (width x length):
Sedan 1.96x4.50, SUV 2.10x4.80, Sports 1.99x4.46, GT 2.08x4.48,
Hatchback 1.81x3.90, Van 2.05x5.08, Utility 2.10x5.42, Buggy 1.90x3.45.

Unlike fitting every car in the old 1.88x4.28 sedan cap, this preserves the
long source pickup's true road silhouette without nonuniform stretching.
The *same* type dimensions now drive spawn-time four-corner support checks
and physics overlap occupancy. Max 10 nearby cars and 300 ms polling remain
unchanged. Physics shell and placeholder wheelbase extend for long bodies.

Evidence scope: raw FBX geometry extraction, source and deterministic
geometry tests; not native Unity/Android Play Mode, FPS, or real rendered
wheel contact. Mismatched or corrupt art retains verified fallback behavior.
