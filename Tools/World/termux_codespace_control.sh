#!/usr/bin/env bash
# Phone-only, no-SSH Codespace discovery and lifecycle configuration.
# Does not rebuild the container, install Unity or modify saved map assets.
set -Eeuo pipefail

REPO="Aaris-khan-don/Unity-Game-Aris"
OWNER="Aaris-khan-don"

if ! command -v gh >/dev/null 2>&1 || ! command -v jq >/dev/null 2>&1; then
  if ! command -v pkg >/dev/null 2>&1; then
    echo "Run this script in Termux (pkg must be available)." >&2
    exit 1
  fi
  pkg install -y gh jq
fi

if ! gh auth status -h github.com >/dev/null 2>&1; then
  echo "Sign in to your GitHub account through the official device login."
  gh auth login -h github.com -p https -w
fi

# Codespaces REST requires 'codespace' OAuth scope for classic OAuth credentials.
gh auth refresh -h github.com -s codespace

login="$(gh api user --jq .login)"
if [[ "${login,,}" != "${OWNER,,}" ]]; then
  echo "Signed into $login, but this setup requires owner $OWNER." >&2
  echo "Use: gh auth switch -h github.com (or gh auth login with the owner account)." >&2
  exit 2
fi

echo "Inspecting Codespaces owned by $login, without SSH..."
rows="$(gh api --paginate /user/codespaces --jq '.codespaces[] | [.name, .repository.full_name, .state] | @tsv')"
if [[ -n "$rows" ]]; then
  printf 'NAME\tREPO\tSTATE\n%s\n' "$rows"
else
  echo "No Codespaces found on this account." >&2
  exit 3
fi

mapfile -t matches < <(printf '%s\n' "$rows" | awk -F '\t' -v target="$REPO" '$2 == target {print $1}')
if (( ${#matches[@]} == 0 )); then
  echo "No Codespace for $REPO on this account." >&2
  echo "Not creating or rebuilding one: the Unity installation may belong to another account/Codespace." >&2
  exit 4
fi

if (( ${#matches[@]} == 1 )); then
  selected="${matches[0]}"
else
  echo "Choose the existing Codespace for this project:"
  select selected in "${matches[@]}"; do
    if [[ -n "${selected:-}" ]]; then break; fi
    echo "Choose a number from the list."
  done
fi

echo "Selected: $selected"
gh variable set AARIS_CODESPACE_NAME --repo "$REPO" --body "$selected"
echo "Repository variable AARIS_CODESPACE_NAME configured."
echo "You can manually start this Codespace with:"
echo "  gh api --method POST /user/codespaces/$selected/start"
echo "or inspect it with:"
echo "  gh api /user/codespaces/$selected --jq '{name,state,repository:.repository.full_name}'"

if gh secret list --repo "$REPO" --json name --jq '.[].name' 2>/dev/null | grep -Fxq AARIS_CODESPACE_LIFECYCLE_TOKEN; then
  echo "Lifecycle secret already exists (its permissions/content cannot be validated here)."
else
  echo
  echo "For automatic GitHub Actions wake-up, you must configure a *separate*,"
  echo "fine-grained personal access token restricted to $REPO, with"
  echo "'Codespaces lifecycle admin: Write'. Never put this token in a committed file."
  echo "Create it through GitHub Settings > Developer settings > Personal access tokens."
  read -r -p "Have you already created this fine-grained token? [y/N] " choice
  if [[ "${choice,,}" == "y" ]]; then
    read -r -s -p "Paste lifecycle-only token (hidden): " lifecycle_token
    echo
    if [[ -z "$lifecycle_token" ]]; then
      echo "No token entered; automatic wake remains unconfigured."
    else
      printf '%s' "$lifecycle_token" | gh secret set AARIS_CODESPACE_LIFECYCLE_TOKEN --repo "$REPO"
      unset lifecycle_token
      echo "Lifecycle secret saved to Actions secrets."
    fi
  else
    echo "Automatic wake remains disabled until the lifecycle secret is set."
  fi
fi

echo
echo "IMPORTANT: Start/stop API access is NOT remote terminal access."
echo "Existing Codespaces need a one-time worker bootstrap. Do NOT rebuild"
echo "the container before preserving any Unity install outside /workspaces."
echo "Phase 1 complete: identity, Codespace and lifecycle settings inspected."
