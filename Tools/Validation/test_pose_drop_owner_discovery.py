#!/usr/bin/env python3
"""Source contract: flight-owned player discovery in native pose QA.

Only checks Editor test harness wiring; it cannot certify visual correctness.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[2]
qa = (root / "Assets/AarisWorld/Editor/AarisPoseFrames.cs").read_text()
drop = (root / "Assets/AarisWorld/Scripts/AarisOfflineDrop.cs").read_text()
assert "footMotor.enabled = false;" in drop
assert "var d=UnityEngine.Object.FindFirstObjectByType<AarisOfflineDrop>();" in qa
assert "var p=d!=null?d.GetComponent<AarisThirdPersonPlayer>():" in qa
assert "UnityEngine.Object.FindFirstObjectByType<AarisThirdPersonPlayer>();" in qa
assert "FindObjectsInactive.Include" in qa
assert 'Log("ACTOR_BOOTSTRAP_DIAGNOSTIC scene="' in qa
assert 'Finish("NO_PLAYER_OR_DROP")' in qa
assert 'if(Now-SessionState.GetFloat("pose.time",(float)Now)>15)' in qa
assert "AarisOfflineDrop" in qa and "CaptureMultiAnglePortrait" in qa
print("PASS: QA discovers flight-owned motor and reports distinct missing actor components")
print("NOT TESTED: actual Unity Editor actor discovery, posture screenshots, Android FPS")
