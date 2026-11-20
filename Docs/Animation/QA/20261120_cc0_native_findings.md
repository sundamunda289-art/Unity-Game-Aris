# 20 Nov 2026: Native Unity CC0 crouch/crawl A/B experiment

Actual Unity 6000.3.26f1 Play Mode screenshot composite, 12 frames (Standing, Crouch, Prone × four views) from persistent isolated Codespace worktree at candidate `cda6e56460fcea8b238baf5b952d6fb57b06673f` plus five extracted standalone Quaternius Universal Animation Library CC0 HumanMotion clips.

Source: https://quaternius.com/animviewer.html (CC0 1.0).

**Result: partial visual improvement, NOT PASS**. Unlike merged baseline `main` (#786), which placed much of the prone body beneath the floor, the Quaternius Crawl_Idle_Loop produces a recognizable horizontal prone body, with Standing remaining upright and crouch mostly viable. Prone still fails native `PRONE_KNEE_SIDEWAYS_OR_MISALIGNED`: left knee lateral offset -0.035m, right +0.454m, knee delta 0.031m, footGap ~0.035m; skinned mesh AABB min is -1.572m relative to terrain (bounds alone are not an actual clipping proof). The uneven right knee and visible ground issues need further correction. No QA certification, no merge to main.

Original working Codespace 30 modified files were preserved. This JPEG is derived from real Unity camera frames, not AI-generated.