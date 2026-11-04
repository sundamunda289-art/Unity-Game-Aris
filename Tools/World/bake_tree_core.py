"""Explicit one-time SpeedTree core bake in disposable matching Unity projects.

prepare produces a reviewable bundle only; apply requires that bundle and --write.
Nothing in this module is loaded by Unity, an importer, a build or the game.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import signal
import subprocess
import tempfile
from pathlib import Path

from engine import capabilities
from patching import atomic_write
from unity_data import WorldError, digest, safe_path

IMPORTER = 'Assets/AarisWorld/Editor/AarisTreeShapeImporter.cs'
HELPER = 'Assets/AarisWorld/Editor/AarisTreeCoreBake.cs'
CONTRACT = 'Tools/World/tree-core-baked.json'
TREE_PREFIX = 'Assets/TerrainDemoScene_URP/Prefabs/Trees/'
TREE_KEYS = ('Conifer/Conifer', 'Cypress/Cypress_Forest_Desktop',
             'Pines/Pine_A/Pine_A', 'Pines/Pine_B/Pine_B',
             'Pines/Pine_C/Pine_C', 'Pines/Pine_D/Pine_D')
MESH_REF = re.compile(rb'm_Mesh: \{fileID: (-?\d+), guid: ([0-9a-f]{32}), type: ([23])\}')


def sha(data):
    return hashlib.sha256(data).hexdigest()


def bundle_path(root, path):
    if path != CONTRACT:
        return safe_path(root, path)
    target = (root/CONTRACT).resolve()
    if root not in target.parents:
        raise WorldError('Contract path escapes the project')
    return target


def sources(root):
    result = []
    for key in TREE_KEYS:
        prefab = TREE_PREFIX + key + '.prefab'
        source = TREE_PREFIX + key + '.st'
        metadata = (root/(source+'.meta')).read_text()
        guid = re.search(r'^guid: ([0-9a-f]{32})$', metadata, re.M).group(1)
        tag = re.search(r'^  userData: (aaris-tree-shape-v1\|[^\r\n]+)$', metadata, re.M)
        if not tag:
            raise WorldError('Unbaked reviewed tree profile required: ' + source)
        refs = MESH_REF.findall((root/prefab).read_bytes())
        if len(refs) != 4 or any(g.decode() != guid or t != b'3' for _, g, t in refs) or len({i for i, _, _ in refs}) != 4:
            raise WorldError('Exactly four unique imported mesh bindings required: ' + prefab)
        result.append({'source': source, 'prefab': prefab,
                       'output': 'Assets/AarisWorld/Models/TreeCore_' + Path(key).name + '.asset',
                       'tag': tag[1], 'sourceGuid': guid,
                       'bindings': [{'sourceId': i.decode()} for i, _, _ in refs]})
    return result


def rewire(data, source, reverse=False):
    """Replace exact mesh references only; all prefab IDs/other bytes survive."""
    result = data
    for binding in source['bindings']:
        before = ('m_Mesh: {fileID: %s, guid: %s, type: 3}' % (binding['sourceId'], source['sourceGuid'])).encode()
        after = ('m_Mesh: {fileID: %s, guid: %s, type: 2}' % (binding['bakedId'], binding['bakedGuid'])).encode()
        old, new = (after, before) if reverse else (before, after)
        if result.count(old) != 1:
            raise WorldError('Missing/ambiguous mesh binding: ' + source['prefab'])
        result = result.replace(old, new, 1)
    return result


def retire_tag(data, tag):
    pattern = re.compile(rb'(?m)^(  userData: )' + re.escape(tag.encode()) + rb'(?=\r?$)')
    result, count = pattern.subn(rb'\1', data)
    if count != 1:
        raise WorldError('Missing/ambiguous shape profile tag')
    return result


def project_files(root):
    return {p.relative_to(root).as_posix(): p for folder in ('Assets', 'Packages', 'ProjectSettings')
            for p in (root/folder).rglob('*') if p.is_file()}


def snapshot(root):
    files = project_files(root)
    if any(p.is_symlink() for folder in ('Assets', 'Packages', 'ProjectSettings') for p in (root/folder).rglob('*')):
        raise WorldError('Use a regular project checkout, without symlinked dependencies')
    return {path: digest(file) for path, file in files.items()}


def launch(project, request, temp, editor):
    mode = request['mode']
    input_path, output_path = temp/(mode+'-request.json'), temp/(mode+'-response.json')
    input_path.write_text(json.dumps(request))
    output_path.unlink(missing_ok=True)
    log_path = temp/(mode+'-unity.log')
    env = dict(os.environ, AARIS_TREE_BAKE_REQUEST=str(input_path), AARIS_TREE_BAKE_RESPONSE=str(output_path))
    args = [editor, '-batchmode', '-nographics', '-quit', '-projectPath', str(project),
            '-executeMethod', 'Aaris.WorldAutomation.TreeCoreBake.Run', '-logFile', str(log_path)]
    process = None
    old_handler = signal.getsignal(signal.SIGTERM)

    def cancelled(signum, frame):
        raise WorldError('Tree bake cancelled; source checkout is untouched')

    signal.signal(signal.SIGTERM, cancelled)
    try:
        process = subprocess.Popen(args, env=env, start_new_session=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        code = process.wait(timeout=1800)
    finally:
        if process is not None and process.poll() is None:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()
        signal.signal(signal.SIGTERM, old_handler)
    if code or not output_path.is_file():
        log = log_path.read_text(errors='replace')[-10000:] if log_path.exists() else 'No Unity log'
        raise WorldError('Unity %s failed (exit %s): %s' % (mode, code, log))
    result = json.loads(output_path.read_text())
    if (result.get('status') != 'ok' or result.get('mode') != mode or
            result.get('unityVersion') != request['unityVersion']):
        raise WorldError('Matching successful Unity response required')
    validate_sources(request['sources'], result.get('sources'))
    return result


def validate_sources(expected, actual):
    if not isinstance(actual, list) or len(actual) != 6:
        raise WorldError('Exactly six baked source results required')
    all_ids = set()
    for key, before, after in zip(TREE_KEYS, expected, actual):
        if (before['source'] != TREE_PREFIX+key+'.st' or before['prefab'] != TREE_PREFIX+key+'.prefab' or
                before['output'] != 'Assets/AarisWorld/Models/TreeCore_'+Path(key).name+'.asset'):
            raise WorldError('Unexpected tree source/output path')
        if any(before[k] != after.get(k) for k in ('source', 'prefab', 'output', 'tag')):
            raise WorldError('Unity result changed the requested source scope')
        bindings = after.get('bindings', [])
        if len(bindings) != 4 or [b['sourceId'] for b in bindings] != [b['sourceId'] for b in before['bindings']]:
            raise WorldError('Unity result lost a source LOD binding')
        for binding in bindings:
            if (not re.fullmatch(r'[0-9a-f]{32}', binding.get('bakedGuid', '')) or
                    not re.fullmatch(r'-?\d+', binding.get('bakedId', '')) or
                    not re.fullmatch(r'[0-9a-f]{64}', binding.get('fingerprint', '')) or
                    type(binding.get('vertices')) is not int or binding['vertices'] <= 0 or
                    type(binding.get('submeshes')) is not int or binding['submeshes'] <= 0):
                raise WorldError('Incomplete baked mesh evidence')
            identity = binding['bakedGuid'], binding['bakedId']
            if identity in all_ids:
                raise WorldError('Repeated saved mesh identity')
            all_ids.add(identity)
        if len({b['bakedGuid'] for b in bindings}) != 1:
            raise WorldError('One native mesh asset per tree source required')
        if 'fingerprint' in before['bindings'][0] and before['bindings'] != bindings:
            raise WorldError('Fresh import changed baked evidence')


def prepare(root, output):
    root, output = Path(root).resolve(), Path(output).resolve()
    if output == root or root in output.parents or output.exists():
        raise WorldError('Choose a new bundle directory outside the project')
    cap = capabilities(root)
    if not cap['ready']:
        raise WorldError('Actual bake requires matching licensed Unity and full LFS checkout: ' + json.dumps(cap))
    request_sources = sources(root)
    version = re.search(r'^m_EditorVersion: (\S+)', (root/'ProjectSettings/ProjectVersion.txt').read_text(), re.M)[1]
    before = snapshot(root)
    if CONTRACT in before or (root/CONTRACT).exists():
        raise WorldError('Tree core bake already recorded; inspect it rather than baking twice')
    if not (root/IMPORTER).is_file() or not (root/(IMPORTER+'.meta')).is_file():
        raise WorldError('Original corrected importer and its metadata required until bake succeeds')
    for row in request_sources:
        if any((root/(row['output']+suffix)).exists() for suffix in ('', '.meta')):
            raise WorldError('Never overwrite a saved mesh identity')
    with tempfile.TemporaryDirectory(prefix='aaris-tree-core-', dir=os.environ.get('AARIS_WORLD_TEMP')) as directory:
        temp = Path(directory); project = temp/'project'
        for folder in ('Assets', 'Packages', 'ProjectSettings'):
            shutil.copytree(root/folder, project/folder)
        shutil.copy2(Path(__file__).with_name('UnityTreeCoreBake.cs'), project/HELPER)
        baked = launch(project, {'mode': 'bake', 'unityVersion': version, 'sources': request_sources}, temp, os.environ['UNITY_EDITOR_PATH'])
        for request_row, row in zip(request_sources, baked['sources']):
            row['sourceGuid'] = request_row['sourceGuid']
            (project/row['prefab']).write_bytes(rewire((root/row['prefab']).read_bytes(), row))
            (project/(row['source']+'.meta')).write_bytes(retire_tag((root/(row['source']+'.meta')).read_bytes(), row['tag']))
        for path in (IMPORTER, IMPORTER+'.meta'):
            (project/path).unlink()
        # An entirely cold import proves the saved prefabs no longer rely on the
        # old callback or cached corrected source artifacts in Library.
        for folder in ('Library', 'Temp', 'obj', 'Logs'):
            shutil.rmtree(project/folder, ignore_errors=True)
        launch(project, {**baked, 'mode': 'verify'}, temp, os.environ['UNITY_EDITOR_PATH'])
        writes = {row[k] + suffix for row in baked['sources'] for k in ('output',) for suffix in ('', '.meta')}
        writes.update(row[k] + suffix for row in baked['sources'] for k, suffix in (('prefab', ''), ('source', '.meta')))
        deletes = {IMPORTER, IMPORTER+'.meta'}
        after = snapshot(project)
        # Unity may create missing folder metadata on first import. Those GUIDs
        # are not used by the mesh bindings; keep them confined to the copy.
        generated_folder_meta = {p for p in set(after)-set(before) if p.endswith('.meta') and
                                 (project/p[:-5]).is_dir() and (root/p[:-5]).is_dir()}
        unexpected = ({p for p in set(before)|set(after) if before.get(p) != after.get(p)} - writes - deletes -
                      {HELPER, HELPER+'.meta'} - generated_folder_meta)
        if unexpected:
            raise WorldError('Unity changed unrelated source files; no outputs accepted: ' + ', '.join(sorted(unexpected)[:15]))
        # Reject a simultaneous user/source edit, even though this was a preview.
        if snapshot(root) != before:
            raise WorldError('Source checkout changed while preparing; no bundle accepted')
        contract = {'schema_version': 1, 'unity_version': version, 'sources': baked['sources'],
                    'fresh_import_without_deformer': True, 'source_reimport_preserves_baked_meshes': True,
                    'before': {p: before[p] for p in sorted((writes|deletes)&set(before))},
                    'after': {p: after[p] for p in sorted(writes)}, 'deleted': sorted(deletes),
                    'limits': 'Mesh stream/binding integrity; wind appearance, LOD visuals, physics and phone FPS still require play/device tests.'}
        output.mkdir(parents=True)
        try:
            for path in writes:
                target = output/'files'/path; target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(project/path, target)
            contract_path = output/'files'/CONTRACT; contract_path.parent.mkdir(parents=True, exist_ok=True)
            contract_path.write_text(json.dumps(contract, indent=2)+'\n')
            report = {'schema_version': 1, 'source_before': before, 'contract_sha256': digest(contract_path),
                      'writes': {**contract['after'], CONTRACT: digest(contract_path)}, 'deletes': sorted(deletes),
                      'unity_version': version, 'sources': baked['sources'], 'unity_bake_and_cold_import_passed': True,
                      'written_to_source': False}
            (output/'review.json').write_text(json.dumps(report, indent=2)+'\n')
        except BaseException:
            shutil.rmtree(output)
            raise
    return report


def apply(root, bundle, write=False):
    root, bundle = Path(root).resolve(), Path(bundle).resolve()
    report = json.loads((bundle/'review.json').read_text())
    rows = sources(root)
    validate_sources(rows, report.get('sources'))
    writes = {r[k]+s for r in rows for k, s in (('prefab', ''), ('source', '.meta'), ('output', ''), ('output', '.meta'))} | {CONTRACT}
    deletes = {IMPORTER, IMPORTER+'.meta'}
    if (set(report['writes']) != writes or set(report['deletes']) != deletes or
            report.get('unity_bake_and_cold_import_passed') is not True):
        raise WorldError('Complete matching Unity-verified core bundle required')
    if snapshot(root) != report['source_before']:
        raise WorldError('Source changed since preview; prepare a fresh bundle')
    for path, expected in report['writes'].items():
        target = bundle_path(root, path)
        if digest(bundle_path(bundle/'files', path)) != expected:
            raise WorldError('Bundle bytes changed: ' + path)
        if path not in report['source_before'] and target.exists():
            raise WorldError('New output path already exists: ' + path)
    contract = json.loads((bundle/'files'/CONTRACT).read_text())
    if (contract.get('sources') != report['sources'] or contract.get('after') != {p: h for p, h in report['writes'].items() if p != CONTRACT} or
            contract.get('deleted') != sorted(deletes) or contract.get('fresh_import_without_deformer') is not True or
            contract.get('source_reimport_preserves_baked_meshes') is not True or contract.get('unity_version') != report['unity_version'] or
            any(report['source_before'].get(p) != h for p, h in contract.get('before', {}).items())):
        raise WorldError('Baked migration contract differs from the reviewed bundle')
    for row in report['sources']:
        if rewire((bundle/'files'/row['prefab']).read_bytes(), row, True) != (root/row['prefab']).read_bytes():
            raise WorldError('Prefab changed beyond mesh references')
        if (bundle/'files'/(row['source']+'.meta')).read_bytes() != retire_tag((root/(row['source']+'.meta')).read_bytes(), row['tag']):
            raise WorldError('Source metadata changed beyond retiring its profile tag')
    if not write:
        return {**report, 'dry_run': True}
    originals = {p: (root/p).read_bytes() if (root/p).exists() else None for p in writes|deletes}
    try:
        for path in sorted(writes):
            atomic_write(root/path, (bundle/'files'/path).read_bytes())
        for path in deletes:
            (root/path).unlink()
        for path, expected in report['writes'].items():
            if digest(root/path) != expected:
                raise WorldError('Post-write bytes differ: ' + path)
    except BaseException:
        for path, data in originals.items():
            if data is None:
                (root/path).unlink(missing_ok=True)
            else:
                atomic_write(root/path, data)
        raise
    return {**report, 'written_to_source': True, 'dry_run': False}


def preserved_source_bytes(root, path, data):
    """Reverse only a hash-bound completed bake for historical integrity checks.

    This does not whitelist unrelated prefab/metadata edits or substitute a report
    for mesh bytes: every enumerated native output must still match its own hash.
    """
    root = Path(root)
    try:
        contract = json.loads((root/CONTRACT).read_text())
        rows = contract['sources']
        allowed = {TREE_PREFIX+key+suffix for key in TREE_KEYS for suffix in ('.prefab', '.st.meta')}
        outputs = {row[k]+suffix for row in rows for k, suffix in (('prefab', ''), ('source', '.meta'), ('output', ''), ('output', '.meta'))}
        if (len(rows) != 6 or path not in allowed or set(contract['after']) != outputs or
                set(contract['deleted']) != {IMPORTER, IMPORTER+'.meta'} or
                contract.get('fresh_import_without_deformer') is not True or
                contract.get('source_reimport_preserves_baked_meshes') is not True):
            return None
        validate_sources(rows, rows)
        if any((root/p).exists() for p in contract['deleted']):
            return None
        for output, expected in contract['after'].items():
            if digest(safe_path(root, output)) != expected:
                return None
        for row in rows:
            if path == row['prefab']:
                restored = rewire(data, row, True)
            elif path == row['source']+'.meta':
                pattern = re.compile(rb'(?m)^(  userData: )(?=\r?$)')
                restored, count = pattern.subn(lambda m: m[1]+row['tag'].encode(), data)
                if count != 1:
                    return None
            else:
                continue
            return restored if sha(restored) == contract['before'].get(path) else None
    except (OSError, ValueError, KeyError, TypeError, WorldError):
        return None
    return None


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[2])
    commands = parser.add_subparsers(dest='command', required=True)
    commands.add_parser('status')
    commands.add_parser('prepare').add_argument('--out', type=Path, required=True)
    apply_parser = commands.add_parser('apply')
    apply_parser.add_argument('--bundle', type=Path, required=True)
    apply_parser.add_argument('--write', action='store_true')
    args = parser.parse_args()
    try:
        result = capabilities(args.root) if args.command == 'status' else (
            prepare(args.root, args.out) if args.command == 'prepare' else apply(args.root, args.bundle, args.write))
        print(json.dumps(result, indent=2))
    except (WorldError, OSError, ValueError, subprocess.TimeoutExpired) as error:
        parser.exit(1, str(error)+'\n')
