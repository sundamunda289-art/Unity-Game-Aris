# Canonical Git bridge for rotating Codespaces

**Current account-switch guide (2026-11-28):**
[TERMUX_ACCOUNT_SWITCH.md](TERMUX_ACCOUNT_SWITCH.md) documents the one-time
Termux installer, `CCF` / `CF` / `cf`, `CD`, detached provisioning and
`CD --status`. The current [AGENTS.md policy](../AGENTS.md) supersedes
historical manual switch/recovery snippets. This document remains the
reference for the repository-only deploy key and lower-level Git helper.

Canonical game source: https://github.com/sundamunda289-art/Unity-Game-Aris (main).
A Codespace can belong to a different GitHub account/fork. Never assume origin
points to the permanent game repository.

A new minimal Codespace is now created directly from this canonical public
repository using the selected hosting user's token and the runtime's
reviewed commit. It is owned by that hosting user; no new fork is required.
The Git bridge supports both this direct-canonical case and an existing
verified canonical fork, while keeping the owner token on the phone.

## One Termux command for account rotation

After the one-time command installation, switch/setup with:

    CD

Reuse the current account:

    CD --resume

Inspect progress:

    CD --status

The refreshed `termux_switch.sh` compatibility entry point delegates to the
same controller. Do not run an old independent switch/bootstrap in parallel
with an unfinished `CD` operation. The controller first verifies and commits
the private bridge, then runs/revalidates Git-key setup and detached
provisioning. Repeat the original command to resume its recorded operation;
an unknown remote result is reconciled before another request.

The operation separately pins the installed runtime `SOURCE_SHA` and the
game `project_sha` resolved from canonical `main`. The provisioner receives
the latter as its `--source-sha`. It verifies/installs Unity Editor first,
then fetches required target LFS objects with one transfer at a time before
a safe fast-forward and explicit checkout. Every target payload is checked
against its declared size and SHA256. Same-machine Unity licensing may run
in parallel only after the Editor is ready.

Dirty tracked source, divergent history, hidden tracked-file flags or file
collisions pause automatic synchronization. Existing work is preserved;
`merge --ff-only --no-overwrite-ignore` protects ignored untracked collisions.
The setup does not reset, clean, stash, force checkout or create a worktree.
See the current guide for disk budgeting and readiness/validation limits.

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

