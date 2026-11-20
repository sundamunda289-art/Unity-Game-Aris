#!/usr/bin/env python3
"""Contract for externally licensed humanoid stance clips and interpolation."""
from pathlib import Path
root = Path(__file__).resolve().parents[2]
visual = (root / "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text()
als = (root / "Assets/AarisWorld/Scripts/ThirdParty/ALSUnity/ALSMath.cs").read_text()
assert "namespace ALSUnity" in als
assert "public static float InterpTo(float current, float target, float deltaTime, float speed)" in als
assert visual.count("ALSUnity.ALSMath.InterpTo(") == 2
assert "AARIS_MIT_STANCE_CLIPS_ACTIVE" in visual
assert '"AarisCharacters/CommunityStances/IdleProneLyingDown"' in visual
assert (root / "Assets/Resources/AarisCharacters/CommunityStances/IdleProneLyingDown.anim").is_file()
assert (root / "Assets/Resources/AarisCharacters/CommunityStances/IdleProneLyingDown.anim.meta").is_file()
assert "communityCrouch.humanMotion" in visual
assert "communityProne.humanMotion" in visual
assert "if (communityStanceClipsLoaded)" in visual
assert "PlanBalancedLowStanceFeet(proneShare" in visual  # non-vendor fallback
assert "crouchClip = communityCrouch;" in visual
assert "proneClip = communityProne;" in visual
assert "Resources.Load<AnimationClip>(" in visual
for pose in ("Stand", "Crouch", "Prone"):
    f = root / "Assets/Resources/AarisCharacters/CommunityStances" / f"Idle{pose}Default.anim"
    assert f.is_file() and f.stat().st_size > 5000, f
    data = f.read_text()
    assert "AnimationClip:" in data
    assert "classID: 95" in data, f
    assert "m_FloatCurves:" in data
    assert f.with_name(f.name + ".meta").is_file()
for name in ("VRCEasyLoco_MIT.txt", "ALSUnity_MIT.txt", "ALSUnity_NOTICES.md"):
    assert (root / "Docs/ThirdParty" / name).is_file()
assert "MIT License" in (root / "Docs/ThirdParty/VRCEasyLoco_MIT.txt").read_text()
print("PASS: vendor clips, MIT notices, source-level ALS blend, fallback")
print("NOTE: native Unity import/rig visual still requires a real Editor run")
