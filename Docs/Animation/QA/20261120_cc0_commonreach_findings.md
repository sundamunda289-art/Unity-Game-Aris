# Native Unity QA: CC0 crawl, symmetric authored legs, shared reach IK — 20 November 2026

12 true Unity 6000.3.26f1 Play Mode camera frames captured in isolated persistent worktree `/workspaces/Aris-Stance-QA` at base commit `0781bc058c5cd12febcdc5e61b838660bf3d97f5`, with uncommitted symmetric-clip and anatomy-scaled common-reach candidate changes. Not an AI illustration.

**Progress:** Compared with the previous symmetric pose (left-right knee-height delta 0.342 metres), the new shared bone-length reach achieved **0.040 m knee-height difference**, **0.028 m knee lateral deviation on both sides**, and **0.268 m foot spread**. Both knees measured above ground (0.306 and 0.266 m).

**Still FAILED**: `SETTLED_PRONE_BOOT_CONTACT_INVALID`: left/right ankle heights = 0.000 m above terrain, lower than expected anatomical boot clearance 0.04-0.18 m. The QA script did not proceed to full pass. Camera shows standing and crouch remain recognizable, prone appears horizontal. Next iteration must restore ankle clearance without spoiling knee symmetry and inspect again.

Do not merge until all native guards and visual review pass. The original Codespace working tree retains 30 dirty files.