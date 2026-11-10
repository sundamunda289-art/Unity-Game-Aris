#!/usr/bin/env bash
# Compile ONLY the fixed Git review branch on the same licensed Codespace.
# Source /workspaces, saved scenes and dirty original assets remain read-only.
set -Eeuo pipefail
SOURCE=/workspaces/Unity-Game-Aris
BRANCH=refs/heads/review/offline-mobile-audio-20261109
# The native 793-LFS verifier strictly requires this isolated worktree root.
# A review-branch SHA gets its own directory, separate from main's photo snapshot.
ROOT=/tmp/aaris-native-photo-worktrees
EDITOR="$HOME/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity"
test -x "$EDITOR" || { echo AARIS_AUDIO_EDITOR_NOT_INSTALLED; exit 11; }
test -d "$SOURCE/.git" || { echo AARIS_AUDIO_SOURCE_MISSING; exit 12; }
if pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null 2>&1; then
  echo AARIS_AUDIO_EDITOR_ALREADY_RUNNING
  exit 13
fi
git -C "$SOURCE" fetch --quiet origin "$BRANCH" >/dev/null 2>&1 || {
  echo AARIS_AUDIO_BRANCH_FETCH_FAILED; exit 14;
}
REV="$(git -C "$SOURCE" rev-parse --verify 'FETCH_HEAD^{commit}')" || exit 14
[[ "$REV" =~ ^[0-9a-f]{40}$ ]] || exit 14
PROJECT="$ROOT/$REV"
mkdir -p "$ROOT"
if test ! -d "$PROJECT"; then
  if test ! -e "$PROJECT" &&
     git -C "$SOURCE" worktree list --porcelain |
       grep -Fx -- "worktree $PROJECT" >/dev/null; then
    git -C "$SOURCE" worktree remove --force "$PROJECT" >/dev/null 2>&1 || {
      echo AARIS_AUDIO_STALE_WORKTREE_REPAIR_FAILED; exit 15;
    }
  fi
  GIT_LFS_SKIP_SMUDGE=1 git -C "$SOURCE" \
    -c filter.lfs.process= -c filter.lfs.smudge=cat -c filter.lfs.required=false \
    worktree add --detach "$PROJECT" "$REV" >/dev/null 2>&1 || {
      echo AARIS_AUDIO_WORKTREE_CREATE_FAILED; exit 15;
    }
fi
test "$(git -C "$PROJECT" rev-parse HEAD)" = "$REV" || exit 16
cd "$PROJECT"
mkdir -p "$ROOT/UnityTemp"
if ! python3 Tools/Codespaces/restore_preserved_source_lfs.py "$PROJECT" > "$ROOT/private-lfs.log" 2>&1; then
  echo AARIS_AUDIO_LFS_SOURCE_VERIFY_FAILED
  exit 17
fi
python3 Tools/Audio/generate_sfx.py --check >/dev/null || {
  echo AARIS_AUDIO_SFX_INTEGRITY_FAILED; exit 18;
}
echo AARIS_AUDIO_793_NATIVE_ASSETS_VERIFIED
# Native Editor import/compilation; -nographics is appropriate for this
# headless compile-only check, never for a screenshot or visual certification.
set +e
timeout --signal=TERM 1450 env TMPDIR="$ROOT/UnityTemp" \
  "$EDITOR" -batchmode -quit -nographics -projectPath "$PROJECT" \
  -executeMethod Aaris.MapReview.Editor.AarisOfflineAudioClipProbe.Verify \
  -logFile "$ROOT/private-unity-compile.log" \
  > "$ROOT/private-stdout.log" 2> "$ROOT/private-stderr.log"
rc=$?
set -e
if grep -Eqi 'error CS[0-9]{4}|Scripts have compiler errors|Compilation failed' \
  "$ROOT/private-unity-compile.log" "$ROOT/private-stderr.log" 2>/dev/null; then
  echo AARIS_AUDIO_UNITY_CSHARP_COMPILE_ERRORS
  exit 19
fi
if test "$rc" -ne 0; then
  case "$rc" in
    124|143) echo AARIS_AUDIO_UNITY_IMPORT_TIMEOUT ;;
    134|139) echo AARIS_AUDIO_UNITY_NATIVE_CRASH ;;
    137) echo AARIS_AUDIO_UNITY_OOM_OR_TERMINATED ;;
    *) echo AARIS_AUDIO_UNITY_IMPORT_EXIT_NONZERO ;;
  esac
  exit 20
fi
if ! grep -Eqi 'AssetDatabase|Asset Pipeline|Refresh|script assemblies|Scripts/Assembly|Entering batchmode' \
  "$ROOT/private-unity-compile.log" 2>/dev/null; then
  echo AARIS_AUDIO_COMPILE_NO_IMPORT_EVIDENCE
  exit 21
fi
if ! grep -q 'AARIS_AUDIO_CLIPS_IMPORT_PASS=42' "$ROOT/private-unity-compile.log" 2>/dev/null; then
  echo AARIS_AUDIO_IMPORTED_CLIP_CHECK_NOT_PASSED
  exit 22
fi
echo AARIS_AUDIO_NATIVE_42_CLIPS_IMPORTED
echo AARIS_AUDIO_NATIVE_COMPILE_EXIT_ZERO_NO_CS_ERRORS
echo AARIS_AUDIO_REVIEW_BRANCH_ONLY_UNCHANGED_SOURCE
