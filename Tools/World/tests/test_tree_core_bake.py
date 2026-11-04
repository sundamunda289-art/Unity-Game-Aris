"""Transaction tests with a simulated engine; these do NOT test Unity or render trees."""
import copy
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import bake_tree_core as bake
from unity_data import WorldError

REPO = Path(__file__).resolve().parents[3]


class TreeCoreBakeTransactions(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)/'project'
        self.bundle = Path(self.temp.name)/'review'
        for folder in ('Assets', 'Packages', 'ProjectSettings'):
            (self.root/folder).mkdir(parents=True)
        (self.root/'Tools/World').mkdir(parents=True)
        for key in bake.TREE_KEYS:
            for suffix in ('.prefab', '.prefab.meta', '.st.meta'):
                relative = bake.TREE_PREFIX+key+suffix
                target = self.root/relative; target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes((REPO/relative).read_bytes())
            (self.root/(bake.TREE_PREFIX+key+'.st')).write_bytes(b'Simulated engine fixture; not a SpeedTree asset')
        for path in (bake.IMPORTER, bake.IMPORTER+'.meta', 'ProjectSettings/ProjectVersion.txt'):
            target = self.root/path; target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes((REPO/path).read_bytes())
        (self.root/'Assets/AarisWorld/Models').mkdir()
        (self.root/'Assets/SavedMap.unity').write_bytes(b'saved map must remain unchanged\r\n')
        self.calls = []
        self.before = self.files()

    def tearDown(self):
        self.temp.cleanup()

    def files(self):
        return {p.relative_to(self.root).as_posix(): p.read_bytes() for p in self.root.rglob('*') if p.is_file()}

    def fake_engine(self, project, request, temp, editor):
        self.calls.append(request['mode'])
        rows = copy.deepcopy(request['sources'])
        if request['mode'] == 'bake':
            self.assertTrue((project/bake.IMPORTER).is_file())
            (project/'Library').mkdir()
            (project/'Library/old-import-cache').write_bytes(b'Never reuse this cache for verification')
            for i, row in enumerate(rows):
                guid = f'{0xAABB00+i:032x}'
                (project/row['output']).write_bytes(b'Simulated native mesh '+str(i).encode())
                (project/(row['output']+'.meta')).write_text('fileFormatVersion: 2\nguid: '+guid+'\n')
                for j, binding in enumerate(row['bindings']):
                    binding.update(bakedId=str(9007199254741001+j), bakedGuid=guid,
                                   fingerprint=bake.sha(f'mesh {i} {j}'.encode()), vertices=100, submeshes=1)
        else:
            self.assertFalse((project/'Library').exists(), 'Verification must start without cached imports')
            self.assertFalse((project/bake.IMPORTER).exists())
            self.assertFalse((project/(bake.IMPORTER+'.meta')).exists())
            for row in rows:
                self.assertNotIn(row['tag'].encode(), (project/(row['source']+'.meta')).read_bytes())
                self.assertEqual(bake.rewire((project/row['prefab']).read_bytes(), row, True), self.before[row['prefab']])
        return {'status': 'ok', 'mode': request['mode'], 'unityVersion': request['unityVersion'], 'sources': rows}

    def prepare(self, engine=None):
        with patch.object(bake, 'capabilities', return_value={'ready': True}), \
                patch.object(bake, 'launch', side_effect=engine or self.fake_engine), \
                patch.dict(os.environ, {'UNITY_EDITOR_PATH': '/simulated-editor'}):
            return bake.prepare(self.root, self.bundle)

    def test_two_stage_preview_and_apply_keep_ids_colliders_lods_and_unrelated_bytes(self):
        report = self.prepare()
        self.assertEqual(self.calls, ['bake', 'verify'])
        self.assertEqual(self.files(), self.before)
        self.assertFalse(report['written_to_source'])
        preview = bake.apply(self.root, self.bundle)
        self.assertTrue(preview['dry_run'])
        self.assertEqual(self.files(), self.before)
        result = bake.apply(self.root, self.bundle, True)
        self.assertTrue(result['written_to_source'])
        self.assertEqual((self.root/'Assets/SavedMap.unity').read_bytes(), self.before['Assets/SavedMap.unity'])
        self.assertFalse((self.root/bake.IMPORTER).exists())
        self.assertFalse((self.root/bake.HELPER).exists(), 'Authoring helper must never be installed into source Assets')
        for row in result['sources']:
            self.assertEqual((self.root/(row['prefab']+'.meta')).read_bytes(), self.before[row['prefab']+'.meta'])
            self.assertEqual(bake.rewire((self.root/row['prefab']).read_bytes(), row, True), self.before[row['prefab']])
            self.assertGreater(int(row['bindings'][0]['bakedId']), 2**53, 'Local IDs must remain lossless strings')
        with self.assertRaises(WorldError):
            bake.apply(self.root, self.bundle, True)

    def test_failed_fresh_import_never_removes_source_importer_or_exposes_bundle(self):
        def fail(project, request, temp, editor):
            if request['mode'] == 'verify':
                raise WorldError('simulated cold import failure')
            return self.fake_engine(project, request, temp, editor)
        with self.assertRaisesRegex(WorldError, 'cold import failure'):
            self.prepare(fail)
        self.assertEqual(self.files(), self.before)
        self.assertFalse(self.bundle.exists())

    def test_missing_editor_stale_project_or_changed_bundle_cannot_write(self):
        with patch.object(bake, 'capabilities', return_value={'ready': False, 'editor_available': False}):
            with self.assertRaisesRegex(WorldError, 'matching licensed Unity'):
                bake.prepare(self.root, self.bundle)
        self.assertEqual(self.files(), self.before)
        report = self.prepare()
        scene = self.root/'Assets/SavedMap.unity'
        scene.write_bytes(b'user edited this during review')
        with self.assertRaisesRegex(WorldError, 'Source changed'):
            bake.apply(self.root, self.bundle, True)
        scene.write_bytes(self.before['Assets/SavedMap.unity'])
        (self.bundle/'files'/report['sources'][0]['output']).write_bytes(b'changed bundle asset')
        with self.assertRaisesRegex(WorldError, 'Bundle bytes changed'):
            bake.apply(self.root, self.bundle, True)
        self.assertEqual(self.files(), self.before)

    def test_unrelated_unity_edits_are_rejected_before_accepting_a_bundle(self):
        def drift(project, request, temp, editor):
            result = self.fake_engine(project, request, temp, editor)
            if request['mode'] == 'verify':
                (project/'Assets/SavedMap.unity').write_bytes(b'unexpected editor save')
            return result
        with self.assertRaisesRegex(WorldError, 'unrelated source'):
            self.prepare(drift)
        self.assertEqual(self.files(), self.before)
        self.assertFalse(self.bundle.exists())

    def test_automatically_created_folder_metadata_stays_in_the_disposable_copy(self):
        def folders(project, request, temp, editor):
            result = self.fake_engine(project, request, temp, editor)
            (project/'Assets/AarisWorld.meta').write_text('fileFormatVersion: 2\nguid: '+'d'*32+'\nfolderAsset: yes\n')
            return result
        self.prepare(folders)
        self.assertEqual(self.files(), self.before)
        self.assertFalse((self.bundle/'files/Assets/AarisWorld.meta').exists())

    def test_mid_apply_write_error_restores_all_original_files(self):
        self.prepare()
        original_write = bake.atomic_write
        writes = 0

        def fail_once(path, data):
            nonlocal writes
            writes += 1
            if writes == 5:
                raise OSError('simulated disk failure')
            return original_write(path, data)

        with patch.object(bake, 'atomic_write', side_effect=fail_once):
            with self.assertRaisesRegex(OSError, 'disk failure'):
                bake.apply(self.root, self.bundle, True)
        self.assertEqual(self.files(), self.before)

    def test_ref_retirement_preserves_crlf_bom_and_rejects_ambiguous_bindings(self):
        report = self.prepare(); row = report['sources'][0]
        original = b'\xef\xbb\xbf'+self.before[row['prefab']].replace(b'\n', b'\r\n')
        self.assertEqual(bake.rewire(bake.rewire(original, row), row, True), original)
        meta = self.before[row['source']+'.meta'].replace(b'\n', b'\r\n')
        self.assertEqual(bake.retire_tag(meta, row['tag']), meta.replace(row['tag'].encode(), b'', 1))
        with self.assertRaisesRegex(WorldError, 'ambiguous'):
            bake.rewire(original+original, row)

    def test_historical_contract_accepts_only_exact_references_and_hash_bound_native_outputs(self):
        from lod_profiles import preserved_tree_lod_only
        report = self.prepare()
        bake.apply(self.root, self.bundle, True)
        old_contract = json.loads((REPO/'Tools/World/tree-house-20261103.postconditions.json').read_text())
        for row in report['sources']:
            for path in (row['prefab'], row['source']+'.meta'):
                self.assertEqual(bake.preserved_source_bytes(self.root, path, (self.root/path).read_bytes()), self.before[path])
                self.assertTrue(preserved_tree_lod_only(self.root, path, old_contract['tree_source_files'][path]))
        row = report['sources'][0]
        prefab = self.root/row['prefab']
        original = prefab.read_bytes()
        prefab.write_bytes(original.replace(b'  m_Size:', b'  WrongSize:', 1))
        self.assertIsNone(bake.preserved_source_bytes(self.root, row['prefab'], prefab.read_bytes()))
        prefab.write_bytes(original)
        (self.root/row['output']).write_bytes(b'Unreviewed geometry edit')
        self.assertIsNone(bake.preserved_source_bytes(self.root, row['prefab'], original))

    def test_helper_stays_outside_assets_and_registers_no_automatic_execution(self):
        import tree_sitter
        import tree_sitter_c_sharp
        helper = REPO/'Tools/World/UnityTreeCoreBake.cs'
        parser = tree_sitter.Parser(tree_sitter.Language(tree_sitter_c_sharp.language()))
        self.assertFalse(parser.parse(helper.read_bytes()).root_node.has_error, 'C# syntax only; not an engine compilation')
        text = helper.read_text()
        for automatic in ('OnPostprocess', 'InitializeOnLoad', 'IPreprocessBuild', 'RuntimeInitializeOnLoad', 'MenuItem'):
            self.assertNotIn(automatic, text)
        self.assertFalse((REPO/bake.HELPER).exists())


if __name__ == '__main__':
    unittest.main()
