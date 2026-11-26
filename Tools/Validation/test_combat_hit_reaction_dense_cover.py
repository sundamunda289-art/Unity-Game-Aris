#!/usr/bin/env python3
"""Regression coverage for burst hit reaction and dense cover searches.

Static source contracts plus simulated pellet cadence; native PhysX testing
is required separately before merging.
"""
from pathlib import Path
import unittest

SOURCE = (Path(__file__).resolve().parents[2] /
          "Assets/AarisWorld/Scripts/AarisOfflineBRDirector.cs").read_text(
              encoding="utf-8")
ON_DAMAGED = SOURCE.split("private void OnDamaged(", 1)[1].split(
    "internal bool ReservesCoverNear(", 1)[0]
COVER = SOURCE.split("private void TrySeekTacticalCover(", 1)[1].split(
    "internal void TakeZoneDamage(", 1)[0]


def react_to_hits(times, hold_seconds=0.22):
    """Match the <=-one-reaction-per-burst timestamp gate."""
    next_reaction = 0.0
    reactions = 0
    for now in times:
        if now >= next_reaction:
            next_reaction = now + hold_seconds
            reactions += 1
    return reactions


class RivalDamageReactionTests(unittest.TestCase):
    def test_shotgun_pellet_burst_reacts_once(self):
        self.assertEqual(react_to_hits([1.0] * 8), 1)
        self.assertEqual(react_to_hits([1.0, 1.05, 1.10, 1.18]), 1)
        self.assertEqual(react_to_hits([1.0, 1.26, 1.55]), 3)

    def test_reaction_gate_precedes_cover_query_gate(self):
        body = ON_DAMAGED
        gate = body.index("if (Time.time >= nextHitReaction)")
        cover = body.index("if (virtualBody || Time.time < nextCoverQuery) return;")
        self.assertLess(gate, cover)
        self.assertIn("nextHitReaction = Time.time + 0.22f;", body)
        self.assertIn("turnOffset += avoidanceHandedness * 65f;", body)
        self.assertIn("nextThink = 0f;", body)
        self.assertEqual(body.count("turnOffset += avoidanceHandedness * 65f;"), 1)
        react = body.split("if (Time.time >= nextHitReaction)", 1)[1].split("}", 1)[0]
        for token in ("nextHitReaction = Time.time + 0.22f;",
                      "turnOffset += avoidanceHandedness * 65f;", "nextThink = 0f;"):
            self.assertIn(token, react)
        call = "TrySeekTacticalCover(attacker.transform.position, Time.time);"
        self.assertEqual(body.count(call), 1)
        self.assertLess(cover, body.index(call))
        self.assertNotIn("Physics.OverlapSphere", body)
        # The shared helper owns the same 1.5s budget. Advance it before
        # either a degenerate-direction return or the actual physics search.
        self.assertEqual(COVER.count("nextCoverQuery = now + 1.5f;"), 1)
        self.assertLess(COVER.index("nextCoverQuery = now + 1.5f;"),
                        COVER.index("if (away.sqrMagnitude < .01f) return;"))
        self.assertLess(COVER.index("nextCoverQuery = now + 1.5f;"),
                        COVER.index("Physics.OverlapSphereNonAlloc("))

    def test_saturated_24_hit_search_retries_with_shared_128_buffer(self):
        self.assertIn(
            "crowdedCoverCandidates = new Collider[128];",
            SOURCE,
        )
        body = COVER
        self.assertIn("coverCandidates = new Collider[24];", SOURCE)
        self.assertIn("EnsurePhysicalCombatBuffers();", body)
        self.assertIn("11f, coverCandidates, Physics.DefaultRaycastLayers,", body)
        self.assertIn("if (count >= coverCandidates.Length)", body)
        self.assertIn("candidates = crowdedCoverCandidates;", body)
        self.assertIn("count = Physics.OverlapSphereNonAlloc(coverSearchCenter,", body)
        self.assertIn("11f, crowdedCoverCandidates, Physics.DefaultRaycastLayers,", body)
        self.assertIn("Collider obstacle = candidates[i];", body)
        retry = body.split("if (count >= coverCandidates.Length)", 1)[1].split("}", 1)[0]
        self.assertIn("candidates = crowdedCoverCandidates;", retry)
        self.assertIn("count = Physics.OverlapSphereNonAlloc(coverSearchCenter,", retry)
        self.assertEqual(body.count("Physics.OverlapSphereNonAlloc("), 2)
        self.assertNotIn("Physics.OverlapSphere(", body)
        self.assertNotIn("new Collider[", body)
        self.assertLess(body.index("candidates = crowdedCoverCandidates;"),
                        body.index("Collider obstacle = candidates[i];"))

    def test_cover_los_and_existing_combat_bounds_retained(self):
        self.assertIn("!obstacle.Raycast(new Ray(coverFrom, coverRay / coverDistance)", COVER)
        self.assertIn("!owner.IsRivalCoverAvailable(candidate, spatialSlot, now)", COVER)
        self.assertIn("owner.WouldCrossInactiveCover(transform.position, displacement)", SOURCE)
        self.assertIn("private const int RivalCount = 100;", SOURCE)
        self.assertIn("return bot == attackerOne || bot == attackerTwo;", SOURCE)
        self.assertIn("private const float FarSimulationInterval = 0.35f;", SOURCE)


if __name__ == "__main__":
    unittest.main()
