#!/usr/bin/env bash
# Real Unity 6000.3 PlayMode acceptance on a disposable full-asset copy.
# Never changes /workspaces/Unity-Game-Aris or creates a Git worktree.
set -Eeuo pipefail
umask 077
ROOT=/workspaces/Unity-Game-Aris
EXPECTED="${1:-}"
[[ "$EXPECTED" =~ ^[a-f0-9]{40}$ ]] || { echo AARIS_ULTRA_QA_INVALID_SHA; exit 2; }
[[ -d "$ROOT/.git" ]] || { echo AARIS_ULTRA_QA_SOURCE_MISSING; exit 3; }
ACTUAL="$(git -C "$ROOT" rev-parse HEAD)"
[[ "$ACTUAL" == "$EXPECTED" ]] || {
  echo "AARIS_ULTRA_QA_SOURCE_MISMATCH expected=$EXPECTED actual=$ACTUAL"
  exit 4
}
if pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null 2>&1; then
  echo AARIS_ULTRA_QA_EDITOR_BUSY_NO_SECOND_IMPORT
  exit 5
fi
EDITOR="$HOME/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity"
[[ -x "$EDITOR" ]] || { echo AARIS_ULTRA_QA_EDITOR_UNAVAILABLE; exit 6; }
command -v xvfb-run >/dev/null || { echo AARIS_ULTRA_QA_XVFB_UNAVAILABLE; exit 7; }

BEFORE="$(git -C "$ROOT" status --porcelain=v1 --untracked-files=all | sha256sum | awk '{print $1}')"
STAGE="$(mktemp -d /tmp/aaris-ultra-gunplay.XXXXXXXX)"
[[ "$STAGE" == /tmp/aaris-ultra-gunplay.* ]] || exit 8
cleanup() {
  local code=$?
  trap - EXIT
  local after
  after="$(git -C "$ROOT" status --porcelain=v1 --untracked-files=all | sha256sum | awk '{print $1}')" || code=90
  if [[ "$BEFORE" == "$after" ]]; then
    echo AARIS_ULTRA_QA_ORIGINAL_WORKTREE_UNCHANGED=yes
  else
    echo AARIS_ULTRA_QA_ORIGINAL_WORKTREE_UNCHANGED=no
    code=91
  fi
  rm -rf -- "$STAGE"
  printf 'AARIS_ULTRA_QA_RESULT=%s\n' "$([[ "$code" -eq 0 ]] && echo PASS || echo FAIL)"
  exit "$code"
}
trap cleanup EXIT

# Full copy retains hydrated local Git LFS meshes and real authored scenes.
# No hardlinks, no new worktree and no Library copy.
needed_kb="$(du -sk "$ROOT/Assets" "$ROOT/Packages" "$ROOT/ProjectSettings" |
  awk '{total += $1} END {print total + 0}')"
available_kb="$(df -Pk /tmp | awk 'NR==2 {print $4}')"
if (( available_kb < needed_kb + 8*1024*1024 )); then
  echo AARIS_ULTRA_QA_TMP_CAPACITY_INSUFFICIENT
  exit 9
fi
cp -a --reflink=auto "$ROOT/Assets" "$ROOT/Packages" "$ROOT/ProjectSettings" "$STAGE/"
printf 'AARIS_ULTRA_QA_COMMIT=%s\n' "$ACTUAL"
echo AARIS_ULTRA_QA_PROJECT_MODE=disposable_full_asset_copy
echo AARIS_ULTRA_QA_UNITY_VERSION=6000.3.26f1
echo AARIS_ULTRA_QA_DEVICE_FPS=not_measured

# On an Editor exit code or failed report, retain a bounded, sanitized
# diagnostic in the GitHub Actions summary BEFORE /tmp is removed. Otherwise
# a PlayMode assertion failure looks identical to an Editor crash, and the
# disposable full-asset copy's logs vanish without a diagnosis.
gate_diagnostics() {
  local name="$1" report="$2" counter=0 line safe
  if [[ -f "$report" ]]; then
    while IFS= read -r line; do
      safe="$(printf '%s' "$line" | tr -cs 'A-Za-z0-9_./:-' '_' | cut -c1-160)"
      [[ -n "$safe" ]] || continue
      printf 'AARIS_ULTRA_QA_DIAG_%s_%02d=%s\n' "$name" "$counter" "$safe"
      counter=$((counter + 1))
    done < <(grep -E '^(FAIL|RESULT=|ERROR=|EXCEPTION=|[[:space:]]*at )' "$report" | tail -n 8 || true)
  fi
  if [[ -f "$STAGE/$name.unity.log" ]]; then
    while IFS= read -r line; do
      safe="$(printf '%s' "$line" | tr -cs 'A-Za-z0-9_./:-' '_' | cut -c1-160)"
      [[ -n "$safe" ]] || continue
      printf 'AARIS_ULTRA_QA_DIAG_%s_%02d=%s\n' "$name" "$counter" "$safe"
      counter=$((counter + 1))
    done < <(grep -Ei 'error CS[0-9]+|Exception:|Assertion|AarisProGunplayQA|RESULT=FAIL|FAIL |Compilation failed|ExecuteMethod' "$STAGE/$name.unity.log" | tail -n 10 || true)
  fi
  [[ "$counter" -gt 0 ]] || printf 'AARIS_ULTRA_QA_DIAG_%s_00=no_diagnostic_in_editor_log\n' "$name"
}

run_gate() {
  local name="$1" entry="$2" report="$3" limit="$4"
  local started
  started="$(date +%s)"
  printf 'AARIS_ULTRA_QA_STAGE_%s=running\n' "$name"
  if ! timeout --kill-after=20s "${limit}s" xvfb-run -a \
      -s '-screen 0 1280x720x24' "$EDITOR" -batchmode \
      -projectPath "$STAGE" -executeMethod "$entry" \
      -logFile "$STAGE/$name.unity.log" \
      > "$STAGE/$name.stdout" 2>&1; then
    printf 'AARIS_ULTRA_QA_STAGE_%s=editor_failed\n' "$name"
    gate_diagnostics "$name" "$report"
    exit 20
  fi
  if [[ ! -s "$report" ]] ||
     [[ "$(stat -c %Y "$report")" -lt "$started" ]] ||
     ! grep -qx 'RESULT=PASS' "$report"; then
    printf 'AARIS_ULTRA_QA_STAGE_%s=report_failed_or_stale\n' "$name"
    gate_diagnostics "$name" "$report"
    exit 21
  fi
  printf 'AARIS_ULTRA_QA_STAGE_%s=PASS\n' "$name"
  printf 'AARIS_ULTRA_QA_STAGE_%s_PASS_ASSERTIONS=%s\n' \
    "$name" "$(grep -c '^PASS ' "$report" || true)"
}

# 1. Actual PlayMode: firing, loot pickup, hit feedback, stances.
run_gate gunplay Aaris.MapReview.Editor.AarisProGunplayQA.Run \
  /tmp/aaris-progunplay-qa/report.txt 520
# 2. Actual EditMode PhysX: damage, cover, 100 logical rivals.
run_gate bot_physics Aaris.MapReview.Editor.AarisBotCombatIntegrationQA.Run \
  /tmp/aaris-bot-combat-qa/report.txt 400
# 3. Saved 2 km original terrain scene with 100 spawned rivals.
AARIS_FULLSCENE_SOAK_SECONDS=15
export AARIS_FULLSCENE_SOAK_SECONDS
run_gate full_scene Aaris.MapReview.Editor.AarisFullSceneCombatSoak.Run \
  /tmp/aaris-full-scene-combat-soak/result.txt 520
grep -q '^SPAWNED_REAL_SCENE=100/100 ' \
  /tmp/aaris-full-scene-combat-soak/result.txt || {
    echo AARIS_ULTRA_QA_100_BOT_SPAWN_FAILED; exit 22;
  }
grep -q '^PASS sustained 100-bot combat' \
  /tmp/aaris-full-scene-combat-soak/result.txt || {
    echo AARIS_ULTRA_QA_100_BOT_COMBAT_FAILED; exit 23;
  }
echo AARIS_ULTRA_QA_100_BOTS=PASS
echo AARIS_ULTRA_QA_NO_RESET_NO_CLEAN_NO_STASH_NO_WORKTREE=yes
