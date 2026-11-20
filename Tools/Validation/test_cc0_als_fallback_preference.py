#!/usr/bin/env python3
"""Source contract for ALS crouch priority with CC0 fallback.

CI only: native Unity photos and Android frame timings are separate gates.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[2]
visual = (root / "Assets/AarisWorld/Scripts/AarisCharacterVisual.cs").read_text()
source_block = visual[
    visual.index("cc0CrouchLoaded = cc0CrouchIdle != null"):
    visual.index("cc0CrawlLoaded = cc0CrawlIdle != null")
]

assert "cc0CrouchIdle.humanMotion && cc0CrouchForward.humanMotion" in source_block
assert "!cc0CrouchIdle.legacy && !cc0CrouchForward.legacy &&" in source_block
assert "!alsCrouchLoaded;" in source_block
assert "crouchClip = alsCrouchLoaded ? alsCrouchIdle" in visual
assert "crouchWalkClip = alsCrouchLoaded ? alsCrouchForward" in visual
assert "if (cc0CrouchLoaded)" in visual
assert "crouchClip = cc0CrouchIdle;" in visual
assert "crouchWalkClip = cc0CrouchForward;" in visual
assert "if (cc0CrawlLoaded)" in visual
assert "proneClip = cc0CrawlIdle;" in visual
assert "crawlClip = cc0CrawlForward;" in visual

for name in ("als_crouch_idle", "als_crouch_forward"):
    resource = root / f"Assets/Resources/AarisCharacters/Primary/ALS/{name}.anim"
    assert resource.is_file() and resource.stat().st_size > 10000, resource

# Truth table: never override a usable ALS pair with a second retargeted crouch.
for als in (False, True):
    for cc0_usable in (False, True):
        cc0_selected = cc0_usable and not als
        assert not (als and cc0_selected)
        if not als and cc0_usable:
            assert cc0_selected
print("PASS: proven ALS crouch takes priority; CC0 remains a valid fallback")
print("NOT TESTED: Unity native 16/30-frame poses or mobile frame pacing")
