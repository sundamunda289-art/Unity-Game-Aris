# Aaris Turbo: owner-authorized Cloudflare account switch from Termux

The Codespace/GitHub repository, Unity Editor process, and game assets stay unchanged. The Cloudflare Worker and its SQLite-backed `TurboMachine` Durable Object are deployed to **the Cloudflare account explicitly authorized by the owner**. GitHub Actions then reissues a machine-scoped Turbo agent grant using the pinned OIDC bootstrap. This does **not** transfer the old account's stored commands, screenshots, or usage counters.

> The Free plan has daily usage limits. Account switching is only for legitimately owned/authorized accounts and must comply with Cloudflare's terms. Optimizing queries and reducing polling remains necessary. Moving workloads does not increase or reset any single account's quota.

## One-time preparation

- **Permanent codebase vs hosting:** source stays `sundamunda289-art/Unity-Game-Aris`, while the Codespace can be hosted at `cgfdgvhvg-ship-it/Unity-Game-Aris` or another authorized account's `Unity-Game-Aris` fork.
- Have a **running** Ubuntu Linux Codespace in an authorized `*/Unity-Game-Aris` hosting repository accessible by an authorized GitHub login, and its valid `CODESPACE_PAT` configured in the repository for the one-time Turbo bootstrap. A Codespace is the temporary Linux host for the Cloudflare Wrangler CLI, not the actual Unity build target changed by the switch.
- Install Termux, and authorize the repository owner and the Codespace account through the official GitHub device flow if prompted.
- The approved repo `main` must contain `Tools/Turbo/wrangler.jsonc`, `Tools/Codespaces/termux_cloudflare_switch.sh` and `Tools/Turbo/cloudflare_account_switch_linux.sh` and the `AARIS_BROKER_URL` variable fallback in `.github/workflows/aaris-turbo-bootstrap.yml`. Do not run against an unmerged PR expecting it to already be on `main`.

On the Android phone:

```bash
pkg install -y curl
curl -fsSL https://raw.githubusercontent.com/sundamunda289-art/Unity-Game-Aris/main/Tools/Codespaces/termux_cloudflare_switch.sh -o "$HOME/aaris-cf-switch.sh"
chmod 700 "$HOME/aaris-cf-switch.sh"
bash "$HOME/aaris-cf-switch.sh"
```

If multiple running Codespaces are visible, give the **exact** chosen name:

```bash
bash "$HOME/aaris-cf-switch.sh" EXACT-CODESPACE-NAME
```

The switcher discovers Codespaces across the selected GitHub account (not only the source repo), validates that the selected one is `Available` and belongs to an authorized `*/Unity-Game-Aris` hosting repository. The script uses `gh codespace ssh` to run Wrangler **inside the running Linux Codespace**, not on native Android. Wrangler's `login --device --browser=false` prints the official Cloudflare authorization link. The phone script opens it automatically if `termux-open-url` is available; otherwise open the printed link manually. Approve the intended account. If the authenticated Cloudflare login has more than one accessible account, the script asks you to choose its exact 32-character Cloudflare Account ID; it cannot safely infer which account you intend.

The new account must have an enabled `workers.dev` subdomain. The script deploys only the Worker source from the pinned GitHub `main` commit, without modifying the current `/workspaces/Unity-Game-Aris` working tree or touching Unity's caches. It creates the `MACHINES` Durable Object SQLite binding using the account-neutral `wrangler.jsonc` config, and sets only `WATCH_TOKEN_HASH` as a Worker secret. No `GITHUB_TOKEN`, `GITHUB_PAT`, Unity login, or license is copied into Cloudflare.

The private watch key is generated once and stored only on the phone with permission `0600` at `~/.config/aaris-ai/cloudflare-watch.key`. If an earlier monitor used a different browser bookmark key, that bookmark will need the new key for the new account. Never post either key in GitHub issues or this chat.

After a successful new Worker `/health` test, the script updates the GitHub **Actions variable** `AARIS_BROKER_URL` (non-secret) and dispatches the exact-machine workflow `aaris-turbo-bootstrap.yml`. This uses OIDC to grant a new scoped agent token in the new account and restarts only the Turbo agent process, **not** the Codespace or Unity Editor. The script watches the GitHub bootstrap run. To verify the actual agent handshake, check for `TURBO_CLOUDFLARE_HANDSHAKE_PASS` in that run's logs. If bootstrap definitively fails, it restores the old GitHub broker URL. Do not switch while an important remote Turbo command is still streaming, since the agent process must be replaced.

## Key boundaries and limitations

- Cloudflare login in your browser does **not** change the Cloudflare connector already connected to ChatGPT. To use the new account's private Durable Object directly from ChatGPT, reconnect/authorize the Cloudflare integration for that account as necessary. The Termux script cannot silently change ChatGPT's connector identity.
- `AARIS_BROKER_URL` is an Actions variable, **not** a new Codespace environment secret. Editing an existing Codespace's GitHub secret alone would not affect its running agent. The bootstrap explicitly overwrites the agent's own private endpoint/token and starts a new agent process.
- The script never deletes the prior account's Worker, Durable Object, secrets, old queue, or private logs. When switching back, the old account retains its own data, subject to its retention/cleanup logic.
- Wrangler's short-lived OAuth session is stored on the Linux Codespace during deployment. Do not copy Cloudflare credentials into GitHub, the Unity game, or public logs.
- A healthy `/health` endpoint proves only basic Worker routing. Real command submission must be checked with an authorized client after the Actions bootstrap reports the handshake. A Cloudflare edge policy returning HTTP 403 may still prevent the agent from connecting.
- The bootstrap workflow's GitHub Codespaces credential must already match the Codespace owner. To change *GitHub* Codespaces accounts, continue to use `Tools/Codespaces/termux_switch.sh` first.
- There is no reliable guarantee of a one-minute deployment: OAuth, npm, Cloudflare deploy, Codespace SSH and GitHub Actions can each take time or encounter service-side errors.
