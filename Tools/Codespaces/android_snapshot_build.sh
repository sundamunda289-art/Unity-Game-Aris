#!/usr/bin/env bash
# Android APK build in ONE existing Codespace; NO git worktree/checkout/reset.
# Read-only source snapshot in /tmp: preserves the concurrent Unity GUI project.
# Requires official Unity+Android support already installing in managed-editors.
set -Eeuo pipefail
umask 077
BASE=/tmp/aaris-codespaces/android-apk
SOURCE=/workspaces/Unity-Game-Aris
EDITOR="$BASE/managed-editors/6000.3.26f1/Editor/Unity"
ANDROID="$BASE/managed-editors/6000.3.26f1/Editor/Data/PlaybackEngines/AndroidPlayer"
STATUS="$BASE/native-build-stage"
mkdir -p "$BASE"
: > "$STATUS"
say() { printf '%s\n' "$*" >> "$STATUS"; }
say "BUILD_JOB_QUEUED_UTC=$(date -u +%FT%TZ)"
for attempt in $(seq 1 720); do
  if grep -qxF AARIS_OFFICIAL_UNITY_ANDROID_INSTALL_SUCCEEDED "$BASE/android-install-state" 2>/dev/null; then
    break
  fi
  if grep -q AARIS_OFFICIAL_UNITY_ANDROID_INSTALL_FAILED_EXIT "$BASE/android-install-state" 2>/dev/null; then
    say AARIS_BUILD_BLOCKED_ANDROID_INSTALL_FAILED
    exit 14
  fi
  sleep 15
done
grep -qxF AARIS_OFFICIAL_UNITY_ANDROID_INSTALL_SUCCEEDED "$BASE/android-install-state" || {
  say AARIS_BUILD_BLOCKED_ANDROID_INSTALL_TIMEOUT; exit 15;
}
for file in "$EDITOR" "$ANDROID/OpenJDK/bin/java" "$ANDROID/SDK/platforms/android-34/android.jar"; do
  test -s "$file" || { say AARIS_BUILD_BLOCKED_ANDROID_TOOLS_INCOMPLETE; exit 16; }
done
test -d "$ANDROID/NDK/toolchains" && test -d "$ANDROID/SDK/build-tools" || {
  say AARIS_BUILD_BLOCKED_ANDROID_TOOLS_INCOMPLETE; exit 16;
}
say AARIS_ANDROID_BUILD_SUPPORT_VERIFIED
test "$(df -BG /tmp | awk 'END{gsub(/G/,"",$4);print $4}')" -ge 35 || {
  say AARIS_BUILD_BLOCKED_TMP_DISK_LOW; exit 17;
}
cd "$SOURCE"
SHA="$(git rev-parse HEAD)"
BRANCH="$(git branch --show-current)"
[[ "$SHA" =~ ^[0-9a-f]{40}$ ]] || exit 18
if ! git diff --quiet || ! git diff --cached --quiet; then
  say AARIS_BUILD_BLOCKED_CONCURRENT_TRACKED_SOURCE_EDITS
  exit 18
fi
say "AARIS_BUILD_SOURCE_SHA=$SHA"
say "AARIS_BUILD_SOURCE_BRANCH=$BRANCH"
PROJECT="$BASE/native-source-$SHA"
test ! -e "$PROJECT" || { say AARIS_BUILD_BLOCKED_SNAPSHOT_ALREADY_EXISTS; exit 19; }
mkdir "$PROJECT"
say AARIS_SOURCE_SNAPSHOT_COPY_STARTED
tar -C "$SOURCE" \
  --exclude='./.git' --exclude='./Library' --exclude='./Logs' \
  --exclude='./obj' --exclude='./Temp' --exclude='./UserSettings' \
  --exclude='./mono_crash.mem.*' --exclude='*.ulf' \
  --exclude='*.secret' --exclude='*.token' \
  -cf - . | tar -C "$PROJECT" -xf -
if [[ "$(git -C "$SOURCE" rev-parse HEAD)" != "$SHA" ]] ||
   ! git -C "$SOURCE" diff --quiet ||
   ! git -C "$SOURCE" diff --cached --quiet; then
  say AARIS_BUILD_BLOCKED_SOURCE_CHANGED_DURING_COPY; exit 20
fi
SCENE="$PROJECT/Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity"
test -s "$SCENE" || { say AARIS_BUILD_BLOCKED_SOURCE_SCENE_MISSING; exit 21; }
if head -c 130 "$SCENE" | grep -q git-lfs.github.com/spec; then
  say AARIS_BUILD_BLOCKED_SCENE_IS_LFS_POINTER; exit 22
fi
say AARIS_SOURCE_SNAPSHOT_READY_NO_GIT_WORKTREE
mkdir -p "$BASE/UnityTemp" "$BASE/Build"
test ! -e "$BASE/Build/AarisGame.apk" && test ! -e "$BASE/build-report.json" || {
  say AARIS_BUILD_BLOCKED_EXISTING_APK_OR_REPORT; exit 23
}
DISPLAY=:99 xdpyinfo >/dev/null 2>&1 || { say AARIS_BUILD_BLOCKED_DISPLAY_MISSING; exit 24; }
say AARIS_NATIVE_ANDROID_BUILD_STARTED
set +e
timeout --signal=TERM --kill-after=30 14400 env \
  DISPLAY=:99 LIBGL_ALWAYS_SOFTWARE=1 \
  AARIS_ANDROID_COMPILE_SDK=34 TMPDIR="$BASE/UnityTemp" \
  JAVA_HOME="$ANDROID/OpenJDK" \
  "$EDITOR" -batchmode -quit -force-glcore -job-worker-count 2 \
  -projectPath "$PROJECT" -buildTarget Android \
  -executeMethod Aaris.MapReview.Editor.AarisCodespaceAndroidBuild.BuildTestApk \
  -logFile "$BASE/private-native-unity-build.log" \
  >"$BASE/private-native-unity-stdout.log" \
  2>"$BASE/private-native-unity-stderr.log"
RC="$?"
set -e
if [[ "$RC" != 0 ]]; then
  say "AARIS_NATIVE_ANDROID_BUILD_FAILED_EXIT=$RC"
  if grep -Eq 'error CS[0-9]{4}' "$BASE/private-native-unity-build.log" 2>/dev/null; then
    say AARIS_NATIVE_ANDROID_CSHARP_ERRORS
  fi
  exit 25
fi
say AARIS_NATIVE_ANDROID_EDITOR_RETURNED_SUCCESS
export JAVA_HOME="$ANDROID/OpenJDK"
export PATH="$JAVA_HOME/bin:$PATH"
APKSIGNER="$(find "$ANDROID/SDK/build-tools" -maxdepth 2 -type f -name apksigner -print -quit)"
ZIPALIGN="$(find "$ANDROID/SDK/build-tools" -maxdepth 2 -type f -name zipalign -print -quit)"
AAPT="$(find "$ANDROID/SDK/build-tools" -maxdepth 2 -type f -name aapt -print -quit)"
APK="$BASE/Build/AarisGame.apk"
test -s "$APK" && test -x "$APKSIGNER" && test -x "$ZIPALIGN" && test -x "$AAPT" || {
  say AARIS_APK_TOOLS_OR_OUTPUT_MISSING; exit 26
}
say AARIS_APK_VALIDATION_STARTED
timeout 240 "$APKSIGNER" verify --verbose --print-certs "$APK" \
  >"$BASE/private-new-signature.log" 2>&1 || { say AARIS_APK_SIGNATURE_INVALID; exit 27; }
grep -qE 'Verified using v2 scheme.*: true' "$BASE/private-new-signature.log" || {
  say AARIS_APK_V2_SIGNATURE_MISSING; exit 28;
}
grep -qE '^Number of signers: 1$' "$BASE/private-new-signature.log" || {
  say AARIS_APK_UNEXPECTED_SIGNER_COUNT; exit 28;
}
timeout 120 "$ZIPALIGN" -c -p 4 "$APK" \
  >"$BASE/private-new-zipalign.log" 2>&1 || { say AARIS_APK_ZIPALIGN_FAILED; exit 29; }
timeout 120 "$AAPT" dump badging "$APK" \
  >"$BASE/private-new-aapt.log" 2>&1 || { say AARIS_APK_MANIFEST_UNREADABLE; exit 30; }
python3 - "$BASE" <<'PY'
from pathlib import Path
import zipfile, hashlib, re, json, sys
root = Path(sys.argv[1])
apk = root / "Build/AarisGame.apk"
aapt = (root / "private-new-aapt.log").read_text(errors="replace")
m = re.search(r"^package: name='([^']+)' versionCode='([0-9]+)'", aapt, re.M)
if not m or m.group(1) != "com.aaris.game" or int(m.group(2)) != 7:
    raise SystemExit("INVALID_PACKAGE_OR_VERSION")
min_match = re.search(r"^sdkVersion:'([0-9]+)'", aapt, re.M)
target_match = re.search(r"^targetSdkVersion:'([0-9]+)'", aapt, re.M)
if not min_match or not target_match or int(min_match.group(1)) > 33:
    raise SystemExit("ANDROID_13_MIN_SDK_NOT_SUPPORTED")
with zipfile.ZipFile(apk) as z:
    names = set(z.namelist())
    assert len(names) == len(z.infolist()), "duplicate APK entries"
    assert "AndroidManifest.xml" in names and "classes.dex" in names
    assert any(n.startswith("lib/arm64-v8a/") and n.endswith(".so") for n in names)
    assert any(n.startswith("lib/armeabi-v7a/") and n.endswith(".so") for n in names)
    assert z.testzip() is None, "APK ZIP CRC failed"
digest = hashlib.sha256()
with apk.open("rb") as stream:
    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
        digest.update(chunk)
sha256 = digest.hexdigest()
(root / "Build/AarisGame.sha256").write_text(sha256 + "  AarisGame.apk\n")
(root / "native-package-verified.json").write_text(json.dumps({
    "status": "verified-archive-and-v2-signature",
    "package": m.group(1),
    "versionCode": int(m.group(2)),
    "minSdk": int(min_match.group(1)),
    "targetSdk": int(target_match.group(1)),
    "bytes": apk.stat().st_size,
    "sha256": sha256
}))
PY
say AARIS_APK_SIGNATURE_PACKAGE_ABI_MANIFEST_CRC_SHA256_VERIFIED
say AARIS_APK_LOCAL_BUILD_READY_NEEDS_REAL_DEVICE_INSTALL_TEST
