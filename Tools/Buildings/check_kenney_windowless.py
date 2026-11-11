#!/usr/bin/env python3
"""Asset-level regression tests for Aaris's author-time CC0 houses.

No Unity installation is required. The source pack is verified against its
pinned SHA-256 and license before the generated game meshes are inspected.
"""
import contextlib
import importlib.util
import io
import math
import pathlib
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "Tools/Buildings/generate_kenney_windowless.py"
spec = importlib.util.spec_from_file_location("aaris_windowless", SCRIPT)
generator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generator)


def read_obj(path):
    vertices, uvs, faces, materials = [], [], [], set()
    for line in path.read_text().splitlines():
        bits = line.split()
        if not bits:
            continue
        tag = bits[0]
        if tag == "v":
            vertices.append(tuple(map(float, bits[1:4])))
        elif tag == "vt":
            uvs.append(tuple(map(float, bits[1:3])))
        elif tag == "usemtl":
            materials.add(bits[1])
        elif tag == "f":
            faces.append(tuple(tuple(map(int, token.split("/")[:2]))
                for token in bits[1:]))
    return vertices, uvs, faces, materials


def segment_triangle_hit(origin, vec, a, b, c):
    """Möller–Trumbore; return whether the bounded segment hits a triangle."""
    eps = 1.0e-7

    def sub(x, y):
        return tuple(i-j for i, j in zip(x, y))

    def dot(x, y):
        return sum(i*j for i, j in zip(x, y))

    def cross(x, y):
        return (x[1]*y[2]-x[2]*y[1],
                x[2]*y[0]-x[0]*y[2],
                x[0]*y[1]-x[1]*y[0])

    e1, e2 = sub(b, a), sub(c, a)
    p = cross(vec, e2)
    det = dot(e1, p)
    if abs(det) < eps:
        return False
    inv = 1.0 / det
    tvec = sub(origin, a)
    u = dot(tvec, p) * inv
    if u < -eps or u > 1.0+eps:
        return False
    q = cross(tvec, e1)
    v = dot(vec, q) * inv
    if v < -eps or u+v > 1.0+eps:
        return False
    t = dot(e2, q) * inv
    return eps < t < 1.0-eps


def ray_hits_mesh(vertices, faces, origin, delta):
    for face in faces:
        poly = [vertices[v-1] for v, uv in face]
        for i in range(1, len(poly)-1):
            if segment_triangle_hit(origin, delta,
                                    poly[0], poly[i], poly[i+1]):
                return True
    return False


class WindowlessHousesTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.folder = pathlib.Path(cls.temp.name)
        generator.OUT = cls.folder
        with contextlib.redirect_stdout(io.StringIO()):
            generator.main()

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    def test_source_is_licensed(self):
        license_text = (self.folder / "Kenney-CC0-License.txt").read_text()
        self.assertIn("Creative Commons Zero, CC0", license_text)
        self.assertIn("kenney", (self.folder / "PROVENANCE.md").read_text().lower())
        self.assertTrue((self.folder / "colormap.png").stat().st_size > 1000)

    def test_all_seven_are_single_mesh_compact_and_without_windows(self):
        names = [name for name, _, _ in generator.STYLES]
        self.assertEqual(len(names), 7)
        self.assertEqual(len(names), len(set(names)))
        for name in names:
            with self.subTest(style=name):
                path = self.folder / ("AarisWindowless_" + name + ".obj")
                self.assertTrue(path.is_file(), str(path))
                vertices, uvs, faces, materials = read_obj(path)
                self.assertGreater(len(vertices), 20)
                self.assertLess(len(vertices), 1000,
                                "Unexpectedly heavy house vertices")
                self.assertLess(len(faces), 600,
                                "Unexpectedly heavy house polygons")
                self.assertGreater(len(uvs), 0)
                self.assertEqual(materials, {"colormap"})
                content = path.read_text().lower()
                self.assertNotIn("usemtl glass", content)
                self.assertNotIn("door-leaf", content)
                self.assertNotIn("window-pane", content)
                for face in faces:
                    self.assertGreaterEqual(len(face), 3)
                    for vi, ui in face:
                        self.assertTrue(1 <= vi <= len(vertices))
                        self.assertTrue(1 <= ui <= len(uvs))
                for vertex in vertices:
                    self.assertTrue(all(math.isfinite(x) for x in vertex))

    def test_seven_facade_styles_share_one_atlas_without_extra_materials(self):
        self.assertEqual(len(generator.FACADE_U), 7)
        self.assertEqual(len(set(generator.FACADE_U.values())), 7)
        for name, _, _ in generator.STYLES:
            with self.subTest(style=name):
                _, uvs, _, materials = read_obj(
                    self.folder / ("AarisWindowless_" + name + ".obj"))
                self.assertEqual(materials, {"colormap"})
                palette_u = generator.FACADE_U[name]
                self.assertTrue(any(abs(u-palette_u) < 1e-6 for u, _ in uvs))
                # Keep samples away from atlas edges to avoid black bleed.
                self.assertTrue(0.10 < palette_u < 0.96)
        self.assertEqual(len(list(self.folder.glob("*.png"))), 1,
                         "Only one shared texture is allowed")

    def test_front_aperture_is_truly_open_but_front_walls_block(self):
        for name, _, _ in generator.STYLES:
            with self.subTest(style=name):
                vertices, _, faces, _ = read_obj(
                    self.folder / ("AarisWindowless_" + name + ".obj"))
                self.assertFalse(ray_hits_mesh(vertices, faces,
                    (0.0, 1.5, -4.6), (0.0, 0.0, 1.2)),
                    "The intended open doorway is physically plugged")
                self.assertTrue(ray_hits_mesh(vertices, faces,
                    (3.0, 1.5, -4.6), (0.0, 0.0, 1.2)),
                    "The front solid wall has a hole where no opening belongs")

    def test_exact_native_scene_hierarchy_and_47_houses(self):
        import collections
        import re
        scene_path = (ROOT / "Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity")
        blocks = re.split(r"(?=^--- !u!)", scene_path.read_text(), flags=re.M)
        components, gameobjects = {}, {}
        for block in blocks:
            head = re.match(r"^--- !u!(\d+) &(\d+)", block)
            if not head:
                continue
            kind, file_id = int(head.group(1)), head.group(2)
            components[file_id] = (kind, block)
            if kind == 1:
                name = re.search(r"^  m_Name: (.+)$", block, re.M)
                if name:
                    gameobjects[file_id] = (name.group(1), block)
        pattern = re.compile(
            r"^.+_(?:bungalow_house|pitched_roof_cottage|a_frame_cabin|"
            r"detached_two_storey_house|garage_apartment|"
            r"two_bedroom_garage_house|row_three_townhouses)_\d{2}$")
        houses = [entry for entry in gameobjects.values()
                  if pattern.fullmatch(entry[0])]
        self.assertEqual(len(houses), 47,
                         "House replacement must be re-audited if map changes")
        for name, body in houses:
            refs = re.findall(r"^  - component: {fileID: (\d+)}", body, re.M)
            kinds = [components[ref][0] for ref in refs]
            self.assertCountEqual(kinds, [4, 33, 23, 64, 205], name)
            transform = next(components[ref][1]
                             for ref in refs if components[ref][0] == 4)
            children = re.findall(r"^  - {fileID: (\d+)}", transform, re.M)
            self.assertEqual(len(children), 2, name)
            actual = []
            for child in children:
                self.assertEqual(components[child][0], 4)
                obj = re.search(
                    r"^  m_GameObject: {fileID: (\d+)}",
                    components[child][1], re.M)
                self.assertIsNotNone(obj, name)
                actual.append(gameobjects[obj.group(1)][0])
            self.assertCountEqual(
                actual, ["ReviewLOD_" + name + "_1",
                         "ReviewLOD_" + name + "_2"], name)
        baker = (ROOT / "Assets/AarisWorld/Editor/"
                 "AarisKenneyWindowlessBaker.cs").read_text()
        self.assertIn('"ReviewLOD_" + source.name + "_1"', baker)
        self.assertIn('"ReviewLOD_" + source.name + "_2"', baker)
        self.assertIn("Undo.DestroyObjectImmediate(lod1.gameObject)", baker)
        self.assertIn("Undo.DestroyObjectImmediate(lod2.gameObject)", baker)
        self.assertIn("houses.Length != 47", baker)

    def test_a_single_urplit_material_is_used_by_editor_baker(self):
        path = ROOT / "Assets/AarisWorld/Editor/AarisKenneyWindowlessBaker.cs"
        code = path.read_text()
        self.assertIn('Shader.Find("Universal Render Pipeline/Lit")', code)
        self.assertIn("PrefabUtility.SaveAsPrefabAsset", code)
        self.assertIn("EditorSceneManager.SaveScene(scene)", code)
        self.assertIn("source.transform.Find(ChildName)", code)
        self.assertNotIn("MeshCollider c = home.AddComponent", code)
        self.assertIn("AddBox(home,", code)
        self.assertIn("Windowless", code)


if __name__ == "__main__":
    unittest.main(verbosity=2)
