#!/usr/bin/env bash
# Repeatable, non-destructive combat acceptance gate for the existing Unity 6 project.
# --quick: source-contract regressions. --full: native C# + PhysX + saved-scene Play Mode.
# All diagnostic output stays in /tmp. Never creates a worktree, commits, or builds an APK.
set -Eeuo pipefail
umask 077

ROOT=/workspaces/Unity-Game-Aris
MODE="$*"
[[ -n "$MODE" ]] || MODE=--quick
case "$MODE" in
  --quick|--full) ;;
  *) printf 'USAGE: bash %s [--quick|--full]\n' "$0" >&2; exit 2 ;;
esac
if [[ ! -d "$ROOT/.git" ]]; then
  echo COMBAT_GATE_WRONG_PROJECT >&2
  exit 10
fi
cd "$ROOT"
LOG_DIR="$(mktemp -d /tmp/aaris-combat-acceptance.XXXXXXXX)"
BEFORE="$(git status --porcelain=v1 --untracked-files=all | sha256sum | awk '{print $1}')"
SOURCE_SHA="$(git rev-parse HEAD)"
on_exit() {
  local rc=$?
  trap - EXIT
  local after
  after="$(git status --porcelain=v1 --untracked-files=all | sha256sum | awk '{print $1}')" || rc=65
  if [[ "$BEFORE" != "$after" ]]; then
    echo COMBAT_GATE_WORKTREE_CHANGED_REVIEW_REQUIRED
    rc=66
  else
    echo COMBAT_GATE_WORKTREE_UNCHANGED=yes
  fi
  printf 'COMBAT_GATE_RESULT=%s EXIT=%s\n' "$([[ $rc -eq 0 ]] && echo PASS || echo FAIL)" "$rc"
  exit "$rc"
}
trap on_exit EXIT
printf 'COMBAT_GATE_MODE=%s\nCOMBAT_GATE_SOURCE_SHA=%s\nCOMBAT_GATE_LOG_DIR=%s\n' \
  "$MODE" "$SOURCE_SHA" "$LOG_DIR"

run_checked() {
  local stage="$1"
  shift
  echo "COMBAT_GATE_START=$stage"
  if "$@" >"$LOG_DIR/$stage.log" 2>&1; then
    echo "COMBAT_GATE_STAGE_$stage=PASS"
  else
    local rc=$?
    echo "COMBAT_GATE_STAGE_$stage=FAIL EXIT=$rc"
    tail -n 35 "$LOG_DIR/$stage.log"
    return "$rc"
  fi
}

run_checked python python3 -m unittest discover -s Tools/Validation -p 'test_combat*.py'
grep -E '^Ran [0-9]+ tests|^OK$' "$LOG_DIR/python.log" || true
[[ "$MODE" == --full ]] || exit 0

EDITOR="$HOME/.local/share/aaris-unity-editors/6000.3.26f1/Editor/Unity"
if [[ ! -x "$EDITOR" ]] || ! command -v xvfb-run >/dev/null; then
  echo COMBAT_GATE_UNITY_OR_XVFB_MISSING
  exit 20
fi
if pgrep -f '[E]ditor/Unity.*-projectPath' >/dev/null 2>&1; then
  echo COMBAT_GATE_EDITOR_BUSY_NO_PARALLEL_IMPORT
  exit 21
fi
run_checked csharp bash Tools/Codespaces/native_source_compile_sandbox.sh
grep -qx 'AARIS_NATIVE_COMPILE_CSHARP_ERRORS=0' "$LOG_DIR/csharp.log"
grep -qx 'AARIS_NATIVE_COMPILE_RESULT=source_only_pass' "$LOG_DIR/csharp.log"

PHYSICS_REPORT=/tmp/aaris-bot-combat-qa/report.txt
physical_started="$(date +%s)"
run_checked physics timeout --kill-after=12s 260s xvfb-run -a -s '-screen 0 1280x720x24' \
  "$EDITOR" -batchmode -projectPath "$ROOT" \
  -executeMethod Aaris.MapReview.Editor.AarisBotCombatIntegrationQA.Run \
  -logFile "$LOG_DIR/physics.unity.log"
[[ -f "$PHYSICS_REPORT" ]] && [[ "$(stat -c %Y "$PHYSICS_REPORT")" -ge "$physical_started" ]]
grep -qx 'RESULT=PASS' "$PHYSICS_REPORT"
grep -Fqx 'PASS 100 rivals allow exactly two player attackers' "$PHYSICS_REPORT"
grep -Fqx 'PASS Player-to-bot and bot-to-player real hit events' "$PHYSICS_REPORT"
grep -Fqx 'PASS Red-Lock turns off behind solid wall' "$PHYSICS_REPORT"

TERRAIN_REPORT=/tmp/aaris-full-scene-combat-soak/result.txt
terrain_started="$(date +%s)"
run_checked terrain env AARIS_FULLSCENE_SOAK_SECONDS=15 \
  timeout --kill-after=12s 350s xvfb-run -a -s '-screen 0 1280x720x24' \
  "$EDITOR" -batchmode -projectPath "$ROOT" \
  -executeMethod Aaris.MapReview.Editor.AarisFullSceneCombatSoak.Run \
  -logFile "$LOG_DIR/terrain.unity.log"
[[ -f "$TERRAIN_REPORT" ]] && [[ "$(stat -c %Y "$TERRAIN_REPORT")" -ge "$terrain_started" ]]
grep -qx 'RESULT=PASS' "$TERRAIN_REPORT"
grep -q '^SPAWNED_REAL_SCENE=100/100 ' "$TERRAIN_REPORT"
grep -qx 'SCOPE_TIERS_VERIFIED=4/4' "$TERRAIN_REPORT"
grep -Fqx 'PASS native player HP 200 text and full-width bar' "$TERRAIN_REPORT"
grep -Fqx 'PASS real AssaultRifle no red-lock and no HP damage through wall' "$TERRAIN_REPORT"
grep -Fqx 'PASS native HP 175 / 200, 87.5% bar, hit flash and HIT FRONT' "$TERRAIN_REPORT"
grep -q '^PASS sustained 100-bot combat' "$TERRAIN_REPORT"
echo COMBAT_GATE_SCENE_BOTS=100/100
echo COMBAT_GATE_SCOPES=0,1,2,4
echo COMBAT_GATE_NATIVE_DAMAGE=bidirectional
echo COMBAT_GATE_ANDROID_FPS=not_measured
