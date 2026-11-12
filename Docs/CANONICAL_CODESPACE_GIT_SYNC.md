# Canonical Git bridge for rotating Codespaces

Canonical game source: https://github.com/sundamunda289-art/Unity-Game-Aris (main).
A Codespace can belong to a different GitHub account/fork. Never assume origin
points to the permanent game repository.

## One Termux command for account rotation

The existing Termux switch command automatically dispatches the Cloudflare
Turbo bootstrap and then runs the canonical Git bridge:

    bash "$HOME/aaris-switch.sh"

Reuse the current account:

    bash "$HOME/aaris-switch.sh" --resume

For an older local switch script, refresh it once:

    curl -fsSL https://raw.githubusercontent.com/sundamunda289-art/Unity-Game-Aris/main/Tools/Codespaces/termux_switch.sh -o "$HOME/aaris-switch.sh"

If the Codespace was not Available when the switch finished, retry ONLY the
deploy-key setup (no second GitHub Actions bootstrap):

    bash "$HOME/aaris-git-bridge.sh"

The switch script authenticates the permanent master account and the selected
hosting account using separate GitHub CLI profiles on the phone. The Git
bridge uses Codespaces SSH to install a public repository-authored Git
helper in the selected Codespace and generates a NEW ED25519 SSH private key
locally in that Codespace. It reads back ONLY the public key and registers that
key as a write deploy key on this single repository using the master GitHub
API. The owner OAuth/PAT token stays in Termux; it is NEVER copied into the
Codespace. The Codespace key is stored at
~/.config/aaris-ai/canonical-deploy-key (0600) outside the game tree.
Its public key appears in repo Settings -> Deploy keys, named
aaris-codespace-<machine>. Revoke keys for retired Codespaces after verifying
that no agents still need them. They are not automatically revoked because
other agents may still be working. The target repository URL is pinned.

## Using the existing live Turbo terminal

No new worktree or extra Codespace is created. In any Codespace after setup:

    bash "$HOME/.local/share/aaris-git/canonical_git.sh" status
    bash "$HOME/.local/share/aaris-git/canonical_git.sh" verify
    bash "$HOME/.local/share/aaris-git/canonical_git.sh" fetch
    bash "$HOME/.local/share/aaris-git/canonical_git.sh" pull
    bash "$HOME/.local/share/aaris-git/canonical_git.sh" commit "Fix mobile camera" Assets/AarisWorld/Scripts/AarisOfflineDrop.cs
    bash "$HOME/.local/share/aaris-git/canonical_git.sh" publish
    bash "$HOME/.local/share/aaris-git/canonical_git.sh" push-main

These same commands can be submitted by ChatGPT to the already-connected
Cloudflare private command queue after the user authorizes the account and
the terminal shows a fresh verified heartbeat.

- status reports branch, canonical remote, Git LFS and tracked changes.
- verify checks repository-only SSH deploy-key access.
- fetch downloads canonical main history but does not touch working files.
- pull accepts only a fast-forward. If the branch diverged or tracked work
  is dirty, it stops safely without resetting/stashing or merging. A Unity
  checkout may separately require verified git lfs pull to hydrate binaries.
- commit stages only paths passed after the commit message, refusing to
  include other agents' previously staged changes.
- publish pushes committed HEAD into a separate remote
  codespace-sync/<machine>/<branch> branch WITHOUT force. The returned
  compare link lets you review a PR; it never auto-merges the PR.
- push-main requires a clean local main branch whose HEAD descends from
  the freshly fetched canonical main. Concurrent newer updates are rejected.

This helper does not change the Codespace's hosting origin, create
worktrees, reset Unity scenes/assets, delete generated files, start Unity,
handle owner passwords, or silently resolve multi-file merge conflicts.
Publishing a review branch is not the same as merging into main. Git LFS
pre-push remains enabled; missing large binaries may require separate
hydration, storage and network allowance.

Existing blocker as checked 2026-11-12: the active Codespace was on
fix/character-motion-original-icewall-20261111 at 1a56c09f with a
divergent candidate relative to canonical main (PR #391).
Do not force-merge or push-main from that branch. Investigate the real
conflicts and review the full Unity assets before integrating.

Security: Do not upload Codespace SSH private keys, GitHub tokens or Unity
licenses to GitHub commits, Actions logs, Cloudflare command output or public
Issues. Revoke a retired Codespace's key under the canonical repository
Settings -> Deploy keys.
