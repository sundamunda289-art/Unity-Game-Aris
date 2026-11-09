#!/usr/bin/env bash
# Review-only, bounded native TerrainData bake in a SHA-pinned /tmp Git worktree.
# The owner's /workspaces Unity project is never changed. The binary outputs
# stay in temporary space until authenticated Actions validates and stages ONLY
# the exact reported TerrainData paths as Git LFS.
set -Eeuo pipefail
EXPECTED="${1:?expected review commit}"
[[ "$EXPECTED" =~ ^[a-f0-9]{40}$ ]] || { echo FOREST_BAKE_BAD_EXPECTED_SHA; exit 10; }
SOURCE=/workspaces/Unity-Game-Aris
ROOT=/tmp/aaris-native-photo-worktrees
PROJECT="$ROOT/$EXPECTED"
OUT=/tmp/aaris-codespaces/deep-forest-bake
REPORT=/tmp/aaris-codespaces/deep-forest-bake-report.json
PHOTO=/tmp/aaris-codespaces/scene-probe
EDITOR="$HOME/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity"
for candidate in "$EDITOR" /workspaces/.aaris-unity-editors/6000.3.26f1/Editor/Unity /tmp/aaris-unity-runtime/editors/6000.3.26f1/Editor/Unity; do
  if test -x "$candidate"; then EDITOR="$candidate"; break; fi
done
test -x "$EDITOR" || { echo FOREST_BAKE_EDITOR_MISSING; exit 11; }
if pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null 2>&1; then
  echo FOREST_BAKE_EDITOR_ALREADY_RUNNING
  exit 12
fi
test -e "$SOURCE/.git" || { echo FOREST_BAKE_SOURCE_REPOSITORY_MISSING; exit 13; }
git -C "$SOURCE" fetch --quiet origin refs/heads/review/deep-forest-terrain-bake-20261109 >/dev/null 2>&1 || { echo FOREST_BAKE_REF_FETCH_FAILED; exit 14; }
REV="$(git -C "$SOURCE" rev-parse FETCH_HEAD)"
test "$REV" = "$EXPECTED" || { echo FOREST_BAKE_UNEXPECTED_BRANCH_SHA; exit 15; }
mkdir -p "$ROOT" "$OUT" "$OUT/UnityTemp" "$PHOTO"
if test ! -d "$PROJECT"; then
  if test ! -e "$PROJECT" && git -C "$SOURCE" worktree list --porcelain | grep -Fx -- "worktree $PROJECT" >/dev/null; then
    git -C "$SOURCE" worktree remove --force "$PROJECT" >/dev/null 2>&1 || { echo FOREST_BAKE_STALE_TEMP_WORKTREE; exit 16; }
  fi
  GIT_LFS_SKIP_SMUDGE=1 git -C "$SOURCE" -c filter.lfs.process= -c filter.lfs.smudge=cat -c filter.lfs.required=false \
    worktree add --detach "$PROJECT" "$REV" >/dev/null 2>&1 || { echo FOREST_BAKE_ISOLATED_WORKTREE_FAILED; exit 16; }
fi
test "$(git -C "$PROJECT" rev-parse HEAD)" = "$EXPECTED" || { echo FOREST_BAKE_PROJECT_SHA_MISMATCH; exit 17; }
if ! python3 "$PROJECT/Tools/Codespaces/restore_preserved_source_lfs.py" "$PROJECT" >"$OUT/private-lfs.log" 2>&1; then
  echo FOREST_BAKE_FULL_793_LFS_HYDRATION_FAILED
  exit 18
fi
echo FOREST_BAKE_793_LFS_ASSETS_VERIFIED
python3 - "$PROJECT" "$OUT/before.json" <<'PYBEFORE'
import hashlib,json,pathlib,sys
root=pathlib.Path(sys.argv[1])
base=root/"Assets/TerrainDemoScene_URP/Terrain/Data"
files=sorted(base.glob("Terrain_*.asset"))
assert len(files)==4
report={}
for path in files:
    data=path.read_bytes()
    assert len(data)>1024 and not data.startswith(b"version https://git-lfs.github.com")
    report[str(path.relative_to(root))]={"sha256":hashlib.sha256(data).hexdigest(),"size":len(data)}
pathlib.Path(sys.argv[2]).write_text(json.dumps(report,sort_keys=True))
PYBEFORE
cd "$PROJECT"
bash "$PROJECT/.devcontainer/start_screen.sh" >/dev/null 2>&1 || { echo FOREST_BAKE_XVFB_FAILED; exit 19; }
test -s "Assets/AarisWorld/Editor/AarisDeepForestNativeBake.cs" || { echo FOREST_BAKE_METHOD_MISSING; exit 20; }
rm -f "$REPORT" "$OUT/package.tar.gz" "$OUT/bake-manifest.json"
set +e
timeout --signal=TERM 1500 env DISPLAY=:99 TMPDIR="$OUT/UnityTemp" LIBGL_ALWAYS_SOFTWARE=1 \
  "$EDITOR" -batchmode -quit -force-glcore -projectPath "$PROJECT" \
  -executeMethod Aaris.MapReview.Editor.AarisDeepForestNativeBake.Apply \
  -logFile "$OUT/private-bake-editor.log" >"$OUT/private-bake-stdout.log" 2>"$OUT/private-bake-stderr.log"
rc=$?
set -e
if test "$rc" -ne 0; then
  if grep -Eqi 'error CS[0-9]{4}' "$OUT/private-bake-editor.log" 2>/dev/null; then
    echo FOREST_BAKE_CSHARP_COMPILER_ERROR
  else
    echo FOREST_BAKE_NATIVE_EDITOR_FAILED
  fi
  exit 21
fi
python3 - "$PROJECT" "$OUT" "$REPORT" "$EXPECTED" <<'PYPACKAGE'
import hashlib,io,json,pathlib,re,subprocess,sys,tarfile
root=pathlib.Path(sys.argv[1]);out=pathlib.Path(sys.argv[2])
report_file=pathlib.Path(sys.argv[3]);expected=sys.argv[4]
report=json.loads(report_file.read_text())
before=json.loads((out/"before.json").read_text())
assert report["status"]=="passed" and report["terrainTiles"]==4
assert report["reason"]=="NATIVE_TERRAINDATA_TREE_PROTOTYPE_SAVED_NO_NEW_INSTANCES"
assert 1<=report["converted"]<=48
assert report["oldTrees"]==report["newTrees"]
assert len(report["tiles"])==4
names=[]
for row in report["tiles"]:
    path=row["asset"]
    assert path in before and re.fullmatch(
      r"Assets/TerrainDemoScene_URP/Terrain/Data/Terrain_[12]_[12]_[a-zA-Z0-9-]+\.asset",path)
    assert row["beforeTrees"]==row["afterTrees"]
    if row["converted"]>0:
        assert row["newPrototypes"]==row["oldPrototypes"]+1
        names.append(path)
    else:
        assert row["newPrototypes"]==row["oldPrototypes"]
assert sum(row["converted"] for row in report["tiles"])==report["converted"]
assert 1<=len(names)<=4 and len(set(names))==len(names)
changed=[]
for path,old in before.items():
    f=root/path
    raw=f.read_bytes()
    new={"sha256":hashlib.sha256(raw).hexdigest(),"size":len(raw)}
    if path in names:
        assert new["sha256"]!=old["sha256"] and 1024<len(raw)<250_000_000
        assert not raw.startswith(b"version https://git-lfs.github.com")
        oldmeta=subprocess.run(["git","-C",str(root),"show","HEAD:"+path+".meta"],
             stdout=subprocess.PIPE,stderr=subprocess.PIPE,check=True).stdout
        assert (root/(path+".meta")).read_bytes()==oldmeta
        changed.append({"path":path,**new})
    else:
        assert new==old
assert sum(t["size"] for t in changed)<350_000_000
manifest={"kind":"native_deep_forest_terrain_lfs_bake","sourceSha":expected,
          "converted":report["converted"],"paths":changed}
(out/"bake-manifest.json").write_text(json.dumps(manifest,sort_keys=True,indent=2))
with tarfile.open(out/"package.tar.gz","w:gz") as tar:
    tar.add(out/"bake-manifest.json",arcname="bake-manifest.json")
    tar.add(report_file,arcname="bake-report.json")
    for entry in changed:
        tar.add(root/entry["path"],arcname=entry["path"])
print("FOREST_BAKE_NATIVE_TERRAINDATA_AUDIT_PASSED")
print("forest_conversion_count="+str(report["converted"]))
print("terrain_assets_changed="+str(len(changed)))
PYPACKAGE
# Re-open the saved native scene in the same original licensed Unity Editor
# (clean isolated worktree) and photograph the actual converted forest.
rm -f "$PHOTO/photo-detail.png" "$PHOTO/photo-context.png" "$PHOTO/photo-report.json"
set +e
timeout --signal=TERM 1450 env DISPLAY=:99 TMPDIR="$OUT/UnityTemp" LIBGL_ALWAYS_SOFTWARE=1 \
  "$EDITOR" -batchmode -quit -force-glcore -projectPath "$PROJECT" \
  -executeMethod Aaris.MapReview.Editor.AarisCodespaceSceneProbe.CaptureAt \
  "-aarisX=418" "-aarisZ=-350" "-aarisYaw=40" "-aarisPitch=18" "-aarisDistance=30" \
  -logFile "$OUT/private-photo-editor.log" >"$OUT/private-photo-stdout.log" 2>"$OUT/private-photo-stderr.log"
rc=$?
set -e
if test "$rc" -ne 0; then echo FOREST_BAKE_NATIVE_PHOTO_FAILED; exit 23; fi
python3 - "$PHOTO" <<'PYPHOTO'
import json,pathlib,sys
base=pathlib.Path(sys.argv[1])
j=json.loads((base/"photo-report.json").read_text())
assert j["status"]=="passed" and j["terrainTiles"]==4
assert abs(j["targetX"]-418)<.2 and abs(j["targetZ"]+350)<.2
assert min(j["detailVariation"],j["contextVariation"])>=6
for kind in ("detail","context"):
    path=base/("photo-"+kind+".png")
    assert path.stat().st_size>8192 and path.open("rb").read(8)==b"\x89PNG\r\n\x1a\n"
PYPHOTO
cp "$PHOTO/photo-detail.png" "$OUT/forest-detail.png"
cp "$PHOTO/photo-context.png" "$OUT/forest-context.png"
cp "$PHOTO/photo-report.json" "$OUT/forest-photo-report.json"
cp "$REPORT" "$OUT/bake-report.json"
echo FOREST_BAKE_NATIVE_RENDERED_AND_PACKAGE_READY
