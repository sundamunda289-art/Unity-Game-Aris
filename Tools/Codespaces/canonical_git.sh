#!/usr/bin/env bash
# Aaris canonical Git bridge: reusable in ANY authorized Codespace account.
# No worktree/checkout/reset/stash/clean, no forced pushes and no bulk auto-stage.
set -euo pipefail
umask 077

TARGET="https://github.com/sundamunda289-art/Unity-Game-Aris.git"
REMOTE="aaris-master"
PROJECT="$PWD"
if [[ -d /workspaces/Unity-Game-Aris/.git ]]; then
  PROJECT="/workspaces/Unity-Game-Aris"
fi
ROOT="$(git -C "$PROJECT" rev-parse --show-toplevel 2>/dev/null)" || {
  echo "AARIS_SYNC_NOT_A_GIT_CHECKOUT"; exit 20;
}
cd "$ROOT"
if [[ ! -f ProjectSettings/ProjectVersion.txt || ! -d Assets ]]; then
  echo "AARIS_SYNC_NOT_UNITY_GAME_PROJECT"; exit 21
fi
if git remote get-url "$REMOTE" >/dev/null 2>&1; then
  [[ "$(git remote get-url "$REMOTE")" == "$TARGET" ]] || {
    echo "AARIS_SYNC_CANONICAL_REMOTE_MISMATCH"; exit 22;
  }
else
  git remote add "$REMOTE" "$TARGET"
fi

export GIT_TERMINAL_PROMPT=0 GIT_LFS_SKIP_SMUDGE=1
TOKEN_FILE="$HOME/.config/aaris-ai/canonical-push.token"
ASKPASS="$HOME/.config/aaris-ai/canonical-git-askpass.sh"

set_push_auth() {
  if [[ ! -f "$TOKEN_FILE" || -L "$TOKEN_FILE" || ! -s "$TOKEN_FILE" ||
        ! -x "$ASKPASS" || -L "$ASKPASS" ]]; then
    echo "AARIS_SYNC_PUSH_AUTH_MISSING: run the existing Termux account switch/bridge setup"
    exit 23
  fi
  local owner mode
  owner="$(stat -c %u "$TOKEN_FILE")"
  mode="$(stat -c %a "$TOKEN_FILE")"
  if [[ "$owner" != "$(id -u)" || "$mode" != 600 ]]; then
    echo "AARIS_SYNC_UNSAFE_PRIVATE_CREDENTIAL"; exit 24
  fi
  export GIT_ASKPASS="$ASKPASS"
}

fetch_main() {
  # Public fetch works even without push credentials. Never hydrate LFS here.
  git -c credential.helper= fetch --no-tags "$REMOTE" main
  git update-ref refs/remotes/aaris-master/main "$(git rev-parse FETCH_HEAD)"
  echo "AARIS_SYNC_CANONICAL_MAIN_FETCHED=$(git rev-parse --short FETCH_HEAD)"
}

current_branch() {
  git symbolic-ref --quiet --short HEAD || {
    echo "AARIS_SYNC_DETACHED_HEAD_REFUSED"; exit 25;
  }
}

check_staged_empty() {
  if ! git diff --cached --quiet; then
    echo "AARIS_SYNC_EXISTING_STAGED_CHANGES: another agent may be working; refusing to mix commits"
    exit 26
  fi
}

mode="status"
if (( $# > 0 )); then mode="$1"; shift; fi

case "$mode" in
  status)
    echo "AARIS_SYNC_PROJECT=$ROOT"
    echo "AARIS_SYNC_BRANCH=$(current_branch)"
    echo "AARIS_SYNC_HEAD=$(git rev-parse --short HEAD)"
    echo "AARIS_SYNC_CANONICAL_REMOTE=$TARGET"
    echo "AARIS_SYNC_AUTH=$([[ -s "$TOKEN_FILE" ]] && echo installed || echo not_installed)"
    echo "AARIS_SYNC_LFS=$(git lfs version 2>/dev/null | head -1 || echo unavailable)"
    git status --short --untracked-files=no | head -40 || :
    ;;
  fetch)
    fetch_main
    ;;
  pull)
    branch="$(current_branch)"
    fetch_main
    if ! git diff --quiet || ! git diff --cached --quiet; then
      echo "AARIS_SYNC_DIRTY_TRACKED_WORK: fetched only; no merge to protect other agents"
      exit 27
    fi
    if git merge --ff-only refs/remotes/aaris-master/main; then
      echo "AARIS_SYNC_FAST_FORWARD_OK"
    else
      echo "AARIS_SYNC_DIVERGED: no reset/rebase/force merge; review conflicts or use a PR"
      exit 28
    fi
    ;;
  publish)
    branch="$(current_branch)"
    machine="$(hostname)"
    if [[ -n "${CODESPACE_NAME:-}" ]]; then machine="$CODESPACE_NAME"; fi
    machine="$(printf '%s' "$machine" | sed 's/[^a-zA-Z0-9._-]/-/g')"
    branch_safe="$(printf '%s' "$branch" | sed 's/[^a-zA-Z0-9._-]/-/g')"
    [[ -n "$machine" && -n "$branch_safe" ]] || exit 29
    set_push_auth
    echo "AARIS_SYNC_PUBLISHING_COMMITTED_HEAD_ONLY"
    ref="refs/heads/codespace-sync/$machine/$branch_safe"
    git -c credential.helper= push "$REMOTE" "HEAD:$ref"
    echo "AARIS_SYNC_PUBLISHED_BRANCH=$ref"
    echo "AARIS_SYNC_PR_LINK=https://github.com/sundamunda289-art/Unity-Game-Aris/compare/main...codespace-sync/$machine/$branch_safe?expand=1"
    ;;
  push-main)
    branch="$(current_branch)"
    [[ "$branch" == main ]] || {
      echo "AARIS_SYNC_NOT_MAIN_BRANCH: use publish then review PR instead"; exit 30;
    }
    if ! git diff --quiet || ! git diff --cached --quiet; then
      echo "AARIS_SYNC_UNCOMMITTED_WORK: refusing push-main"; exit 31
    fi
    fetch_main
    if ! git merge-base --is-ancestor refs/remotes/aaris-master/main HEAD; then
      echo "AARIS_SYNC_MAIN_DIVERGED: refusing non-fast-forward push"; exit 32
    fi
    set_push_auth
    git -c credential.helper= push "$REMOTE" HEAD:refs/heads/main
    echo "AARIS_SYNC_MAIN_FAST_FORWARD_PUSH_OK"
    ;;
  commit)
    (( $# >= 2 )) || {
      echo 'Usage: canonical_git.sh commit "message" path/to/file [path/to/another-file]'
      exit 33
    }
    message="$1"
    shift
    check_staged_empty
    for file in "$@"; do
      case "$file" in
        /*|.|..|../*|*/../*|*/..|Library|Library/*|Logs|Logs/*|Temp|Temp/*|obj|obj/*|.git|.git/*|*.secret|*.token|*.ulf|*.env)
          echo "AARIS_SYNC_UNSAFE_COMMIT_PATH"; exit 34 ;;
      esac
    done
    git add -- "$@"
    if git diff --cached --quiet; then
      echo "AARIS_SYNC_NO_SELECTED_CHANGES"
    else
      git diff --cached --check || { echo "AARIS_SYNC_DIFF_CHECK_FAILED"; exit 35; }
      git commit -m "$message"
      echo "AARIS_SYNC_SELECTED_FILES_COMMITTED"
    fi
    ;;
  *)
    echo 'Usage: canonical_git.sh {status|fetch|pull|commit "message" path...|publish|push-main}'
    exit 2
    ;;
esac
