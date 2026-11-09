#!/usr/bin/env python3
"""Bounded, owner-only issue reply. Never publish arbitrary stdout, logs or secrets."""
import json
from pathlib import Path
import re
import sys
folder = Path(sys.argv[1])
try:
    data = json.loads((folder / 'result.json').read_text())
except (OSError, ValueError):
    data = {}
operations = set('machines info quota start fresh fresh_ssh fresh_minimal rebuild full_rebuild status logs sync recovery_sync lfs lfs_report changes preserve_assets retry_assets install bootstrap deps cli_diag editor editor_diag editor_isolate tmp_audit license auth probe shot accept_terms safe_mode compile_report refresh close_editor editor_process editor_recover scene_render scene_play drop_video drop_frames map_verify photo snapshot play_diag foliage_diag test stop'.split())
statuses = {'SUCCESS', 'FAILED', 'NO_CODESPACE', 'TARGET_UNAVAILABLE', 'UNKNOWN'}
op = data.get('operation', 'unknown')
state = data.get('status', 'FAILED')
if op not in operations:
    op = 'unknown'
if state not in statuses:
    state = 'FAILED'
detail = data.get('detail', 'unavailable')
if not re.fullmatch(r'[a-zA-Z0-9_]{1,80}', detail):
    detail = 'unavailable'
print('**AARIS CODESPACE CONTROL**')
print('Operation: `%s` | Status: `%s` | Detail: `%s`' % (op, state, detail))
try:
    lines = (folder / 'terminal.txt').read_text().splitlines()
except OSError:
    lines = []
safe = []
for line in lines:
    if re.fullmatch(r'[A-Z][A-Z0-9_]{2,90}', line):
        safe.append(line)
    elif re.fullmatch(r'(commit|arch|os_id|os_version|glibc|docker_ready|cpus|memory_mb|workspace_free_kb|tmp_free_kb|lfs_tracked_count|lfs_unhydrated_pointer_count|lfs_log_recorded|lfs_error_kind|lfs_error_hash_count|lfs_object_hash_1|lfs_object_hash_2|lfs_object_hash_3|changed_files_count|raw_identical_count|raw_different_count|state|idle_minutes|devcontainer|editor_installed|editor_running|display|scene|machine|codespace|target_codespace|reason)=[A-Za-z0-9_.-]{1,80}', line):
        safe.append(line)
    elif op == 'cli_diag' and re.fullmatch(
        r'(UNITY_CLI_PRESENT|UNITY_CLI_INSTALL_LOG_RECORDED|'
        r'UNITY_CLI_INSTALL_LOG_CODE_OTHER|UNITY_CLI_INSTALL_FLAGS_[A-Z_]{2,30}|'
        r'UNITY_CLI_INSTALL_LOG_DIAGNOSTIC_[A-Z_]{2,40}|'
        r'UNITY_CLI_INSTALL_LOG_REJECTED_[A-Z_]{2,30})=(yes|no)', line):
        safe.append(line)
    elif op == 'cli_diag' and re.fullmatch(
        r'UNITY_CLI_INSTALL_HELP_EXIT_[0-9]{1,3}', line):
        safe.append(line)
    elif op in ('scene_render', 'scene_play', 'photo', 'foliage_diag') and re.fullmatch(
        r'(terrain_tiles|pixel_deviation|image_bytes|play_frames|runtime_errors|photo_target_x|photo_target_z|photo_camera_y)=[0-9.]{1,20}', line):
        safe.append(line)
    elif op == 'retry_assets' and re.fullmatch(r'(tracked_changed_count|lfs_changed_count|checkpoint_sha)=[A-Za-z0-9]{1,64}', line):
        safe.append(line)
    elif op == 'retry_assets' and re.fullmatch(r'checkpoint_branch=backup/codespace-local-changes-20261108', line):
        safe.append(line)
    elif op == 'preserve_assets' and re.fullmatch(r'(tracked_changed_count|untracked_review_count|lfs_changed_count|category_count|copied_file_count|staged_file_count|checkpoint_sha)=[A-Za-z0-9]{1,64}', line):
        safe.append(line)
    elif op == 'preserve_assets' and re.fullmatch(r'checkpoint_branch=backup/codespace-local-changes-20261108', line):
        safe.append(line)
    elif op == 'map_verify' and re.fullmatch(r'(placed|overlay_files|skipped_terrain|skipped_trees|skipped_obstacles|skipped_assets|image_count|runtime_errors)=[0-9]{1,8}', line):
        safe.append(line)
    elif op == 'drop_frames' and re.fullmatch(r'(frame_count|package_bytes)=[0-9]{1,13}', line):
        safe.append(line)
    elif op == 'drop_video' and re.fullmatch(r'(overlay_files|overlay_bytes|video_bytes|play_frames|runtime_errors)=[0-9]{1,13}', line):
        safe.append(line)
    elif op == 'play_diag' and (
        re.fullmatch(r'(play_status|play_reason)=[A-Z_a-z0-9]{1,60}', line) or
        re.fullmatch(r'(frames|terrainTiles|runtimeErrors)=[0-9]{1,6}', line) or
        re.fullmatch(r'(playerPresent|cameraPresent|vehicleManagerPresent)=(yes|no)', line)):
        safe.append(line)
    elif op == 'foliage_diag' and re.fullmatch(
        r'(tree_prototypes|foliage_materials|image_bytes|terrain_tiles)=[0-9]{1,9}', line):
        safe.append(line)
    elif op == 'scene_play' and re.fullmatch(
        r'(runtime_errors)=[0-9]{1,5}|(runtime_error_kind|runtime_error_source|play_reason)=[A-Z_]{1,60}', line):
        safe.append(line)
    elif op == 'quota' and (
        re.fullmatch(r'CODESPACES_(BILLING_PERIOD)=[0-9]{4}-[0-9]{2}', line) or
        re.fullmatch(r'CODESPACES_(COMPUTE_LINES|STORAGE_LINES)=[0-9]{1,6}', line) or
        re.fullmatch(r'CODESPACES_(USED_CORE_HOURS|REMAINING_CORE_HOURS|REMAINING_4CORE_HOURS|FREE_REMAINING_CORE_HOURS|PRO_REMAINING_CORE_HOURS)=[0-9]{1,9}(?:\.[0-9]{1,4})?', line) or
        re.fullmatch(r'CODESPACES_(PLAN|BILLING_REPORT|HOURS_STATUS)=[A-Z_]{2,42}', line) or
        re.fullmatch(r'CODESPACES_(INCLUDED_CORE_HOURS)=[0-9]{1,6}', line)):
        safe.append(line)
    elif op == 'machines' and re.fullmatch(r'[A-Za-z0-9-]{3,55}\t\d{1,2}\t\d{1,3}', line):
        safe.append(line)
    if len(safe) >= (42 if op == 'cli_diag' else 22):
        break
if safe:
    print()
    print('```text')
    print(*safe, sep=chr(10))
    print('```')
if op in ('scene_render', 'scene_play', 'photo', 'snapshot', 'drop_video', 'map_verify'):
    print('Only verified Unity camera PNGs and completed native scene/Play tests count as evidence; a successful workflow without them is not proof.')
else:
    print('Editor process and a nonblank image do not prove Unity Personal activation or valid scene rendering.')
