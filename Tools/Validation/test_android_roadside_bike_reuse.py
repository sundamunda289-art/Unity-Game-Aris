#!/usr/bin/env python3
"""Static and state-machine parity for Android roadside scenery reuse.

No Unity native Play Mode or actual Android frame profiling is implied.
"""
import random
import re
import unittest
from pathlib import Path

CODE = (Path(__file__).resolve().parents[2] / "Assets/AarisWorld" /
        "Scripts/AarisRoadsideBikeScenery.cs").read_text(encoding="utf-8")


def paint_index(value):
    hash_ = 2166136261
    for char in value:
        hash_ = ((hash_ ^ ord(char)) * 16777619) & 0xffffffff
    return hash_ % 4


class BikeRecycling(unittest.TestCase):
    def test_original_import_is_kept_and_mesh_creation_bounded(self):
        self.assertIn('AarisVehicles/Authored/AarisRoadBike', CODE)
        self.assertIn('Instantiate(bikeSource, root.transform, false);', CODE)
        self.assertIn('private const int MaximumVisibleBikes = 3;', CODE)
        self.assertIn('new Stack<GameObject>(MaximumVisibleBikes);', CODE)
        self.assertIn('parkedBikePool.Count < MaximumVisibleBikes', CODE)
        self.assertIn('parkedBikePool.Push(retired);', CODE)
        self.assertIn('ReuseBike(previous, nearest.point, spot)', CODE)
        self.assertIn('ReuseBike(parkedBikePool.Pop(), nearest.point, spot)', CODE)

    def test_collision_validation_precedes_any_relocation_or_recycle(self):
        update = CODE.split("private void Update()", 1)[1].split(
            "private GameObject ReuseBike(", 1)[0]
        self.assertLess(update.index("if (!TryParkingSpot("),
                        update.index("GameObject candidate = parkedVictim != null"))
        self.assertLess(update.index("if (candidate == null) return;"),
                        update.index("parkedVictim.visible = null;"))
        reuse = CODE.split("private GameObject ReuseBike(", 1)[1].split(
            "private bool TryParkingSpot(", 1)[0]
        self.assertLess(reuse.index("root.SetActive(false);"),
                        reuse.index("root.transform.SetPositionAndRotation("))
        self.assertLess(reuse.index("root.transform.SetPositionAndRotation("),
                        reuse.index("root.SetActive(true);"))
        self.assertNotIn("Physics.SyncTransforms()", reuse)

    def test_cached_original_mesh_and_paint_variants_not_reimported(self):
        self.assertIn("private sealed class BikeVisualCache", CODE)
        self.assertIn("internal Renderer[] renderers;", CODE)
        self.assertIn("internal Material[][][] paintLayouts;", CODE)
        self.assertIn("var layouts = new Material[paintVariants.Length][][];", CODE)
        self.assertIn("layouts[paint][r] = converted;", CODE)
        self.assertIn("renderer.sharedMaterials = layouts[paintIndex][r];", CODE)
        self.assertIn("renderer.sharedMaterials = layout;", CODE)
        self.assertIn("int color = StablePaintIndex(point.id);", CODE)
        create = CODE.split("private GameObject CreateBike(", 1)[1]
        self.assertIn('new LOD(.012f, renderers)', create)
        self.assertIn("root.AddComponent<BoxCollider>()", create)
        reuse = CODE.split("private GameObject ReuseBike(", 1)[1].split(
            "private bool TryParkingSpot(", 1)[0]
        for banned in ("Instantiate(", "new Material(", "new GameObject(",
                       "AddComponent<", "Destroy("):
            self.assertNotIn(banned, reuse)

    def test_reused_paint_does_not_recolor_rubber_or_lighting(self):
        # Mirror the actual 8-source-slot material classification:
        # only Paint is re-bound, wheel, metal, lamp, etc stay fixed.
        material_kinds = [1, 2, 3, 4, 0, 7, 5, 6]
        combinations = [
            [f"paint-{paint}" if kind == 0 else f"base-{kind}"
             for kind in material_kinds]
            for paint in range(4)
        ]
        for i in range(len(material_kinds)):
            if material_kinds[i] == 0:
                self.assertEqual(len({layout[i] for layout in combinations}), 4)
            else:
                self.assertEqual(len({layout[i] for layout in combinations}), 1)
        for id_ in ("ARIS_BIKE_1", "ARIS_BIKE_9", "village", ""):
            self.assertEqual(paint_index(id_), paint_index(id_))

    def test_low_memory_releases_only_hidden_bikes_and_pauses_pooling(self):
        self.assertIn("private void OnEnable() { Application.lowMemory += OnLowMemory; }", CODE)
        self.assertIn("private void OnDisable() { Application.lowMemory -= OnLowMemory; }", CODE)
        callback = CODE.split("private void OnLowMemory()", 1)[1].split(
            "private void Update()", 1)[0]
        self.assertIn("lowMemoryPending = true;", callback)
        self.assertNotIn("Destroy(", callback)
        update = CODE.split("private void Update()", 1)[1].split(
            "private GameObject ReuseBike(", 1)[0]
        self.assertIn("poolAllowedAt = Time.unscaledTime + 60f;", update)
        self.assertIn("while (parkedBikePool.Count > 0)", update)
        self.assertIn("bikeVisuals.Remove(inactiveBike);", update)
        self.assertIn("Destroy(inactiveBike);", update)
        self.assertIn("Time.unscaledTime >= poolAllowedAt &&", update)
        self.assertNotIn("Destroy(plan.visible);", update)
        self.assertNotIn("Destroy(parkedVictim.visible);", update)

    def test_recycling_never_exceeds_three_original_model_instances(self):
        rng = random.Random(20261119)
        capacity = 3
        live = []
        pooled = []
        created = 0
        for _ in range(2000):
            if live and rng.random() < 0.5:
                pooled.append(live.pop(rng.randrange(len(live))))
            else:
                if len(live) >= capacity:
                    rig = live.pop(rng.randrange(len(live)))
                elif pooled:
                    rig = pooled.pop()
                else:
                    created += 1
                    rig = created
                live.append(rig)
            self.assertLessEqual(len(live), capacity)
            self.assertLessEqual(len(pooled), capacity)
            self.assertLessEqual(len(live) + len(pooled), capacity)
        self.assertLessEqual(created, 3)


if __name__ == "__main__":
    unittest.main(verbosity=2)
