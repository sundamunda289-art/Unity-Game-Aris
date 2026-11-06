#!/usr/bin/env bash
# Termux-only one-time secure download + GitHub Secret setup for a real Unity ULF.
# GitHub Actions exports ONLY an age-encrypted artifact, never raw license XML.
set -euo pipefail
REPO="sundamunda289-art/Unity-Game-Aris"
WORKFLOW="aaris-live-unity.yml"
# Review branch until PR #79 is merged; afterwards set AARIS_LICENSE_REF=main.
REF="${AARIS_LICENSE_REF:-ai/scene-operator-v1}"
ARTIFACT="aaris-unity-license-encrypted"
KEY_DIR="$HOME/.config/aaris-license"
KEY="$KEY_DIR/identity.txt"
PRIVATE_ULF="$KEY_DIR/Unity_lic.ulf"
echo "Aaris secure Unity license export via GitHub Actions"
command -v pkg >/dev/null || { echo "Run this in the Termux app." >&2; exit 2; }
pkg install -y gh age jq python
if ! gh auth status >/dev/null 2>&1; then
  gh auth login --web
fi
mkdir -p "$KEY_DIR"
chmod 700 "$KEY_DIR"
if [[ ! -s "$KEY" ]]; then
  umask 077
  age-keygen -o "$KEY"
fi
chmod 600 "$KEY"
PUBLIC="$(age-keygen -y "$KEY")"
case "$PUBLIC" in age1*) ;; *) echo "Invalid age public key" >&2; exit 2;; esac

echo "Preparing a phone-accessible Unity Hub cloud session with your PUBLIC encryption key."
echo "Workflow ref: $REF (use AARIS_LICENSE_REF=main after reviewed merge)."
BEFORE="$(gh run list --workflow "$WORKFLOW" --event workflow_dispatch -R "$REPO" \
  --limit 1 --json databaseId --jq '.[0].databaseId // 0' 2>/dev/null || printf '0')"
gh workflow run "$WORKFLOW" -R "$REPO" --ref "$REF" \
  -f session_minutes=45 -f license_export_recipient="$PUBLIC"

RUN=""
for i in $(seq 1 45); do
  RUN="$(gh run list --workflow "$WORKFLOW" --event workflow_dispatch -R "$REPO" \
    --limit 1 --json databaseId --jq '.[0].databaseId // 0' 2>/dev/null || printf '0')"
  if [[ "$RUN" =~ ^[0-9]+$ && "$RUN" != "0" && "$RUN" != "$BEFORE" ]]; then break; fi
  RUN=""
  sleep 3
done
if [[ -z "$RUN" ]]; then
  echo "Could not identify the newly dispatched run. Check GitHub Actions."
  exit 1
fi

echo "Action run: https://github.com/$REPO/actions/runs/$RUN"
echo "If Unity requests a genuine sign-in, approve it on your phone:"
echo "https://aaris-unity-live-hub.aaris-game-control-6f9c.workers.dev"
echo "Watching run status. The encrypted file is never shown in public logs."

# Watch can return a non-zero status when Unity's entitlement is unavailable.
gh run watch "$RUN" -R "$REPO" --exit-status || true

TEMP="$KEY_DIR/download-$RUN"
mkdir -p "$TEMP"
if ! gh run download "$RUN" -R "$REPO" -n "$ARTIFACT" -D "$TEMP"; then
  echo
  echo "No encrypted ULF artifact was produced. Unity did not issue a"
  echo "reusable ULF on this runner. A Google/Unity sign-in alone is not proof."
  echo "Check Unity status in the GitHub Actions run before trying again."
  exit 1
fi

test -s "$TEMP/Unity_lic.ulf.age" || {
  echo "Encrypted artifact is missing or empty." >&2
  exit 1
}
umask 077
if ! age --decrypt -i "$KEY" -o "$PRIVATE_ULF" "$TEMP/Unity_lic.ulf.age"; then
  echo "Could not decrypt the Unity license file." >&2
  rm -f "$PRIVATE_ULF"
  exit 1
fi
chmod 600 "$PRIVATE_ULF"
trap 'rm -f "$PRIVATE_ULF"' EXIT

if ! python3 - "$PRIVATE_ULF" <<'PY'
import pathlib, sys, xml.etree.ElementTree as ET
data=pathlib.Path(sys.argv[1]).read_bytes()
if not 200<=len(data)<=180000:
    raise SystemExit("Invalid ULF size")
try:
    elem=ET.fromstring(data)
except ET.ParseError:
    raise SystemExit("Not a valid ULF XML document")
tag=elem.tag.lower()
if "license" not in tag and "root" not in tag:
    raise SystemExit("Unexpected ULF XML root")
print("ULF XML format verified (license content hidden).")
PY
then
  echo "Unrecognized license format. GitHub Secret NOT modified." >&2
  rm -f "$PRIVATE_ULF"
  exit 1
fi

gh secret set UNITY_LICENSE -R "$REPO" < "$PRIVATE_ULF"
# Never copy a license into Android shared Downloads or a GitHub public Release.
# The plaintext temporary file is removed by the EXIT trap.
rm -f "$TEMP/Unity_lic.ulf.age"
rmdir "$TEMP" 2>/dev/null || true
echo "Authorized ULF stored in private GitHub UNITY_LICENSE Secret; no plaintext Downloads copy."
echo "Check: gh secret list -R $REPO"
echo "A newly started runner must STILL validate this license; ULF portability is not guaranteed."
