#!/usr/bin/env python3
"""Source contract for native Editor pose capture's real actor bootstrap.

Do not substitute a fake player or silently accept a failed scene. Real
Unity 6000.3.26f1 Play Mode remains a mandatory verification gate.
"""
from pathlib import Path
root = Path(__file__).resolve().parents[2]
editor = (root / "Assets/AarisWorld/Editor/AarisPoseFrames.cs").read_text()
motor = (root / "Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs").read_text()
drop = (root / "Assets/AarisWorld/Scripts/AarisOfflineDrop.cs").read_text()
start = editor.index("// The flight system legitimately disables the foot motor")
end = editor.index("var b=p.GetComponent<AarisBattleActions>();",start)
lookup = editor[start:end]
assert "var d=UnityEngine.Object.FindFirstObjectByType<AarisOfflineDrop>();" in lookup
assert "d.GetComponent<AarisThirdPersonPlayer>()" in lookup
assert "if(p==null)p=UnityEngine.Object.FindFirstObjectByType<AarisThirdPersonPlayer>();" in lookup
assert lookup.index("d.GetComponent<AarisThirdPersonPlayer>()") < lookup.index("if(p==null)p=")
assert "if(p==null||d==null)" in lookup
assert "QA_BOOTSTRAP_LOOKUP player=" in lookup
assert "Finish(\"NO_PLAYER_OR_DROP\");" in lookup
assert "SceneManager.GetActiveScene().path" in lookup
assert "new GameObject" not in lookup and "AddComponent" not in lookup
assert "footMotor.enabled = false;" in drop
assert "AarisOfflineDrop.EnsureForPlayer(this);" in motor
print("PASS: QA retrieves real motor via drop and reports missing bootstrap")
print("NOT TESTED: Native Unity scene, pose images, Android application")
