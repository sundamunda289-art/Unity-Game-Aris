"""Regression tests for 17 garage marker+apron moves, 45 pads and 59 buildings."""
from __future__ import annotations
import copy
import json
import math
import re
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0,str(ROOT/"Tools"/"World"))
from smart_residential_clearance import (simulation,ClearanceError,
    HOUSE_FACTORS,box_gap,bay_box,house_box)
from verify_tree_house_upgrade import (SCENE_MOBILE_LOD_PATH,
    restore_reviewed_residential_vehicle_spawns)


class ResidentialVehicleSafetyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.layout=json.loads((ROOT/"Docs/MAP_LAYOUT.json").read_text())
        cls.manifest=json.loads((ROOT/"Tools/World/reviewed-residential-vehicle-spawns-20261105.json").read_text())
        cls.scene=(ROOT/SCENE_MOBILE_LOD_PATH).read_bytes()

    def test_all_45_spawn_and_17_paired_apron_changes(self):
        plan=simulation(self.layout)
        self.assertEqual(plan["total_buildings"],59)
        self.assertEqual(plan["total_spawns"],45)
        self.assertEqual(len(plan["relocated"]),17)
        self.assertEqual(plan["unchanged_spawns"],28)
        approved={r["spawn_name"]:r for r in self.manifest["rows"]}
        self.assertEqual(set(approved),{p["spawn_name"] for p in plan["relocated"]})
        for row in plan["relocated"]:
            m=approved[row["spawn_name"]]
            self.assertEqual(m["house_name"],row["house_name"])
            self.assertEqual(m["model"],row["model"])
            self.assertEqual(m["road_exit_xz_m"],row["road_exit_xz_m"])
            self.assertEqual(m["old_spawn_position_m"],row["original_position_m"])
            for a,b in zip(m["new_spawn_position_m"],row["new_position_m"]):
                self.assertAlmostEqual(a,b,places=6)
            self.assertAlmostEqual(m["shift_z_m"],row["delta_xz_m"][1],places=6)
            self.assertAlmostEqual(row["before_clearance_m"],
                                   row["after_clearance_m"],places=5)
            self.assertGreaterEqual(row["after_clearance_m"],0.25)
            self.assertLessEqual(abs(m["shift_z_m"]),1.5)
        self.assertIsNotNone(restore_reviewed_residential_vehicle_spawns(ROOT,self.scene))

    def test_exact_scene_pair_transform_bytes(self):
        owners={}
        transforms={}
        for block in re.split(rb"(?=^--- !u!)",self.scene,flags=re.M):
            m=re.match(rb"--- !u!1 &(\d+)\nGameObject:\n",block)
            if m:
                n=re.search(rb"^  m_Name: ([^\r\n]+)",block,flags=re.M)
                if n:owners[m[1]]=n[1].decode()
            m=re.match(rb"--- !u!4 &(\d+)\nTransform:\n",block)
            if m:
                g=re.search(rb"^  m_GameObject: \{fileID: (\d+)\}$",block,flags=re.M)
                if g and g[1] in owners:
                    transforms[owners[g[1]]]=block
        actual=set()
        for row in self.manifest["rows"]:
            for suffix,key in (("","marker"),("_Apron","apron")):
                target=row["spawn_name"]+suffix
                self.assertIn(target,transforms)
                self.assertIn(row[key]["updated_line"].encode(),transforms[target])
                self.assertNotIn(row[key]["original_line"].encode(),transforms[target])
                actual.add(target)
        self.assertEqual(len(actual),34)
        self.assertEqual(sum(bool(re.fullmatch(r"VehicleSpawnPoint_\d{2}$",n)) for n in transforms),45)
        self.assertEqual(sum(bool(re.fullmatch(r"VehicleSpawnPoint_\d{2}_Apron$",n)) for n in transforms),45)

    def test_future_single_house_scale_what_if_preserves_driveway(self):
        original=simulation(self.layout)
        spot="VehicleSpawnPoint_03"
        previous=next(x for x in original["relocated"] if x["spawn_name"]==spot)
        resized=simulation(self.layout,desired_house_scales={
            "ClockTowerTown_garage_apartment_06":1.15
        })
        changed=next(x for x in resized["relocated"] if x["spawn_name"]==spot)
        self.assertGreater(changed["new_position_m"][2],previous["new_position_m"][2])
        self.assertAlmostEqual(changed["before_clearance_m"],changed["after_clearance_m"],places=5)
        self.assertEqual(changed["road_exit_xz_m"],previous["road_exit_xz_m"])
        for invalid in ({"does-not-exist":1.2},
                        {"ClockTowerTown_garage_apartment_06":2},
                        {"ClockTowerTown_garage_apartment_06":float("nan")},
                        {"ClockTowerTown_garage_apartment_06":"huge"}):
            with self.assertRaises(ClearanceError):
                simulation(self.layout,desired_house_scales=invalid)

    def test_vehicle_marker_or_apron_tampering_fails_closed(self):
        row=self.manifest["rows"][0]
        for part in ("marker","apron"):
            source=row[part]["updated_line"].encode()
            old=row[part]["original_line"].encode()
            self.assertIn(source,self.scene)
            tampered=self.scene.replace(source,old,1)
            self.assertIsNone(restore_reviewed_residential_vehicle_spawns(ROOT,tampered))

    def test_block_unsafe_driveway_and_new_neighbour_obstacle(self):
        layout=copy.deepcopy(self.layout)
        target=next(s for s in layout["vehicle_spawns"] if s.get("serves") and
                    next(b for b in layout["buildings"] if b["name"]==s["serves"])["model"]=="garage_apartment")
        target["exit_world_xz_m"]=[target["position_m"][0]+3,target["position_m"][2]]
        with self.assertRaises(ClearanceError):
            simulation(layout)
        layout=copy.deepcopy(self.layout)
        # A wall placed in a garage access path MUST block the simulation.
        spot=next(s for s in layout["vehicle_spawns"] if s.get("serves") and
                  next(b for b in layout["buildings"] if b["name"]==s["serves"])["model"]=="garage_apartment")
        layout["buildings"].append({"name":"Test_Access_Blocker","model":"TownHall",
            "aabb_xz_m":[spot["position_m"][0]-2,spot["position_m"][2]-2,
                         spot["position_m"][0]+2,spot["position_m"][2]+2]})
        with self.assertRaises(ClearanceError):
            simulation(layout)


if __name__=="__main__":
    unittest.main()
