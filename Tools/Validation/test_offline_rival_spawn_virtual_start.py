#!/usr/bin/env python3
"""Ensure distant rivals are virtual from birth without hiding real close combat."""
from pathlib import Path
import unittest

SOURCE=(Path(__file__).resolve().parents[2]/"Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()

class SpawnVirtualStart(unittest.TestCase):
    def test_first_frame_distant_capsules_are_disabled(self):
        init=SOURCE.split("internal void Initialize(AarisOfflineBRDirector",1)[1].split("private void BuildVisual()",1)[0]
        self.assertIn("if (spawnDistanceSqr >= 185f * 185f ||",init)
        self.assertIn("!spawnCameraRelevant))",init)
        self.assertIn("virtualBody = true;",init)
        self.assertIn("capsule.enabled = false;",init)
        self.assertLess(init.index("capsule.enabled = false;"),init.index("BuildVisual();"))
    def test_midrange_behind_camera_does_not_import_rig(self):
        init=SOURCE.split("internal void Initialize(AarisOfflineBRDirector",1)[1].split("private void BuildVisual()",1)[0]
        self.assertIn("if (spawnDistanceSqr < 55f * 55f ||",init)
        self.assertIn("(spawnDistanceSqr < 100f * 100f &&",init)
        self.assertIn("owner.IsDistantRivalOnScreen(transform.position)",init)
        self.assertIn("if (!visualBuilt && playerDistanceSqr < 105f * 105f)",SOURCE)
    def test_near_and_far_logic_is_continuous(self):
        def state(distance,on_screen):
            virtual = distance >= 185 or (distance >= 90 and not on_screen)
            initial_rig = distance < 55 or (distance < 100 and on_screen)
            return virtual,initial_rig
        self.assertEqual(state(40,False),(False,True))
        self.assertEqual(state(80,False),(False,False))
        self.assertEqual(state(100,False),(True,False))
        self.assertEqual(state(100,True),(False,False))
        self.assertEqual(state(80,True),(False,True))
        self.assertEqual(state(400,False),(True,False))
        self.assertEqual(state(1000,True),(True,False))

if __name__=="__main__":unittest.main()
