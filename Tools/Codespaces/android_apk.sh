#!/usr/bin/env bash
# Build Aaris Android APK inside an existing owner's Codespace only.
# No original dirty GUI workspace/reset/commit, no tokens/license files exported.
set -Eeuo pipefail
MODE="${1:-preflight}"
case "$MODE" in preflight|build|build_hud) ;; *) echo APK_UNSUPPORTED_MODE; exit 2 ;; esac
SOURCE=/workspaces/Unity-Game-Aris
BASE=/tmp/aaris-codespaces/android-apk
VERSION=6000.3.26f1
mkdir -p "$BASE"
EDITOR="$HOME/.local/share/aaris-unity-editors/$VERSION/Editor/Unity"
for candidate in "$EDITOR" "/workspaces/.aaris-unity-editors/$VERSION/Editor/Unity" \
  "/tmp/aaris-unity-runtime/editors/$VERSION/Editor/Unity"; do
  if test -x "$candidate"; then EDITOR="$candidate"; break; fi
done
# A previous attempt installed a complete *isolated* official Unity Editor.
# Prefer that verified cached copy on retries instead of spending time
# reinstalling Android modules into an unrelated, manually installed Editor.
if test -d "$BASE/managed-editors"; then
  while IFS= read -r candidate; do
    mod="$(dirname "$candidate")/Data/PlaybackEngines/AndroidPlayer"
    if test -x "$candidate" && test -d "$mod/Variations" &&
       test -d "$mod/SDK/platform-tools" && test -d "$mod/SDK/build-tools" &&
       test -d "$mod/NDK/toolchains" && test -x "$mod/OpenJDK/bin/java"; then
      EDITOR="$candidate"
      echo APK_CACHED_OFFICIAL_ANDROID_EDITOR_REUSED
      break
    fi
  done < <(find "$BASE/managed-editors" -maxdepth 7 -path '*/Editor/Unity' -print 2>/dev/null)
fi
if ! test -x "$EDITOR"; then echo APK_EDITOR_MISSING; exit 11; fi
E_ROOT="$(dirname "$EDITOR")"
ANDROID="$E_ROOT/Data/PlaybackEngines/AndroidPlayer"
AUTH_HOME="$(getent passwd "$(id -u)" | cut -d: -f6)"
if ! test -d "$AUTH_HOME"; then AUTH_HOME="$HOME"; fi
CLI=""
for tool in "$AUTH_HOME/.local/bin/unity" \
            "$AUTH_HOME/.local/share/aaris-unity-cli/bin/unity" \
            "$HOME/.local/bin/unity" \
            "/tmp/aaris-unity-runtime/cli/bin/unity"; do
  if test -x "$tool"; then CLI="$tool"; break; fi
done
if test -z "$CLI"; then CLI="$(command -v unity || true)"; fi
if test -n "$CLI" && env HOME="$AUTH_HOME" \
    UNITY_CLI_HOME="$AUTH_HOME/.local/share/aaris-unity-cli" \
    "$CLI" license status --format json --non-interactive >/dev/null 2>&1; then
  echo APK_LICENSE_ACTIVE
else
  echo APK_LICENSE_NOT_VERIFIED
  if test "$MODE" != preflight; then exit 12; fi
fi
test -s "$SOURCE/ProjectSettings/EditorBuildSettings.asset" || {
  echo APK_BUILD_SETTINGS_MISSING; exit 13;
}
if grep -q 'path: Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity' \
  "$SOURCE/ProjectSettings/EditorBuildSettings.asset"; then
  echo APK_MAIN_SCENE_REGISTERED
else
  echo APK_MAIN_SCENE_NOT_REGISTERED; exit 14
fi
if test -e "$SOURCE/.git"; then echo APK_SOURCE_WORKSPACE_PRESENT;
else echo APK_SOURCE_WORKSPACE_NOT_FOUND; exit 15; fi
APK_MODULE=missing
if test -d "$ANDROID/Variations"; then APK_MODULE=present; fi
SDK=missing
if test -d "$ANDROID/SDK/platform-tools" && test -d "$ANDROID/SDK/build-tools"; then
  SDK=present
fi
NDK=missing
if test -d "$ANDROID/NDK" && test -d "$ANDROID/NDK/toolchains"; then
  NDK=present
fi
JDK=missing
if test -x "$ANDROID/OpenJDK/bin/java"; then JDK=present; fi
echo "apk_android_module=$APK_MODULE"
echo "apk_android_sdk=$SDK"
echo "apk_android_ndk=$NDK"
echo "apk_android_jdk=$JDK"
if test -n "$CLI"; then echo APK_UNITY_CLI_PRESENT
else echo APK_UNITY_CLI_MISSING; fi
echo "apk_tmp_free_gb=$(df -BG /tmp | awk 'END {gsub(/G/,"",$4);print $4}')"
echo APK_PREFLIGHT_RECORDED
if test "$MODE" = preflight; then exit 0; fi

# Official Unity CLI/Hub installations are the only supported source of
# Android SDK/NDK/JDK modules. If the original Unity Editor was installed
# manually, install the exact *same version* to isolated /tmp. Never touch the
# original Editor binary, its cache, the original GUI project or license files.
if test "$APK_MODULE" != present || test "$SDK" != present ||
   test "$NDK" != present || test "$JDK" != present; then
  if test -z "$CLI"; then echo APK_MODULE_INSTALL_CLI_MISSING; exit 16; fi
  echo APK_MODULE_INSTALL_START
  MODULE_OK=no
  if timeout 1200 env HOME="$AUTH_HOME" \
       UNITY_CLI_HOME="$AUTH_HOME/.local/share/aaris-unity-cli" \
       "$CLI" install-modules -e "$VERSION" -m android --cm \
       >"$BASE/private-module-install.log" 2>&1; then
    MODULE_OK=yes
    echo APK_EXISTING_CLI_MANAGED_EDITOR_MODULES_INSTALLED
  else
    echo APK_EDITOR_NOT_CLI_MANAGED_USING_ISOLATED_INSTALL
  fi
  if test "$MODULE_OK" = no; then
    test "$(df -BG /tmp | awk 'END {gsub(/G/,"",$4);print $4}')" -ge 25 || {
      echo APK_ISOLATED_INSTALL_INSUFFICIENT_TMP_DISK; exit 17;
    }
    MANAGED="$BASE/managed-editors"
    mkdir -p "$MANAGED"
    # The CLI install-path preference is user-local configuration. Save and
    # restore it on exit; do NOT reconfigure or move original Unity installs.
    PREV_INSTALL_PATH="$(env HOME="$AUTH_HOME" \
       UNITY_CLI_HOME="$AUTH_HOME/.local/share/aaris-unity-cli" \
       "$CLI" install-path --get 2>/dev/null | tail -n1 || true)"
    restore_install_path() {
      if test -n "$PREV_INSTALL_PATH" && test -d "$PREV_INSTALL_PATH"; then
        env HOME="$AUTH_HOME" \
           UNITY_CLI_HOME="$AUTH_HOME/.local/share/aaris-unity-cli" \
           "$CLI" install-path --set "$PREV_INSTALL_PATH" \
           >"$BASE/private-install-path-restore.log" 2>&1 || true
      fi
    }
    trap restore_install_path EXIT
    if ! env HOME="$AUTH_HOME" \
       UNITY_CLI_HOME="$AUTH_HOME/.local/share/aaris-unity-cli" \
       "$CLI" install-path --set "$MANAGED" \
       >"$BASE/private-install-path-setup.log" 2>&1; then
      echo APK_UNITY_CLI_TEMP_INSTALL_PATH_FAILED
      exit 18
    fi
    echo APK_ISOLATED_MANAGED_EDITOR_DOWNLOAD_STARTED
    if ! timeout 10500 env HOME="$AUTH_HOME" \
       UNITY_CLI_HOME="$AUTH_HOME/.local/share/aaris-unity-cli" \
       "$CLI" install "$VERSION" -c de8167e7440c -m android --cm --yes --accept-eula \
       >"$BASE/private-isolated-install.log" 2>&1; then
      echo APK_MANAGED_EDITOR_INSTALL_FAILED
      exit 19
    fi
    restore_install_path
    trap - EXIT
    echo APK_ISOLATED_MANAGED_EDITOR_INSTALLED
  fi
  # A CLI-managed copy might be in its own versioned parent directory.
  # Never silently build with an incompatible Unity version.
  LOOKUP=("$EDITOR"
    "$BASE/managed-editors/$VERSION/Editor/Unity"
    "$AUTH_HOME/Unity/Hub/Editor/$VERSION/Editor/Unity"
    "$AUTH_HOME/.local/share/unity3d/Hub/Editor/$VERSION/Editor/Unity")
  if test -d "$BASE/managed-editors"; then
    while IFS= read -r candidate; do LOOKUP+=("$candidate"); done < <(
      find "$BASE/managed-editors" -maxdepth 7 -path '*/Editor/Unity' -print
    )
  fi
  MATCH=""
  for candidate in "${LOOKUP[@]}"; do
    mod="$(dirname "$candidate")/Data/PlaybackEngines/AndroidPlayer"
    if test -x "$candidate" && test -d "$mod/Variations" &&
       test -d "$mod/SDK/platform-tools" && test -d "$mod/SDK/build-tools" &&
       test -d "$mod/NDK/toolchains" && test -x "$mod/OpenJDK/bin/java"; then
      MATCH="$candidate"
      break
    fi
  done
  if test -z "$MATCH"; then
    echo APK_ANDROID_MODULES_NOT_DISCOVERED; exit 20
  fi
  EDITOR="$MATCH"
  echo APK_ANDROID_MODULES_INSTALLED_AND_VERIFIED
fi
if pgrep -f '[E]ditor/Unity.*-projectPath /workspaces/Unity-Game-Aris' >/dev/null 2>&1; then
  # The GUI can be frozen without a window; isolate the build in its OWN
  # immutable worktree rather than touching the original Unity process.
  echo APK_EXISTING_GUI_EDITOR_LEFT_UNCHANGED
fi
# The previous main SHA (PR #251) completed a native Unity build, but its
# Android metadata was overwritten by the legacy preprocessor callback.
# Before importing NEW main, reclaim only regenerable Library/Temp/obj
# of that one finished, SHA-verified, temporary isolated worktree. Never
# touch original /workspaces, any Git LFS object, or the current worktree.
OLD_SHA=93ee9c452785741aff302d425ef315f664b13f5f
OLD_PROJECT="/tmp/aaris-native-android-build-worktrees/$OLD_SHA"
if test -d "$OLD_PROJECT" && test ! -L "$OLD_PROJECT" &&
   test "$(realpath -e "$OLD_PROJECT")" = "$OLD_PROJECT" &&
   test "$(git -C "$OLD_PROJECT" rev-parse HEAD 2>/dev/null)" = "$OLD_SHA"; then
  if ps -eo args= | grep -F -- "$OLD_PROJECT" |
     grep -E '[/]Editor/Unity|[U]nity.*-projectPath' >/dev/null; then
    echo APK_PREVIOUS_WORKTREE_STILL_ACTIVE_NOT_CLEANED
  else
    for folder in Library Temp obj; do
      old_cache="$OLD_PROJECT/$folder"
      if test -d "$old_cache" && test ! -L "$old_cache" &&
         test "$(realpath -e "$old_cache")" = "$old_cache"; then
        rm -rf --one-file-system -- "$old_cache"
      fi
    done
    echo APK_PREVIOUS_FAILED_BUILD_IMPORT_CACHE_CLEARED
    echo "apk_tmp_free_gb_after_cache_cleanup=$(df -BG /tmp | awk 'END {gsub(/G/,"",$4);print $4}')"
  fi
fi
# Build an exact, hard-coded reviewed HUD/parachute branch on owner request;
# all ordinary builds still use main. No arbitrary ref comes from issue text.
REF_NAME=main
if test "$MODE" = build_hud; then
  REF_NAME=fix/android-ugui-shader-parachute-20261110
fi
# The active Codespace may use a different owner's fork as "origin".
# Never build that fork by accident: fetch the canonical game repository explicitly.
CANONICAL_REPO=https://github.com/sundamunda289-art/Unity-Game-Aris.git
SOURCE_REF="refs/aaris-apk-reviewed/$REF_NAME"
git -C "$SOURCE" fetch --quiet --no-tags "$CANONICAL_REPO" "+refs/heads/$REF_NAME:$SOURCE_REF" || {
  echo APK_CANONICAL_GAME_REF_FETCH_FAILED; exit 20; }
SOURCE_HEAD="$(git -C "$SOURCE" rev-parse --verify "$SOURCE_REF^{commit}")"
[[ "$SOURCE_HEAD" =~ ^[0-9a-f]{40}$ ]] || { echo APK_MAIN_SHA_INVALID; exit 21; }
SHA="$SOURCE_HEAD"
# Build the exact reviewed source commit, even when monitoring/workflow fixes
# later landed on main. Ensure it remains an ancestor of main; never accept an
# arbitrary SHA supplied through public issue comments.
if test -n "${AARIS_EXPECTED_BUILD_SHA:-}"; then
  [[ "$AARIS_EXPECTED_BUILD_SHA" =~ ^[0-9a-f]{40}$ ]] || exit 21
  git -C "$SOURCE" merge-base --is-ancestor "$AARIS_EXPECTED_BUILD_SHA" "$SOURCE_HEAD" || {
    echo APK_REQUESTED_GAME_SNAPSHOT_NOT_ON_MAIN; exit 21;
  }
  SHA="$AARIS_EXPECTED_BUILD_SHA"
  echo APK_REVIEWED_GAME_SNAPSHOT_PINNED
fi
echo APK_BUILD_SOURCE_PINNED
ROOT=/tmp/aaris-native-android-build-worktrees
PROJECT="$ROOT/$SHA"
mkdir -p "$ROOT"
# Build from a standalone, temporary clean clone. Never register a new Git
# worktree, and never checkout/reset/stash/clean the active GUI repository.
# The shared Git object store avoids duplicating multi-GB history in /tmp.
if test ! -d "$PROJECT"; then
  test ! -e "$PROJECT" || { echo APK_TEMP_PROJECT_PATH_UNSAFE; exit 22; }
  GIT_LFS_SKIP_SMUDGE=1 git -c filter.lfs.process= \
    -c filter.lfs.smudge=cat -c filter.lfs.required=false \
    clone --quiet --shared --no-checkout "$SOURCE" "$PROJECT" >/dev/null 2>&1 || {
      echo APK_ISOLATED_CLONE_FAILED; exit 23; }
  # Temporary clone needs the canonical LFS endpoint too, not the local
  # source clone's possibly unrelated default origin / fork.
  git -C "$PROJECT" remote set-url origin "$CANONICAL_REPO" || {
    echo APK_CANONICAL_LFS_ORIGIN_FAILED; exit 23; }
  GIT_LFS_SKIP_SMUDGE=1 git -C "$PROJECT" -c filter.lfs.process= \
    -c filter.lfs.smudge=cat -c filter.lfs.required=false \
    checkout --quiet --detach "$SHA" >/dev/null 2>&1 || {
      echo APK_ISOLATED_CHECKOUT_FAILED; exit 23; }
fi
test -d "$PROJECT/.git" && test ! -L "$PROJECT" || {
  echo APK_ISOLATED_PROJECT_NOT_A_CLONE; exit 24; }
test "$(git -C "$PROJECT" rev-parse HEAD)" = "$SHA" || {
  echo APK_WORKTREE_SHA_MISMATCH; exit 24; }
cd "$PROJECT"
# Preserve all 822 current-main Git LFS objects, with authentic SHA verification.
if python3 Tools/Codespaces/restore_preserved_source_lfs.py "$PROJECT" \
    >"$BASE/private-lfs.log" 2>&1; then
  # Check the exact current 822-asset full snapshot, including new motions.
  grep -qxF "AARIS_PRESERVED_LFS_SNAPSHOT_VERIFIED=822" "$BASE/private-lfs.log" || {
    echo APK_EXPECTED_822_LFS_ASSETS_NOT_VERIFIED; exit 25;
  }
  echo APK_AUTHENTIC_822_LFS_VERIFIED
elif git lfs pull >"$BASE/private-lfs.log" 2>&1; then
  test "$(git lfs ls-files --name-only | wc -l)" -eq 822 || {
    echo APK_REMOTE_LFS_COUNT_INVALID; exit 25;
  }
  # An isolated worktree created with LFS smudge disabled can retain some
  # genuine Git LFS pointer files even after "git lfs pull" succeeded.
  # Checkout only that worktree from the authentic local LFS object store;
  # never modify the original /workspaces project or skip hash verification.
  git lfs checkout >>"$BASE/private-lfs.log" 2>&1 || {
    echo APK_REMOTE_LFS_CHECKOUT_FAILED; exit 25;
  }
  python3 - <<'PYLFS' || { echo APK_REMOTE_LFS_HASH_VERIFICATION_FAILED; exit 25; }
from pathlib import Path
from collections import Counter
import sys
sys.path.insert(0, "Tools/Codespaces")
from restore_preserved_source_lfs import audit_pointers, candidate_matches, original_cache_roots, object_path
ptrs = audit_pointers(Path.cwd())
assert len(ptrs) == 822, "APK_REMOTE_LFS_EXPECTED_COUNT_MISMATCH"
invalid=[]
categories=Counter()
for relative, (oid, size) in ptrs.items():
    path=Path(relative)
    if candidate_matches(path, oid, size):
        continue
    invalid.append((relative,oid,size))
    try:
        if path.stat().st_size < 400 and path.read_bytes().startswith(b"version https://git-lfs.github.com/spec"):
            categories["pointer_only"]+=1
        elif path.is_file():
            categories["wrong_bytes"]+=1
        else:
            categories["missing_file"]+=1
    except OSError:
        categories["missing_file"]+=1
# Emit only counts, never names, filenames or LFS OIDs to the public log.
print("APK_LFS_MISSING_AFTER_CHECKOUT="+str(len(invalid)))
for category,count in sorted(categories.items()):
    print("APK_LFS_"+category.upper()+"="+str(count))
if invalid:
    # Re-hydrate any LFS pointer left behind by worktree-specific filter setup,
    # using *only* source objects whose actual bytes match HEAD oid+size.
    import os,shutil,tempfile
    stores=original_cache_roots(Path("/workspaces/Unity-Game-Aris"))
    for relative,oid,size in invalid:
        dest=Path(relative)
        if dest.is_symlink():
            raise RuntimeError("APK_UNSAFE_LFS_SYMLINK")
        source=next((object_path(root,oid) for root in stores
                     if candidate_matches(object_path(root,oid),oid,size)),None)
        if source is None:
            continue
        with tempfile.NamedTemporaryFile(dir=dest.parent,prefix=".aaris-lfs-",delete=False) as tmp:
            tmp_path=Path(tmp.name)
            try:
                with source.open("rb") as stream:
                    shutil.copyfileobj(stream,tmp,1024*1024)
                tmp.flush()
                os.fsync(tmp.fileno())
            except BaseException:
                tmp_path.unlink(missing_ok=True)
                raise
        try:
            if not candidate_matches(tmp_path,oid,size):
                raise RuntimeError("APK_LFS_COPIED_HASH_MISMATCH")
            os.replace(tmp_path,dest)
        finally:
            tmp_path.unlink(missing_ok=True)
assert all(candidate_matches(Path(path),oid,size)
           for path,(oid,size) in ptrs.items()),"APK_LFS_INTEGRITY_INCOMPLETE"
PYLFS
  echo APK_REMOTE_822_LFS_SHA256_VERIFIED
else
  echo APK_LFS_HYDRATION_FAILED; exit 25
fi
if head -c 110 Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity |
    grep -q 'git-lfs.github.com/spec'; then
  echo APK_NATIVE_SCENE_LFS_POINTER; exit 26
fi
test -s Assets/AarisWorld/Editor/AarisCodespaceAndroidBuild.cs || {
  echo APK_BUILD_SCRIPT_MISSING; exit 27; }
if test "$MODE" = build_hud; then
  test -s Assets/Resources/AarisHUDOverlay.shader || {
    echo APK_HUD_REVIEW_SHADER_SOURCE_MISSING; exit 27;
  }
  grep -q 'ApplyCanvasShader' Assets/AarisWorld/Scripts/AarisHudGlyphs.cs || {
    echo APK_HUD_REVIEW_CANVAS_BINDING_MISSING; exit 27;
  }
  echo APK_HUD_REVIEW_BRANCH_AND_SHADER_VERIFIED
fi
bash .devcontainer/start_screen.sh >"$BASE/private-screen.log" 2>&1 || {
  echo APK_DISPLAY_START_FAILED; exit 28
}
DISPLAY=:99 xdpyinfo >/dev/null 2>&1 || {
  echo APK_DISPLAY_MISSING; exit 29; }
mkdir -p "$BASE/UnityTemp" "$BASE/Build"
rm -f "$BASE/Build/AarisGame.apk" "$BASE/build-report.json"
ANDROID="$(dirname "$EDITOR")/Data/PlaybackEngines/AndroidPlayer"
COMPILE_SDK=
# The bundled SDK can lack API 33. Keep the manifest target at 33 and use
# installed API 34 for compilation when the ordinary main builder needs it.
if test "$MODE" = build && ! test -s "$ANDROID/SDK/platforms/android-33/android.jar"; then
  test -s "$ANDROID/SDK/platforms/android-34/android.jar" || {
    echo APK_ANDROID_COMPILE_PLATFORM_MISSING; exit 29;
  }
  COMPILE_SDK=34
fi
echo APK_NATIVE_UNITY_BUILD_STARTED
# -buildTarget Android is required BEFORE executeMethod to avoid broken
# batchmode domain reload/scripting target switches.
set +e
timeout --signal=TERM 12000 env DISPLAY=:99 LIBGL_ALWAYS_SOFTWARE=1 \
  AARIS_ANDROID_COMPILE_SDK="$COMPILE_SDK" TMPDIR="$BASE/UnityTemp" \
  "$EDITOR" -batchmode -quit -force-glcore -job-worker-count 2 \
  -projectPath "$PROJECT" -buildTarget Android \
  -executeMethod Aaris.MapReview.Editor.AarisCodespaceAndroidBuild.BuildTestApk \
  -logFile "$BASE/private-editor.log" \
  >"$BASE/private-stdout.log" 2>"$BASE/private-stderr.log"
RC=$?
set -e
if test "$RC" -ne 0; then
  # Emit only fixed error categories. Private Unity logs, license state,
  # shader paths, stack traces and identity values stay on the Codespace.
  case "$RC" in
    124) echo APK_EDITOR_TIMEOUT ;;
    137) echo APK_EDITOR_KILLED_OR_OUT_OF_MEMORY ;;
    139) echo APK_EDITOR_SEGMENTATION_FAULT ;;
    134) echo APK_EDITOR_NATIVE_ABORT ;;
    143) echo APK_EDITOR_TERMINATED_OR_CODESPACE_STOPPED ;;
    *) echo APK_EDITOR_NONZERO_EXIT ;;
  esac
  if grep -Eq 'error CS[0-9]{4}' "$BASE/private-editor.log" 2>/dev/null; then
    echo APK_CSHARP_COMPILE_FAILED
  elif grep -Eqi 'licen[sc]e.*(invalid|expired|missing|failed)' "$BASE/private-editor.log" 2>/dev/null; then
    echo APK_EDITOR_LICENSE_FAILED
  elif grep -Eqi 'SIGSEGV|segmentation fault' "$BASE/private-editor.log" 2>/dev/null; then
    echo APK_EDITOR_NATIVE_CRASH_IN_LOG
  elif test -s "$BASE/build-report.json"; then
    python3 - "$BASE/build-report.json" <<'PYREPORT'
import json,sys
try:
    reason=json.load(open(sys.argv[1])).get("reason","")
except (OSError,ValueError):
    reason=""
if (isinstance(reason,str) and reason.isupper() and
    reason.isascii() and 3 <= len(reason) <= 70 and
    all(x.isupper() or x.isdigit() or x=="_" for x in reason)):
    print("APK_BUILD_REASON_"+reason)
else:
    print("APK_BUILD_REPORT_FAILURE_UNCLASSIFIED")
PYREPORT
  else
    echo APK_EDITOR_EXIT_BEFORE_BUILD_METHOD
  fi
  exit 30
fi
python3 - "$BASE" <<'PY'
import json,pathlib,sys,hashlib,zipfile
root=pathlib.Path(sys.argv[1])
p=root/"Build/AarisGame.apk"
j=root/"build-report.json"
try:
    d=json.loads(j.read_text())
    assert d["status"]=="passed" and d["terrainTiles"]==4
    assert d["buildTarget"]=="Android" and d["sceneCount"]==1
    assert p.is_file() and 100000<p.stat().st_size<2_000_000_000
    with zipfile.ZipFile(p) as z:
        names=set(z.namelist())
        assert len(names)==len(z.infolist()), "duplicate ZIP entries"
        assert "AndroidManifest.xml" in names
        assert "classes.dex" in names
        assert z.testzip() is None, "bad ZIP CRC"
    with p.open("rb") as stream:
        digest=hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    sha=digest.hexdigest()
    (root/"apk-sha256.txt").write_text(sha+"  AarisGame.apk\n")
except Exception:
    raise SystemExit("APK_ARCHIVE_OR_NATIVE_REPORT_INVALID")
print("APK_BUILD_SUCCESS_VALID_ZIP")
print("apk_bytes="+str(p.stat().st_size))
PY
# Validate actual Android installation metadata before exposing an APK.
# The prior build was a valid ZIP, but did not check signing/API compatibility.
AARIS_ANDROID_PLAYER_ROOT="$ANDROID" bash Tools/Codespaces/apk_install_diagnostic.sh \
  > "$BASE/private-native-installability-report.log" 2>&1 || {
    echo APK_INSTALLABILITY_TOOLS_FAILED; exit 32;
  }
CHECK="$BASE/private-native-installability-report.log"
grep -qxF APK_INSTALL_DIAG_APKSIGNER_VERIFY_PASS "$CHECK" || {
  echo APK_SIGNER_VERIFICATION_FAILED; exit 33;
}
grep -qxF APK_INSTALL_DIAG_ZIPALIGN_PASS "$CHECK" || {
  echo APK_ZIPALIGN_VERIFICATION_FAILED; exit 34;
}
grep -qxF apk_signature_v2=true "$CHECK" || {
  echo APK_V2_SIGNATURE_MISSING; exit 35;
}
grep -qxF apk_signer_count=1 "$CHECK" || {
  echo APK_SIGNER_COUNT_INVALID; exit 36;
}
grep -qxF apk_android_manifest=yes "$CHECK"
grep -qxF apk_classes_dex=yes "$CHECK"
grep -qxF apk_arm64_unity_lib=yes "$CHECK" || {
  echo APK_ARM64_NATIVE_LIBRARIES_MISSING; exit 36;
}
if test "$MODE" = build; then
  grep -qxF apk_armv7_unity_lib=yes "$CHECK" || {
    echo APK_ARMV7_NATIVE_LIBRARIES_MISSING; exit 36;
  }
  grep -qxF APK_INSTALL_DIAG_ZIPALIGN_16K_PASS "$CHECK" || {
    echo APK_ZIPALIGN_16K_VERIFICATION_FAILED; exit 36;
  }
fi
grep -qxF apk_min_sdk=26 "$CHECK"
grep -qxF apk_target_sdk=33 "$CHECK" || {
  echo APK_ANDROID_API_TARGET_UNEXPECTED; exit 37;
}
# New standalone Aaris Game package must not conflict with mapexplorer.
AAPT="$(find "$ANDROID/SDK/build-tools" -maxdepth 2 -type f -name aapt -print -quit 2>/dev/null || true)"
if ! test -x "$AAPT"; then echo APK_AAPT_MISSING; exit 38; fi
timeout 120 "$AAPT" dump badging "$BASE/Build/AarisGame.apk" \
  > "$BASE/private-package-id-report.log" 2>&1 || exit 39
grep -Eq "^package: name='com[.]aaris[.]game' versionCode='7'" \
  "$BASE/private-package-id-report.log" || {
    echo APK_PACKAGE_ID_OR_VERSION_INVALID; exit 40;
  }
echo APK_INSTALLABILITY_SIGNATURE_MANIFEST_API33_VERIFIED
echo "apk_commit=$SHA"
if test "$MODE" = build_hud; then echo APK_HUD_REVIEW_BUILD_VERIFIED; fi
echo APK_UPLOAD_READY
