#!/usr/bin/env python3
"""License-free snapshot client contract tests (no Unity Editor needed)."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

SRC = Path(__file__).resolve().parents[1] / "Codespaces/snapshot_cli.py"
spec = importlib.util.spec_from_file_location("aaris_snapshot_cli", SRC)
snap = importlib.util.module_from_spec(spec)
spec.loader.exec_module(snap)


class SnapProtocolTests(unittest.TestCase):
    def test_good_shot(self):
        shot = snap.normalize_shot({"id":"forest-01","pos":"120,18,340",
                                     "lookAt":[150,12,400]})
        out = snap.unity_shot(shot)
        self.assertEqual((out["x"], out["lookZ"]), (120,400))
        self.assertEqual((out["width"], out["height"]), (1280,720))

    def test_reject_bad_coordinates_and_ids(self):
        for pos in ("nan,3,2", "1001,2,3", "0,0,0,0", [True,2,3], "3e309,2,3"):
            with self.subTest(pos=pos), self.assertRaises(ValueError):
                snap.normalize_shot({"id":"forest","pos":pos,"lookAt":[5,4,5]})
        for name in ("../scene", "a/b", "", "🔑", "a"*49):
            with self.subTest(name=name), self.assertRaises(ValueError):
                snap.normalize_shot({"id":name,"pos":[1,2,3],"lookAt":[4,5,6]})

    def test_invalid_size_and_self_look(self):
        base = {"id":"forest","pos":[0,2,0],"lookAt":[0,2,0]}
        with self.assertRaises(ValueError):
            snap.normalize_shot(base)
        base["lookAt"] = [0,3,0]
        for w,h in ((1921,1080),(1280,1081),(0,720),(True,500)):
            with self.subTest(size=(w,h)), self.assertRaises(ValueError):
                snap.normalize_shot(dict(base,width=w,height=h))

    def test_presets_and_batch(self):
        shot=snap.normalize_shot({"id":"forest","pos":[1,2,3],"lookAt":[4,5,6]})
        with tempfile.TemporaryDirectory() as d:
            f=Path(d)/"presets.json"
            snap.save_presets(f,[shot])
            self.assertEqual(snap.read_presets(f)["forest"],shot)
            batch=Path(d)/"shots.json"
            snap.atomic_json(batch,{"shots":[shot]})
            self.assertEqual(snap.load_batch(batch),[shot])
            snap.atomic_json(batch,{"shots":{}})
            with self.assertRaises(ValueError):
                snap.load_batch(batch)


if __name__ == "__main__":
    unittest.main()
