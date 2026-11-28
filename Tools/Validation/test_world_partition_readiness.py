#!/usr/bin/env python3
"""Contract tests for the real read-only Unity scene partition preflight."""
import importlib.util
from collections import Counter
from pathlib import Path
import re
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "Tools/Validation/world_partition_readiness.py"
spec = importlib.util.spec_from_file_location("world_partition_readiness", SCRIPT)
mod = importlib.util.module_from_spec(spec)
import sys
sys.modules[spec.name] = mod
spec.loader.exec_module(mod)


def native_inventory(path, prefabs):
    """Count instantiated prefab components, not only their YAML aliases.

    This test intentionally stays standard-library-only for its existing CI
    runner. It is a bounded inventory, not a replacement for the full saved
    mesh/material/pose validator or Unity's native serialization checks.
    """
    scene = mod.WorldScene(path.read_text())
    counts = Counter(block.kind for block in scene.blocks.values()
                     if not mod._HEADER.search(block.body).group(3))
    instances = {}
    for uid, block in scene.blocks.items():
        if block.kind != 1001:
            continue
        match = re.search(r'm_SourcePrefab: \{fileID: 100100000, guid: ([0-9a-f]{32}), type: 3\}', block.body)
        assert match and match[1] in prefabs, ('Unresolved inventory prefab', path, uid)
        guid = match[1]
        source = mod.WorldScene(prefabs[guid].read_text())
        assert all(b.kind != 1001 for b in source.blocks.values()), 'Nested prefab requires native inventory support'
        for field in ('m_AddedComponents', 'm_AddedGameObjects', 'm_RemovedGameObjects'):
            assert re.search(r'(?m)^    ' + field + r': \[\]$', block.body), ('Unsupported structural override', field)
        for target, target_guid, prop in re.findall(
                r'target: \{fileID: (-?\d+), guid: ([0-9a-f]{32}), type: 3\}\n      propertyPath: ([^\n]+)', block.body):
            assert target_guid == guid and int(target) in source.blocks, ('Unresolved inventory override', path, target)
            assert prop in ('m_Name', 'm_StaticEditorFlags') or re.fullmatch(
                r'm_Local(?:Position|Scale|Rotation|EulerAnglesHint)\.[xyzw]', prop), ('Unsupported inventory property', prop)
        removed = set()
        section = block.body.split('    m_RemovedComponents:', 1)[1].split('    m_RemovedGameObjects:', 1)[0]
        for identity, removal_guid in re.findall(r'fileID: (-?\d+), guid: ([0-9a-f]{32}), type: 3', section):
            identity = int(identity)
            assert removal_guid == guid and identity in source.blocks, ('Unresolved removed component', path, identity)
            assert source.blocks[identity].kind in (64, 65, 135, 136, 143, 146, 154), 'Only collision removal is supported'
            assert identity not in removed, 'Duplicate removed component'
            removed.add(identity)
        counts.update(b.kind for identity, b in source.blocks.items() if identity not in removed)
        instances[uid] = (guid, source, removed)
    aliases = set()
    for uid, block in scene.blocks.items():
        if not mod._HEADER.search(block.body).group(3):
            continue
        alias = re.search(r'm_CorrespondingSourceObject: \{fileID: (-?\d+), guid: ([0-9a-f]{32}), type: 3\}', block.body)
        instance = re.search(r'm_PrefabInstance: \{fileID: (-?\d+)\}', block.body)
        assert alias and instance and int(instance[1]) in instances, ('Unresolved stripped alias', path, uid)
        guid, source, removed = instances[int(instance[1])]
        identity = int(alias[1])
        assert alias[2] == guid and identity in source.blocks and identity not in removed, ('Invalid stripped source', path, uid)
        assert block.kind == source.blocks[identity].kind, ('Stripped component type mismatch', path, uid)
        key = (int(instance[1]), identity)
        assert key not in aliases, ('Duplicate stripped source alias', path, uid)
        aliases.add(key)
    return counts


def active_detail_paths(root):
    text = (root / 'ProjectSettings/EditorBuildSettings.asset').read_text()
    entries = re.findall(r'(?m)^  - enabled: 1\n    path: ([^\n]+)\n    guid: ([0-9a-f]{32})$', text)
    assert entries and entries[0][0] == mod.SCENE, 'Missing bootstrap build scene'
    paths = [path for path, guid in entries]
    assert len(paths) == len(set(paths)), 'Duplicate active scene'
    for path, guid in entries:
        assert (root / path).is_file(), ('Missing active scene', path)
        assert re.search(r'(?m)^guid: ' + guid + '$', Path(str(root / path) + '.meta').read_text()), ('Build scene GUID mismatch', path)
    registered = re.findall(r'(?m)^  - scenePath: ([^\n]+)$', (root / mod.SCENE).read_text())
    assert len(registered) == len(set(registered)) and set(registered) == set(paths[1:]), 'Streamer/build manifest differs'
    return [root / path for path in paths[1:]]


def baked_static_inventory(root):
    """Exclude only verified presentation cohorts from the original inventory.

    This remains a standard-library structural/reference check. Native bake
    and reload gates verify the generated mesh buffers and world matrices.
    A matching name alone never exempts an object, collider or original root.
    """
    scene = mod.WorldScene((root / mod.SCENE).read_text())
    prefix = '__AarisBakedStatic_'
    hosts = {go for go, name in scene.names.items() if name.startswith(prefix)}
    script_path = root / 'Assets/AarisWorld/Scripts/AarisBakedStaticBatch.cs.meta'

    def guid(path):
        matches = re.findall(r'(?m)^guid: ([0-9a-f]{32})$', path.read_text())
        assert len(matches) == 1, ('Invalid asset metadata', path)
        return matches[0]

    def scalar(body, name, indent=2):
        matches = re.findall(r'(?m)^' + ' ' * indent + re.escape(name) + r':([^\n]*)$', body)
        assert len(matches) == 1, ('Missing/duplicate cohort field', name)
        return matches[0].strip()

    def section(body, name, indent=2):
        head = re.search(r'(?m)^' + ' ' * indent + re.escape(name) + r':([^\n]*)\n', body)
        assert head and scalar(body, name, indent) in ('', '[]'), ('Invalid cohort array', name)
        tail = body[head.end():]
        end = re.search(r'(?m)^' + ' ' * indent + r'[A-Za-z_][A-Za-z0-9_]*:', tail)
        return tail[:end.start()] if end else tail

    def array(body, name, pattern, indent=2):
        lines = [line for line in section(body, name, indent).splitlines() if line.strip()]
        matches = [re.fullmatch(' ' * indent + r'- ' + pattern, line) for line in lines]
        assert all(matches), ('Unsupported cohort array entry', name)
        return [match.groups() for match in matches]

    local = r'\{fileID: (-?\d+)\}'
    asset = r'\{fileID: (-?\d+), guid: ([0-9a-f]{32}), type: ([23])\}'
    script = guid(script_path) if script_path.exists() else None
    controllers = {uid: block for uid, block in scene.blocks.items() if block.kind == 114 and script and
                   scalar(block.body, 'm_Script') == '{fileID: 11500000, guid: ' + script + ', type: 3}'}
    if not hosts:
        assert not controllers, 'Unowned presentation controller'
        return Counter(), (0, 0, 0, 0, 0)
    assert script and len(hosts) == len(controllers), 'Generated host/controller count mismatch'
    assert {scene.names[go] for go in hosts} == {prefix + f'{i:03d}' for i in range(len(hosts))}, \
        'Missing/duplicate generated cohort name'
    derived = set()
    for host in hosts:
        assert scene.parent_go[host] is None, 'Generated host must not reparent an original root'
        members = scene.descendants(host)
        assert members == {host, *scene.children[host]}, 'Generated output must be a direct leaf child'
        assert not derived.intersection(members), 'Overlapping generated cohorts'
        derived.update(members)
    roots = [block for block in scene.blocks.values() if block.kind == 1660057539]
    assert len(roots) == 1, 'Generated hosts require one saved SceneRoots record'
    root_links = [int(row[0]) for row in array(roots[0].body, 'm_Roots', local)]
    assert all(root_links.count(scene.go_to_transform[host]) == 1 for host in hosts), \
        'Generated host is missing or duplicated in SceneRoots'
    by_owner = {}
    for uid, owner in scene.owner.items():
        if uid != owner:
            by_owner.setdefault(owner, []).append(uid)

    def components(go, expected):
        body = scene.blocks[go].body
        assert scalar(body, 'm_IsActive') == '1' and scalar(body, 'm_StaticEditorFlags') == '0', \
            'Generated object must be active with no baked static flags'
        linked = [int(row[0]) for row in array(body, 'm_Component', r'component: ' + local)]
        assert len(linked) == len(set(linked)) and set(linked) == set(by_owner.get(go, [])), \
            'Generated component ownership mismatch'
        assert Counter(scene.blocks[uid].kind for uid in linked) == expected, \
            'Unexpected component in generated presentation subtree'
        return {scene.blocks[uid].kind: uid for uid in linked}

    active_paths = {str(path.relative_to(root)) for path in active_detail_paths(root)}
    fallback_guid = guid(root / 'Assets/AarisWorld/Scripts/AarisChunkFallbackVisibility.cs.meta')
    fallbacks = {}
    for uid, block in scene.blocks.items():
        if block.kind == 114 and scalar(block.body, 'm_Script') == \
                '{fileID: 11500000, guid: ' + fallback_guid + ', type: 3}':
            owner = scene.owner[uid]
            assert owner not in fallbacks, 'Duplicate fallback owner'
            fallbacks[owner] = (scalar(block.body, 'chunkScenePath'),
                                {int(row[0]) for row in array(block.body, 'silhouetteRenderers', local)})
    meshes = {}
    generated_path = root / 'Assets/AarisWorld/Generated/StaticBatches'
    for meta in generated_path.glob('*.asset.meta'):
        assert re.fullmatch(r'Cohort_\d{3}_\d{3}\.asset\.meta', meta.name), 'Unexpected generated mesh name'
        identity = guid(meta)
        assert identity not in meshes, 'Duplicate generated mesh GUID'
        path = meta.with_suffix('')
        assert path.is_file() and path.stat().st_size > 0, ('Missing generated mesh asset', path)
        meshes[identity] = path
    sources_seen, outputs_seen, used_meshes = set(), set(), set()
    draws_total = fallback_count = 0
    consumed = set()
    for uid, block in controllers.items():
        host = scene.owner.get(uid)
        assert host in hosts and components(host, Counter({4: 1, 114: 1}))[114] == uid, 'Wrong cohort owner'
        assert scalar(block.body, 'm_Enabled') == '1', 'Disabled cohort controller'
        transform = scene.go_to_transform[host]
        outputs = [int(row[0]) for row in array(block.body, 'outputs', local)]
        assert outputs and len(outputs) == len(set(outputs)) and not outputs_seen.intersection(outputs), \
            'Duplicate/empty generated output ownership'
        output_meshes = array(block.body, 'outputMeshes', asset)
        output_materials = array(block.body, 'outputMaterials', asset)
        assert len(outputs) == len(output_meshes) == len(output_materials), 'Incomplete generated output snapshots'
        path = scalar(block.body, 'fallbackScenePath')
        assert not path or path in active_paths, 'Cohort fallback scene is not in the active manifest'
        fallback_count += bool(path)
        parts = re.split(r'(?m)^  - renderer: ', section(block.body, 'sources'))
        assert not parts[0].strip() and len(parts) > 2, 'Cohort must replace at least two source renderers'
        materials_seen = set()
        draw_count = 0
        for part in parts[1:]:
            match = re.match(local + r'\n', part)
            assert match, 'Invalid cohort source reference'
            source = int(match[1])
            assert source in scene.blocks and scene.blocks[source].kind == 23 and \
                not mod._HEADER.search(scene.blocks[source].body).group(3), 'Unresolved cohort source renderer'
            owner = scene.owner.get(source)
            assert owner in scene.names and owner not in derived and source not in sources_seen, \
                'Duplicate/generated source ownership'
            sources_seen.add(source)
            source_body = scene.blocks[source].body
            assert scalar(source_body, 'm_Enabled') == '1', 'Disabled original cohort source'
            actual = array(source_body, 'm_Materials', asset)
            assert actual and actual == array(part, 'materials', asset, 4), 'Original source material snapshot changed'
            filters = [item for item in by_owner[owner] if scene.blocks[item].kind == 33]
            assert len(filters) == 1 and scalar(scene.blocks[filters[0]].body, 'm_Mesh') == \
                scalar(part, 'authoredMesh', 4), 'Original source mesh snapshot changed'
            materials_seen.update(actual)
            draw_count += len(actual)
            ancestors = []
            node = owner
            while node is not None:
                assert node not in ancestors, 'Source hierarchy cycle'
                ancestors.append(node)
                node = scene.parent_go[node]
            visible_owners = [fallbacks[node] for node in ancestors if node in fallbacks]
            if path:
                assert len(visible_owners) == 1 and visible_owners[0][0] == path and \
                    source in visible_owners[0][1], 'Cohort source has different fallback ownership'
            else:
                assert not visible_owners, 'Persistent cohort contains streamed fallback source'
        assert int(scalar(block.body, 'sourceSubmeshDraws')) == draw_count > len(outputs), \
            'Incomplete or non-beneficial cohort draw coverage'
        assert materials_seen == set(output_materials), 'Generated output material coverage differs'
        draws_total += draw_count
        output_children = []
        suffix = scene.names[host][len(prefix):]
        for index, output in enumerate(outputs):
            assert output in scene.blocks and scene.blocks[output].kind == 23, 'Missing generated output renderer'
            owner = scene.owner.get(output)
            assert owner in derived and scene.parent_go[owner] == host and not scene.children[owner], \
                'Generated output is not an owned leaf'
            linked = components(owner, Counter({4: 1, 23: 1, 33: 1}))
            assert linked[23] == output and scene.names[owner] == f'Material_{index:03d}', 'Generated output order differs'
            body = scene.blocks[output].body
            assert scalar(body, 'm_Enabled') == '0' and array(body, 'm_Materials', asset) == [output_materials[index]], \
                'Generated output must remain disabled with its authored material'
            mesh = re.fullmatch(asset, scalar(scene.blocks[linked[33]].body, 'm_Mesh'))
            assert mesh and mesh.groups() == output_meshes[index], 'Generated mesh snapshot mismatch'
            identity = mesh[2]
            assert mesh[1] == '4300000' and mesh[3] == '2' and identity in meshes and identity not in used_meshes, \
                'Unowned/duplicate generated mesh reference'
            assert meshes[identity].name == f'Cohort_{suffix}_{index:03d}.asset', 'Generated mesh has a different cohort owner'
            used_meshes.add(identity)
            output_children.append(linked[4])
            consumed.update((owner, *linked.values()))
        assert set(scene.children[host]) == {scene.owner[output] for output in outputs}, 'Unreferenced generated child'
        declared_children = [int(row[0]) for row in array(scene.blocks[transform].body, 'm_Children', local)]
        assert declared_children == output_children, 'Generated child hierarchy order differs'
        outputs_seen.update(outputs)
        consumed.update((host, transform, uid))
    assert used_meshes == set(meshes), 'Unreferenced generated mesh asset'
    assert consumed == {uid for uid, owner in scene.owner.items() if owner in derived}, 'Unaccounted generated component'
    counts = Counter(scene.blocks[uid].kind for uid in consumed)
    return counts, (len(hosts), len(sources_seen), draws_total, fallback_count, len(hosts) - fallback_count)


def obj(kind, uid, body):
    return "--- !u!{} &{}\n{}\n".format(kind, uid, body)


def go(uid, name):
    return obj(1, uid, "GameObject:\n  m_Name: " + name)


def transform(uid, goid, parent):
    return obj(4, uid,
               "Transform:\n  m_GameObject: {fileID: %d}\n"
               "  m_Father: {fileID: %d}" % (goid, parent))


def fixture():
    blocks = [
        go(1, "Aaris_Island_2km"), transform(2, 1, 0),
        go(3, "Districts"), transform(4, 3, 2),
        go(5, "Terrain_2x2"), transform(6, 5, 2),
        go(7, "BattleRoyaleProps"), transform(8, 7, 0),
        go(9, "FoundationsAndAprons"), transform(10, 9, 2),
        go(11, "RoadNetwork"), transform(12, 11, 2),
        go(13, "DemoDistrict"), transform(14, 13, 4),
        go(15, "House"), transform(16, 15, 14),
        go(17, "ReviewLOD_House_1"), transform(18, 17, 16),
        go(19, "MobileRuntime"), transform(20, 19, 0),
        go(26, "Terrain_1_1"), transform(27, 26, 6),
        obj(64, 21, "MeshCollider:\n  m_GameObject: {fileID: 15}"),
        obj(205, 22,
            "LODGroup:\n  m_GameObject: {fileID: 15}\n"
            "  m_LODs: [{renderers: [{fileID: 24}]}]"),
        obj(23, 24, "MeshRenderer:\n  m_GameObject: {fileID: 17}"),
        obj(114, 25,
            "MonoBehaviour:\n  m_GameObject: {fileID: 19}\n"
            "  m_AuthoritativeHouseCollider: {fileID: 21}"),
        obj(218, 28, "Terrain:\n  m_GameObject: {fileID: 26}")
    ]
    return "%YAML 1.1\n" + "".join(blocks)


class WorldPartitionReadiness(unittest.TestCase):
    def test_detects_external_collider_reference_without_lod_false_positive(self):
        scene = mod.WorldScene(fixture())
        district = scene.find_unique("DemoDistrict")
        out, incoming = scene.semantic_cross_refs(scene.descendants(district))
        self.assertEqual(len(incoming), 1)
        self.assertEqual(len(out), 0)
        self.assertEqual(next(iter(incoming)), (25, 21))
        report = scene.report()
        self.assertEqual(len(report["districts"]), 1)
        d = report["districts"][0]
        self.assertEqual(d["lod_groups"], 1)
        self.assertEqual(d["mesh_colliders"], 1)
        self.assertFalse(d["direct_move_safe"])
        self.assertFalse(report["ready_for_district_migration"])

    def test_invalid_scene_references_fail_closed(self):
        text = fixture()
        self.assertRaises(ValueError, mod.WorldScene,
                          text + go(1, "Duplicate"))
        self.assertRaises(ValueError, mod.WorldScene,
                          text.replace("m_Father: {fileID: 16}",
                                       "m_Father: {fileID: 9999}"))

    def test_strict_flag_refuses_unproven_migration(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "demo.unity"
            path.write_text(fixture())
            self.assertEqual(mod.main(["--scene", str(path), "--require-safe"]), 1)
            self.assertEqual(mod.main(["--scene", str(path)]), 0)

    def test_live_world_has_all_authored_visual_and_collision_dependencies(self):
        report = mod.audit_scene(ROOT / mod.SCENE)
        self.assertEqual(report["terrain_objects"], 4)
        native_pilot = (
            ROOT / "Assets/AarisWorld/Scenes/Chunks/Aaris_ForestLookout_Detail.unity"
        ).is_file()
        # Native Airy prefabs now hold most GameObjects/LODs outside scene YAML.
        # Resolve their counts and removed-collider overrides; archive scenes
        # preserve the 94 authored visual roots after 150m sector rebaking.
        folder = ROOT / "Assets/AarisWorld/Scenes/Chunks"
        district_scenes = sorted(folder.glob("Aaris_District_*_Detail.unity"))
        if district_scenes:
            self.assertTrue(native_pilot)
            self.assertEqual(len(district_scenes), 12)
            prefabs = {}
            for meta in (ROOT / 'Assets').rglob('*.prefab.meta'):
                guid = re.search(r'(?m)^guid: ([0-9a-f]{32})$', meta.read_text())[1]
                self.assertNotIn(guid, prefabs)
                prefabs[guid] = meta.with_suffix('')
            archives = Counter()
            for path in district_scenes:
                archives.update(native_inventory(path, prefabs))
            self.assertEqual(archives[1], 1464)
            self.assertEqual(archives[205], 93)
            self.assertEqual(archives[23], 279)
            self.assertEqual(archives[64], 0)
            pilot_path = folder / 'Aaris_ForestLookout_Detail.unity'
            archive_total = archives + native_inventory(pilot_path, prefabs)
            active = Counter()
            for path in active_detail_paths(ROOT):
                active.update(native_inventory(path, prefabs))
            # Scene-header records legitimately change with cell count. Every
            # authored visual component and collider count must still match.
            for kind in (1, 4, 23, 33, 54, 64, 65, 135, 136, 143, 146, 154, 205, 218):
                self.assertEqual(active[kind], archive_total[kind], ('Active/archive inventory mismatch', kind))
            self.assertEqual(active[205], 94)
            self.assertEqual(active[1], 1467)
            for kind in (54, 64, 65, 135, 136, 143, 146, 154, 218):
                self.assertEqual(active[kind], 0, ('Streamed physics object', kind))
            persistent = native_inventory(ROOT / mod.SCENE, prefabs)
            generated, cohort_inventory = baked_static_inventory(ROOT)
            if generated:
                self.assertEqual(generated, Counter({1: 186, 4: 186, 23: 150, 33: 150, 114: 36}))
                self.assertEqual(cohort_inventory, (36, 280, 475, 20, 16))
                self.assertTrue(all(persistent[kind] >= count for kind, count in generated.items()))
                persistent.subtract(generated)
            self.assertEqual(persistent[1], 1395)
            self.assertEqual(persistent[205], 71)
            self.assertEqual(persistent[64], 132)
            self.assertEqual(persistent[65], 200)
            self.assertEqual(persistent[1] + active[1], 2862)
            # These are explicitly serialized records, not instantiated totals.
            self.assertEqual(report["game_objects"] - generated[1], 784)
            self.assertEqual(report["lod_groups"], 34)
        else:
            # Before migrating the district visuals, the one ForestLookout
            # native pilot is the only authored additive detail scene.
            self.assertEqual(report["game_objects"], 877 if native_pilot else 878)
            self.assertEqual(report["lod_groups"], 127 if native_pilot else 128)
        self.assertEqual(report["mesh_colliders"], 132 if district_scenes else 95)
        self.assertEqual(report["box_colliders"], 200)
        self.assertEqual(len(report["districts"]), 13)
        self.assertFalse(report["ready_for_district_migration"])
        self.assertTrue(all(not d["direct_move_safe"] for d in report["districts"]))
        self.assertTrue(all(d["mesh_colliders"] > 0
                            for d in report["districts"]))
        if district_scenes:
            self.assertTrue(all(d["lod_groups"] == 0
                                for d in report["districts"]))
        else:
            self.assertTrue(all(d["lod_groups"] > 0
                                for d in report["districts"]
                                if not (native_pilot and d["name"] == "ForestLookout")))
        if native_pilot:
            lookout = next(d for d in report["districts"]
                           if d["name"] == "ForestLookout")
            self.assertEqual(lookout["objects"], 3)
            self.assertEqual(lookout["lod_groups"], 0)
            self.assertEqual(lookout["mesh_colliders"], 1)
            self.assertEqual(lookout["inbound_inspector_refs"], 1)
            self.assertFalse(lookout["direct_move_safe"])
        self.assertEqual(
            [r["name"] for r in report["persistent_groups"]],
            ["Terrain_2x2", "BattleRoyaleProps",
             "FoundationsAndAprons", "RoadNetwork"]
        )


if __name__ == "__main__":
    unittest.main()
