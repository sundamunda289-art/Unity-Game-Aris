#!/usr/bin/env bash
# Rebuild a fixed-version APK in a previously validated /tmp Unity snapshot.
# Never touch the live Unity GUI project, original APK, source Git or worktrees.
set -Eeuo pipefail
umask 077
BASE=/tmp/aaris-codespaces/android-apk
SHA=1a56c09fab08ae1f03751ca95eac4c01eeaa7f84
PROJECT="$BASE/native-source-$SHA"
EDITOR="$BASE/managed-editors/6000.3.26f1/Editor/Unity"
SDK="$BASE/managed-editors/6000.3.26f1/Editor/Data/PlaybackEngines/AndroidPlayer"
STAGE="$BASE/installfix-v8-stage"
APK="$BASE/Build/AarisGame.apk"
PREVIOUS_SHA=07dd49c30571d4fdd6203ce7a0fbb1b3339180135e77131ac0e4999b0d605464
CERT=c469f27b142a0d85c766700d05bbfe7c3fd55b888ffa9e36a5a72cda65aa3eb1
say() { echo "$1" >> "$STAGE"; }
: > "$STAGE"
say AARIS_V8_REBUILD_QUEUED
test -x "$EDITOR" && test -d "$PROJECT/Library" && test -s "$APK" || {
  say AARIS_V8_PREVIOUS_NATIVE_BUILD_MISSING; exit 11;
}
echo "$PREVIOUS_SHA  $APK" | sha256sum -c - >/dev/null || {
  say AARIS_V8_PREVIOUS_BUILD_HASH_MISMATCH; exit 12;
}
test -s "$HOME/.android/debug.keystore" || { say AARIS_V8_SIGNING_KEY_MISSING; exit 13; }
export JAVA_HOME="$SDK/OpenJDK" PATH="$SDK/OpenJDK/bin:$PATH"
KEY_CERT="$("$JAVA_HOME/bin/keytool" -list -v \
  -keystore "$HOME/.android/debug.keystore" -storepass android \
  -alias androiddebugkey 2>/dev/null |
  sed -n 's/.*SHA256: //p' | head -1 | tr -d ':' | tr '[:upper:]' '[:lower:]')"
test "$KEY_CERT" = "$CERT" || { say AARIS_V8_EXISTING_SIGNER_CHANGED; exit 14; }
say AARIS_V8_OLD_SIGNING_IDENTITY_CONFIRMED
# Patch ONLY the saved temporary /tmp native source, never the owner workspace.
python3 - "$PROJECT" <<'PY'
from pathlib import Path
import sys
p=Path(sys.argv[1])
assert p.is_dir() and str(p).startswith("/tmp/aaris-codespaces/android-apk/native-source-")
targets={
"Assets/AarisWorld/Editor/AarisCodespaceAndroidBuild.cs":[
 ("ExpectedVersion = \"0.3.1\"", "ExpectedVersion = \"0.3.2\""),
 ("ExpectedVersionCode = 7", "ExpectedVersionCode = 20261112"),
 ("PlayerSettings.bundleVersion = \"0.3.1\"", "PlayerSettings.bundleVersion = \"0.3.2\""),
 ("PlayerSettings.Android.bundleVersionCode = 7","PlayerSettings.Android.bundleVersionCode = 20261112"),
 ("PlayerSettings.Android.bundleVersionCode != 7","PlayerSettings.Android.bundleVersionCode != 20261112")],
"Assets/AarisWorld/Editor/AarisExplorerBuild.cs":[
 ("PlayerSettings.bundleVersion = \"0.3.1\"","PlayerSettings.bundleVersion = \"0.3.2\""),
 ("PlayerSettings.Android.bundleVersionCode = 7","PlayerSettings.Android.bundleVersionCode = 20261112")],
"ProjectSettings/ProjectSettings.asset":[
 ("  bundleVersion: 0.3.1","  bundleVersion: 0.3.2"),
 ("  AndroidBundleVersionCode: 7","  AndroidBundleVersionCode: 20261112")]
}
for name,changes in targets.items():
 f=p/name
 text=f.read_text()
 for old,new in changes:
  if text.count(old)!=1: raise RuntimeError("VERSION_PATCH_PRECONDITION_FAILED_"+name)
  text=text.replace(old,new)
 f.write_text(text)
print("AARIS_V8_VERSION_PATCH_VERIFIED")
PY
say AARIS_V8_VERSION_PATCH_VERIFIED
mkdir -p "$BASE/prior-v7"
test ! -e "$BASE/prior-v7/AarisGame.apk" || { say AARIS_V8_BACKUP_ALREADY_EXISTS; exit 15; }
mv "$APK" "$BASE/prior-v7/AarisGame.apk"
for n in build-report.json native-package-verified.json; do
  if test -s "$BASE/$n"; then mv "$BASE/$n" "$BASE/prior-v7/$n"; fi
done
test ! -e "$APK" || { say AARIS_V8_OUTPUT_NOT_EMPTY; exit 16; }
say AARIS_V7_PREVIOUS_APK_PRESERVED
DISPLAY=:99 xdpyinfo >/dev/null || { say AARIS_V8_DISPLAY_UNAVAILABLE; exit 17; }
say AARIS_V8_UNITY_BUILD_STARTED
set +e
timeout --signal=TERM --kill-after=30 12000 env \
 DISPLAY=:99 LIBGL_ALWAYS_SOFTWARE=1 AARIS_ANDROID_COMPILE_SDK=34 \
 TMPDIR="$BASE/UnityTemp" \
 "$EDITOR" -batchmode -quit -force-glcore -job-worker-count 2 \
 -projectPath "$PROJECT" -buildTarget Android \
 -executeMethod Aaris.MapReview.Editor.AarisCodespaceAndroidBuild.BuildTestApk \
 -logFile "$BASE/private-v8-editor.log" \
 >"$BASE/private-v8-stdout.log" 2>"$BASE/private-v8-stderr.log"
RC=$?
set -e
if test "$RC" -ne 0; then say "AARIS_V8_UNITY_BUILD_FAILED_EXIT_$RC"; exit 18; fi
test -s "$APK" && test -s "$BASE/build-report.json" || {
  say AARIS_V8_UNITY_OUTPUT_MISSING; exit 19;
}
python3 - "$BASE/build-report.json" <<'PY'
import json,sys
j=json.load(open(sys.argv[1]))
assert j["status"]=="passed" and j["applicationId"]=="com.aaris.game"
assert j["applicationVersion"]=="0.3.2" and j["applicationVersionCode"]==20261112
assert j["sceneCount"]==1 and j["terrainTiles"]==4
PY
say AARIS_V8_UNITY_BUILD_SUCCEEDED
AAPT="$SDK/SDK/build-tools/36.0.0/aapt"
SIGN="$SDK/SDK/build-tools/36.0.0/apksigner"
ALIGN="$SDK/SDK/build-tools/36.0.0/zipalign"
test -x "$AAPT" && test -x "$SIGN" && test -x "$ALIGN" || exit 20
"$AAPT" dump badging "$APK" >"$BASE/private-v8-badging.log"
grep -Eq "^package: name='com[.]aaris[.]game' versionCode='20261112' versionName='0.3.2'" \
 "$BASE/private-v8-badging.log" || { say AARIS_V8_PACKAGE_VERSION_INVALID; exit 21; }
OUT="$BASE/Build/AarisGame-v8-signed.apk"
test ! -e "$OUT" || { say AARIS_V8_SIGNED_OUTPUT_ALREADY_EXISTS; exit 22; }
say AARIS_V8_SIGNING_STARTED
"$SIGN" sign --ks "$HOME/.android/debug.keystore" --ks-key-alias androiddebugkey \
 --ks-pass pass:android --key-pass pass:android \
 --v1-signing-enabled true --v2-signing-enabled true --v3-signing-enabled true \
 --out "$OUT" "$APK" >"$BASE/private-v8-sign.log" 2>&1 || {
 say AARIS_V8_SIGNING_FAILED; exit 23;
}
"$SIGN" verify --verbose --print-certs "$OUT" >"$BASE/private-v8-verify.log" 2>&1 || {
 say AARIS_V8_SIGNATURE_VERIFICATION_FAILED; exit 24;
}
for scheme in v2 v3; do
 grep -q "Verified using $scheme scheme.*: true" "$BASE/private-v8-verify.log" || {
 say AARIS_V8_SIGNING_SCHEME_MISSING; exit 25;
 }
done
# Android SDK verifies v1 only if the requested platform window includes
# pre-Android-7 devices; this is a verifier display rule, not a missing v1.
"$SIGN" verify --min-sdk-version 21 --verbose "$OUT" > "$BASE/private-v8-verify-v1.log" 2>&1 || {
 say AARIS_V8_V1_VERIFICATION_FAILED; exit 25;
}
grep -q 'Verified using v1 scheme.*: true' "$BASE/private-v8-verify-v1.log" || {
 say AARIS_V8_V1_SIGNING_MISSING; exit 25;
}
NEWCERT="$(sed -n 's/^Signer #1 certificate SHA-256 digest: //p' \
 "$BASE/private-v8-verify.log" | head -1 | tr '[:upper:]' '[:lower:]')"
test "$NEWCERT" = "$CERT" || { say AARIS_V8_CERT_MISMATCH; exit 26; }
"$ALIGN" -c -p 4 "$OUT" >/dev/null 2>&1 || {
 say AARIS_V8_ALIGNMENT_FAILED; exit 27;
}
"$AAPT" dump badging "$OUT" >"$BASE/private-v8-signed-badging.log"
python3 - "$BASE" <<'PY'
from zipfile import ZipFile
from pathlib import Path
import hashlib,json,re,sys
root=Path(sys.argv[1])
apk=root/"Build/AarisGame-v8-signed.apk"
t=(root/"private-v8-signed-badging.log").read_text()
assert re.search(r"^package: name='com[.]aaris[.]game' versionCode='20261112' versionName='0.3.2'",t,re.M)
assert re.search(r"^sdkVersion:'26'$",t,re.M)
assert re.search(r"^targetSdkVersion:'33'$",t,re.M)
with ZipFile(apk) as z:
 names=set(z.namelist())
 assert len(names)==len(z.infolist()) and z.testzip() is None
 assert "AndroidManifest.xml" in names and "classes.dex" in names
 assert any(n.startswith("lib/arm64-v8a/") and n.endswith(".so") for n in names)
 assert any(n.startswith("lib/armeabi-v7a/") and n.endswith(".so") for n in names)
h=hashlib.sha256()
with apk.open("rb") as f:
 for b in iter(lambda:f.read(1024*1024),b""): h.update(b)
sha=h.hexdigest()
(root/"Build/AarisGame-v8.sha256").write_text(sha+"  AarisGame-v8-signed.apk\n")
(root/"installfix-v8-report.json").write_text(json.dumps({
 "status":"verified_locally_device_install_not_confirmed",
 "package":"com.aaris.game","versionCode":20261112,
 "versionName":"0.3.2","minSdk":26,"targetSdk":33,
 "signing":"v1+v2+v3","certificateSha256":"c469f27b142a0d85c766700d05bbfe7c3fd55b888ffa9e36a5a72cda65aa3eb1",
 "bytes":apk.stat().st_size,"sha256":sha},indent=2))
print("AARIS_V8_LOCAL_APK_INTEGRITY_PASS")
PY
say AARIS_V8_APK_MULTISCHEME_SIGNATURE_MANIFEST_SHA256_VERIFIED
say AARIS_V8_READY_FOR_REAL_DEVICE_INSTALL_TEST
