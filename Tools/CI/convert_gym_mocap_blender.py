#!/usr/bin/env python3
"""Blender 4.x: export one animation-only Humanoid candidate FBX per source action.

This script does not assert that Unity Humanoid retargeting already works:
Unity must import and inspect every AnimationClip.humanMotion before APK build.
"""
import argparse
import json
from pathlib import Path
import re
import sys
import zipfile
import bpy

UPRIGHT = (
    "ArmsBySide_ID", "ArmsBySide_FW", "ArmsBySide_BW",
    "ArmsBySide_SW", "ArmsBySide_FR",
    "Crouched_ID", "Crouched_FW", "Crouched_BW", "Crouched_SW",
)
ACCAD = (
    "Male1_A1_Stand",
    "Male1_A7_Crouch",
    "Male1_A8_CrouchToLie",
    "Male1_A9_LieDown",
    "Male1_A10_LieToCrouch",
    "Male1_A11_CrawlForward",
    "Male1_A12_CrawlBackward",
    "Male1_B24_WalkToCrouch",
    "Male1_B25_CrouchToWalk",
)
MAX_EXPORT = 12_000_000


def export_rig(source: Path, output: Path, is_bvh: bool):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    if is_bvh:
        bpy.ops.import_anim.bvh(filepath=str(source),
                                axis_forward="-Z", axis_up="Y",
                                global_scale=1.0, update_scene_fps=True)
    else:
        bpy.ops.import_scene.gltf(filepath=str(source))
    arms = [ob for ob in bpy.data.objects if ob.type == "ARMATURE"]
    if len(arms) != 1:
        raise RuntimeError(f"{source.name}: expected 1 armature, got {len(arms)}")
    arm = arms[0]
    bones = sorted(b.name for b in arm.data.bones)
    if len(bones) < 15:
        raise RuntimeError(f"{source.name}: suspiciously few bones ({len(bones)})")
    actions = [a for a in bpy.data.actions if a.frame_range[1] > a.frame_range[0]]
    if len(actions) < 1:
        raise RuntimeError(f"{source.name}: missing keyed animation action")
    # Both importers normally generate one action. Stop rather than mixing
    # unrelated actions into a single clip with invalid limb contacts.
    if len(actions) != 1:
        raise RuntimeError(f"{source.name}: ambiguous actions: {[a.name for a in actions]}")
    action = actions[0]
    action.name = output.stem
    action.use_fake_user = True
    if arm.animation_data is None:
        arm.animation_data_create()
    arm.animation_data.action = action
    for track in list(arm.animation_data.nla_tracks):
        arm.animation_data.nla_tracks.remove(track)
    bpy.ops.object.select_all(action="DESELECT")
    arm.select_set(True)
    bpy.context.view_layer.objects.active = arm
    output.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.export_scene.fbx(
        filepath=str(output), use_selection=True, object_types={"ARMATURE"},
        apply_unit_scale=True, axis_forward="-Z", axis_up="Y",
        add_leaf_bones=False, bake_anim=True,
        bake_anim_use_all_bones=True, bake_anim_use_nla_strips=False,
        bake_anim_use_all_actions=False, bake_anim_force_startend_keying=True,
        bake_anim_step=1.0, bake_anim_simplify_factor=0.0,
        path_mode="AUTO",
    )
    size = output.stat().st_size
    if size < 15_000 or size > MAX_EXPORT:
        raise RuntimeError(f"{output.name}: invalid FBX output bytes={size}")
    info = {
        "filename": output.name, "bytes": size, "bone_count": len(bones),
        "source": source.name, "action": action.name,
        "frames": [float(v) for v in action.frame_range],
        "fps": bpy.context.scene.render.fps,
        "bones": bones,
    }
    print("AARIS_GYM_FBX_EXPORT_PASS", output.name, size,
          len(bones), info["frames"], flush=True)
    return info


def main():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argparse.ArgumentParser()
    p.add_argument("--input", required=True)
    p.add_argument("--output", required=True)
    p.add_argument("--accad", action="store_true")
    ns = p.parse_args(args)
    source, out = Path(ns.input), Path(ns.output)
    out.mkdir(parents=True, exist_ok=True)
    report = []
    for name in UPRIGHT:
        report.append(export_rig(source / f"{name}.glb",
                                 out / f"{name}.fbx", False))
    if ns.accad:
        archive = source / "ACCAD_Male1_bvh.zip"
        with zipfile.ZipFile(archive) as z:
            members = z.namelist()
            for name in ACCAD:
                files = [x for x in members if x.lower().endswith(name.lower()+".bvh")]
                if len(files) != 1:
                    raise RuntimeError(f"Missing/ambiguous ACCAD clip: {name}")
                # Strict basename + no path traversal on extraction.
                payload = z.read(files[0])
                if len(payload) > 3_000_000:
                    raise RuntimeError("BVH source unexpectedly large")
                temp = out / (name + ".source.bvh")
                temp.write_bytes(payload)
                try:
                    report.append(export_rig(temp, out / f"{name}.fbx", True))
                finally:
                    temp.unlink()
    total = sum(x["bytes"] for x in report)
    if total > 90_000_000:
        raise RuntimeError("Movement-only binary package budget exceeded")
    (out / "retarget-preflight.json").write_text(
        json.dumps({"clips": report, "total_bytes": total}, indent=2) + "\n")
    print("AARIS_GYM_FBX_CONVERSION_COMPLETE",
          len(report), total, flush=True)


if __name__ == "__main__":
    main()
