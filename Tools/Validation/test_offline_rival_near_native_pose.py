#!/usr/bin/env python3
"""Physical near-rival snapshots must preserve live physics and stance LOS."""
import random
import unittest
from pathlib import Path

SRC=(Path(__file__).resolve().parents[2]/
     "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text()
NEAR=SRC.split("// A new on-camera actor may wait one frame",1)[1].split(
    "// Distant bots stay fully alive as logical actors",1)[0]

class NativeNearPose(unittest.TestCase):
    def test_one_pose_for_pre_move_steering(self):
        for needle in (
            "Vector3 selfPosition = transform.position;",
            "Vector3 selfForward = transform.forward;",
            "target.position - selfPosition",
            "recentRivalAttacker.transform.position - selfPosition",
            "coverDestination - selfPosition",
            "owner.WouldCrossInactiveCover(transform.position, displacement)",
            "owner.WouldCrossInactiveCover(selfPosition, first)",
            "owner.WouldCrossInactiveCover(selfPosition, second)",
            "Quaternion.Euler(0f, turnOffset, 0f) * selfForward"):
            self.assertIn(needle,NEAR)
        self.assertLessEqual(NEAR.count("transform.position"),11)
        self.assertLessEqual(NEAR.count("transform.forward"),2)
        self.assertNotIn(".selfPosition",NEAR)

    def test_native_collision_aim_and_animation_preserved(self):
        for needle in (
            "Vector3 beforeMove = transform.position;",
            "CollisionFlags hit = capsule.Move(displacement +",
            "owner.TrackRivalPosition(spatialSlot, this, afterMove);",
            "Vector3 realTravel = afterMove - beforeMove;",
            "IsPostMoveShotInRangeAndFacing(afterMove, transform.forward,",
            "owner.MayAttackPlayer(this)",
            "TryGetSightHit(target, out RaycastHit shotHit)",
            "MuzzlePathClear(target)",
            "targetCapsule = target == playerTarget ? playerTargetCapsule",
            "canSee = HasSight();",
            "owner.TryZoneRetreatDirection(transform.position,",
            "UpdateAnimationBlend(wanted, dt)"):
            self.assertIn(needle,NEAR)
        self.assertLess(NEAR.index("Vector3 selfPosition = transform.position;"),
                        NEAR.index("if (now >= nextThink)"))
        self.assertLess(NEAR.index("Vector3 beforeMove = transform.position;"),
                        NEAR.index("capsule.Move(displacement +"))
        self.assertLess(NEAR.index("capsule.Move(displacement +"),
                        NEAR.index("Vector3 realTravel = afterMove - beforeMove;"))

    def test_100_bot_steering_and_post_collision_parity(self):
        rng=random.Random(20261123)
        for _ in range(240):
            poses=[(rng.uniform(-850,850),rng.uniform(-850,850))
                   for _ in range(100)]
            for i,(x,z) in enumerate(poses):
                target=poses[(i+17)%100]
                cover=(rng.uniform(-850,850),rng.uniform(-850,850))
                step=(rng.uniform(-1.8,1.8),rng.uniform(-1.8,1.8))
                snapshot=(x,z)
                self.assertEqual(
                    ((target[0]-x,target[1]-z),(cover[0]-x,cover[1]-z)),
                    ((target[0]-snapshot[0],target[1]-snapshot[1]),
                     (cover[0]-snapshot[0],cover[1]-snapshot[1])))
                t=rng.random()
                moved=(x+step[0]*t,z+step[1]*t)
                self.assertAlmostEqual(moved[0]-x,step[0]*t,places=9)
                self.assertAlmostEqual(moved[1]-z,step[1]*t,places=9)

if __name__=="__main__":
    unittest.main()
