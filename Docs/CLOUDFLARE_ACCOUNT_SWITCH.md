# Aaris Turbo: Cloudflare account switch (Android Termux)

**Status:** Deployable source changes; no Cloudflare account is switched by merging code alone.

This changes only the Turbo control plane. It never restarts Unity Editor, overwrites a Unity scene, deletes a Codespace, or resets a Durable Object.

## One-time prerequisite

- The GitHub repo owner and current Codespace owner must be authenticated in Termux via GitHub CLI. Existing `Tools/Codespaces/termux_switch.sh` stores credentials in `~/.config/aaris-ai/master-gh` and `~/.config/aaris-ai/codespace-gh`, which this switcher can reuse.
- The target Codespace must already be **Available** and have Ubuntu/Linux x64, curl, tar, sha256sum and Python 3. The switcher installs SHA-256-verified user-local Node.js 22 (and npx) if missing; only the Codespace runs Wrangler.
- The owner must have an authorized Cloudflare account with Workers and Durable Objects enabled. The first Worker deployment to an account may require one-time workers.dev subdomain registration.
- Merge this PR before using the command below; the switcher must fetch the reviewed source files from `main`.
- Review and merge the separate SQL efficiency fix PR #338 to reduce repeated Free-tier row reads before continuing intensive screenshot monitoring.

## Install the phone command once

```bash
pkg install -y gh curl jq openssl coreutils
curl -fsSL https://raw.githubusercontent.com/sundamunda289-art/Unity-Game-Aris/main/Tools/Codespaces/termux_cloudflare_switch.sh -o "$HOME/aaris-cf-switch.sh"
chmod 700 "$HOME/aaris-cf-switch.sh"
```

## Switch an account

```bash
bash "$HOME/aaris-cf-switch.sh"
```

When several Codespaces are active, specify the exact machine name:

```bash
bash "$HOME/aaris-cf-switch.sh" aaris-ai-ubuntu-minimal-REPLACE_WITH_EXACT_NAME
```

The one phone command performs these steps:
1. Select an already-running Codespace using the authenticated Codespace GitHub account.
2. Generate or reuse a 64-character private watch key stored with permission 0600 at `~/.config/aaris-ai/cloudflare-watch.key`. Only its SHA-256 hash is sent to the remote deployment.
3. Over SSH, run the reviewed Linux script and open **https://dash.cloudflare.com/oauth2/device** in the browser. Approve the official Wrangler device code for the target Cloudflare account. Select its account ID if the login can access multiple accounts.
4. Deploy `Tools/Turbo/worker.mjs`, `watch_page.mjs`, and `wrangler.jsonc` to that Cloudflare account with SQLite-backed `MACHINES` Durable Object binding and `WATCH_TOKEN_HASH` secret. It does **not** upload the GitHub PAT or raw watch key.
5. Require `/health` to return the expected service response and both anonymous command endpoints to return HTTP 401 before activating the new URL.
6. Set the GitHub Actions **repository variable** `AARIS_BROKER_URL` (not `TURBO_WORKER_URL`, which the old bootstrap does not read). Keep the currently authorized Codespace account's `CODESPACE_PAT` GitHub Actions secret in sync through the GitHub CLI without printing it.
7. Dispatch the existing owner-only `aaris-turbo-bootstrap.yml` workflow for the exact machine, wait for its status and require the OIDC-minted token's real `TURBO_CLOUDFLARE_HANDSHAKE_PASS`. The remote installer replaces only the Turbo agent process; Unity Editor remains running.
8. If Termux:API clipboard is connected, copy a private `/watch#key=...` link to the phone clipboard, without logging that key.

The workflow's `AARIS_BROKER_URL` variable takes effect at the next workflow invocation. Changing only a GitHub secret does **not** update a currently running agent, whose actual connection information is in `~/.config/aaris-turbo/endpoint` and `token`.

## Failure and data separation

- The script never claims a completed switch until the bootstrap job succeeds. If its dispatch or job fails, it restores the previous `AARIS_BROKER_URL` repo variable where possible.
- If the new account has no workers.dev subdomain, the verified Node.js download fails, GitHub Codespace SSH fails, or an account lacks permission, fix that prerequisite and retry; do not delete the old Worker, old Durable Object, or Unity workspace.
- Accounts have independent Durable Object data: old pending jobs, screenshots and terminal histories do **not** automatically migrate. Do not switch while important old jobs are still in flight.
- Cloudflare usage limits apply to each eligible account under its plan and policies. Account switching is a disaster-recovery/authorized migration technique, not a guarantee of unlimited free resources or permission to circumvent platform restrictions.
- Browser login is a real user action. Never store Cloudflare credentials or GitHub access tokens in git, Issues, Actions logs, or a public URL query string.
