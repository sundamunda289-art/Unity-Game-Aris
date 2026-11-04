"""Source guard for avoiding idle terrain/bridge queries in the saved review player.

These checks are not a Unity compile, CharacterController physics test, or an FPS benchmark.
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PLAYER = ROOT / "Assets/AarisWorld/Scripts/AarisThirdPersonPlayer.cs"


class PlayerIdleFootingSourceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = PLAYER.read_text(encoding="utf-8")
        cls.update = cls.source.split("private void Update()", 1)[1].split(
            "private void AnimateWalk(", 1
        )[0]

    def test_footing_query_is_nested_under_nonzero_horizontal_intent(self):
        u = self.update
        self.assertRegex(
            u,
            r"if \(wish\.sqrMagnitude > 0f\)\s*\{\s*"
            r"Vector3 candidate = transform\.position \+ wish \* \(speed \* dt\);\s*"
            r"//[^\n]*\n\s*if \(Mathf\.Abs\(candidate\.x\) > 975f \|\| "
            r"Mathf\.Abs\(candidate\.z\) > 975f \|\| !SafeFooting\(candidate\)\)"
            r"\s*wish = Vector3\.zero;\s*\}",
        )
        self.assertEqual(u.count("SafeFooting(candidate)"), 1)
        self.assertEqual(u.count("Vector3 candidate ="), 1)

    def test_gravity_and_character_controller_still_update_when_idle(self):
        u = self.update
        self.assertIn("verticalVelocity = Mathf.Max(verticalVelocity - gravity * dt, -40f);", u)
        self.assertIn("controller.Move((wish * speed + Vector3.up * verticalVelocity) * dt);", u)
        self.assertIn("if ((flags & CollisionFlags.Above) != 0", u)
        self.assertLess(u.index("if (wish.sqrMagnitude > 0f)"), u.index("controller.Move("))
        self.assertIn("now - lastGrounded <= 0.1f", u)

    def test_original_terrain_bridge_and_collider_policy_retained(self):
        s = self.source
        self.assertIn("if (GroundHeight(position) >= seaLevel + 0.35f) return true;", s)
        self.assertIn("Physics.Raycast(position + Vector3.up * rise", s)
        self.assertIn("support.point.y >= seaLevel + 0.35f", s)
        self.assertIn("support.normal.y >= Mathf.Cos(controller.slopeLimit * Mathf.Deg2Rad)", s)
        self.assertIn("if (nearbyColliders != null) nearbyColliders.RefreshNow();", s)
        self.assertNotIn("farClipPlane =", s)


if __name__ == "__main__":
    unittest.main()
