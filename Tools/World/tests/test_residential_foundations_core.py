"""Native Foundation planes must cover each expanded house while keeping centre.

This verifies serialized coordinates only; it is NOT a Unity render/physics test.
"""
import json
import re
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0,str(ROOT / "Tools" / "World"))
from verify_tree_house_upgrade import restore_reviewed_residential_foundations


class ResidentialFoundationsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.scene=(ROOT/"Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity").read_bytes()
        cls.plan=json.loads((ROOT/"Tools/World/reviewed-residential-foundations-20261105.json").read_text())
        cls.layout=json.loads((ROOT/"Docs/MAP_LAYOUT.json").read_text())

    def test_all_47_foundations_expand_around_exact_original_world_centres(self):
        data={row["name"]:row for row in self.plan["changes"]}
        self.assertEqual(len(data),47)
        owners={}
        transforms={}
        for block in re.split(rb"(?=^--- !u!)",self.scene,flags=re.M):
            m=re.match(rb"--- !u!1 &(\d+)\nGameObject:\n",block)
            if m:
                n=re.search(rb"^  m_Name: ([^\r\n]+)",block,flags=re.M)
                if n:owners[m[1]]=n[1].decode()
            m=re.match(rb"--- !u!4 &(\d+)\nTransform:\n",block)
            if m:
                transforms[m[1]]=block
        found={}
        for block in transforms.values():
            match=re.search(rb"^  m_GameObject: \{fileID: (\d+)\}$",block,flags=re.M)
            if not match or match[1] not in owners:continue
            name=owners[match[1]]
            if name.endswith("_Foundation") and name[:-11] in data:
                self.assertNotIn(name,found)
                found[name]=block
        self.assertEqual(len(found),47)
        for b in self.layout["buildings"]:
            if b["name"] not in data:continue
            row=data[b["name"]]
            block=found[b["name"]+"_Foundation"]
            for key,text in (
                ("scaleX",row["scaleX"]),("scaleZ",row["scaleZ"])):
                self.assertGreater(float(text),1.0)
            self.assertIn(
                ("  m_LocalScale: {x: "+row["scaleX"]+
                 ", y: 1.0, z: "+row["scaleZ"]+"}").encode(),block)
            self.assertIn(
                ("  m_LocalPosition: {x: "+row["positionX"]+
                 ", y: 0.0, z: "+row["positionZ"]+"}").encode(),block)
            x,_,z=b["position_m"]
            sx,sz,px,pz=[float(row[k]) for k in
                         ("scaleX","scaleZ","positionX","positionZ")]
            self.assertAlmostEqual(sx*x+px,x,places=5)
            self.assertAlmostEqual(sz*z+pz,z,places=5)
            oldw,oldd=row["footprintBefore"];neww,newd=row["footprintAfter"]
            self.assertAlmostEqual((oldw+2)*sx,neww+2,places=5)
            self.assertAlmostEqual((oldd+2)*sz,newd+2,places=5)
        self.assertIsNotNone(restore_reviewed_residential_foundations(ROOT,self.scene))

    def test_unexpected_translation_and_partial_edit_fail_closed(self):
        source=self.scene
        marker=("  m_LocalPosition: {x: "+self.plan["changes"][0]["positionX"]+
                ", y: 0.0, z: "+self.plan["changes"][0]["positionZ"]+"}").encode()
        self.assertIn(marker,source)
        tampered=source.replace(marker,b"  m_LocalPosition: {x: 0.0, y: 0.0, z: 0.0}",1)
        self.assertIsNone(restore_reviewed_residential_foundations(ROOT,tampered))
        width=self.plan["changes"][0]["scaleX"]
        scalar=("  m_LocalScale: {x: "+width+
                ", y: 1.0, z: "+self.plan["changes"][0]["scaleZ"]+"}").encode()
        self.assertIn(scalar,source)
        tampered=source.replace(scalar,b"  m_LocalScale: {x: 1.0, y: 1.0, z: 1.0}",1)
        self.assertIsNone(restore_reviewed_residential_foundations(ROOT,tampered))


if __name__=="__main__":
    unittest.main()
