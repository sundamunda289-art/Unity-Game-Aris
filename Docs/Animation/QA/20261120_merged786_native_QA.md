# Real Unity 6000.3.26f1 merged main (#786) pose QA — 20 November 2026

Captured in detached persistent Codespace worktree `/workspaces/Aris-Stance-QA` at exact merged `main` SHA `db258f441df195de91301883a1a0f67704291e45`. The image contact sheet holds **12 real Unity camera renders** (4 angles for each Standing, Crouch, Prone), resized and labeled for comparison; **not AI-generated**.

**Observed:** Standing upright, crouch lowered with visible forward bend. Prone rendered predominantly *below terrain* and inverted/sideways instead of a horizontal body. QA failed `PRONE_KNEE_SIDEWAYS_OR_MISALIGNED` with left/right lateral offsets 0.325m / 0.341m and knee height delta 0m; mesh min is 1.536m below the terrain surface. This is a real regression in the imported lying clip/rig and root visual offsets. Do NOT treat static tests as proof of good visuals. The older proposal PR #787 also failed: its alternative standing pose kneels and its prone body is buried.

**Next engineering direction:** replace incompatible VRChat lying preset with a real compatible locomotion/crawl idle from CC0 source; preserve native repeatable visual QA and original Codespace dirty files.