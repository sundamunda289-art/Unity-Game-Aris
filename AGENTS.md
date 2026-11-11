# Aaris Unity CI agent instructions

Repository: sundamunda289-art/Unity-Game-Aris; branch: main. Public repository: NEVER commit or release passwords, tokens, Unity license files, Hub state, private content or secrets.

## 2026-11-11: FIRST-READ RUNBOOK — Cloudflare Turbo Codespace terminal + GUI (current preferred control)

**READ THIS FIRST in a NEW CHAT.** The canonical file is **AGENTS.md** at the repository root (not agent.md). This section supersedes older descriptions of GitHub Actions *per-command* terminal control, but DOES NOT supersede Unity license rules, safety rules, preservation of dirty source assets, existing active APK builds, or evidence requirements elsewhere in this file. Detailed source/operational reference: [Docs/AARIS_TURBO_TERMINAL.md](Docs/AARIS_TURBO_TERMINAL.md). Work with the existing connected **GitHub (sundamunda289-art)** and **Cloudflare** connectors if those tools are available in the present chat; do not pretend their presence or live connection state if absent.

### Operational decision tree — avoid slow GitHub Actions for routine work

1. **Check Cloudflare first.** Cloudflare Worker script name: `aaris-turbo-terminal`. Durable Object binding `MACHINES`, class `TurboMachine`, namespace currently named `aaris-turbo-terminal_TurboMachine`. A Codespace-specific Durable Object is named `machine:<exact-codespace-name>`; there is also a shared `registry` object. Enumerate current registry with the linked Cloudflare management API; an old machine name or a historical `connected` stage alone is **not** proof it is online. Query `last_seen` and compute heartbeat age; prefer a fresh heartbeat (normally within 90 seconds) and prove with a harmless actual command before claiming control. Do not hardcode a Codespace name; multiple GitHub accounts/machines can rotate.
2. **If a machine is active and its Turbo agent is responding:** ChatGPT submits commands **directly to its private Cloudflare Durable Object SQL queue**, reads stdout/stderr/status from the same queue, and takes private GUI screenshots with the same protocol. **NO new GitHub Actions job per command.** Never upload commands to public GitHub Issues, git commits, or a public `cmd.txt`.
3. **If the chosen Codespace is shut down or the agent is missing/expired:** check GitHub Codespace state/account/quotas and bootstrap **once** with `.github/workflows/aaris-turbo-bootstrap.yml` via owner-only `workflow_dispatch` with exact `machine`, or one **owner-authored** Issue #77 comment `TURBO START <exact-codespace-name>`. The workflow uses repository secret `CODESPACE_PAT` (currently selected Codespaces GitHub account), official GitHub Codespaces SSH, signed GitHub Actions OIDC, and Cloudflare to start/reuse an authorized existing Codespace if needed, install/rotate its agent, and exit. A green bootstrap is only accepted after authenticated `TURBO_CLOUDFLARE_HANDSHAKE_PASS`; then verify a real command output. Do not start multiple bootstraps repeatedly, nor choose ambiguous machines.
4. **If the user rotates GitHub Codespaces accounts:** use the single Termux helper `Tools/Codespaces/termux_switch.sh`, not an unscoped old GitHub token. The phone command `bash "$HOME/aaris-switch.sh"` prompts for official device/browser login for the new Codespaces account, updates repo Actions secret `CODESPACE_PAT`, and dispatches **one** `aaris-turbo-bootstrap.yml` run with `machine=auto`. To reuse the already authorized account without a second login, use `bash "$HOME/aaris-switch.sh" --resume`. Refresh that helper from its verified `main` GitHub source if an old local copy predates this workflow:
   ```bash
   pkg install -y gh curl jq
   curl -fsSL https://raw.githubusercontent.com/sundamunda289-art/Unity-Game-Aris/main/Tools/Codespaces/termux_switch.sh -o "$HOME/aaris-switch.sh"
   bash "$HOME/aaris-switch.sh"
   ```
   `auto` reuses a uniquely selectable Codespace; **only if that selected account has none** does it create one minimal Ubuntu SSH Codespace. When several are ambiguous, selection fails closed—use an exact name. Codespaces quota cannot be bypassed. Do **not** touch the old exhausted account or delete its projects.
5. **Auto-resume:** the updated **minimal Ubuntu** devcontainer invokes `Tools/Turbo/ensure_agent.sh` on start and reconnects the installed agent if its local seven-day grant has not expired. A new account/machine or expired grant needs the one-time owner-only bootstrap. Original full Unity Codespaces need their own verified startup handling.

### Cross-account codebase vs Codespace hosting — verified 2026-11-11

- **Canonical source remains** `sundamunda289-art/Unity-Game-Aris` (`main`). Its GitHub Actions, Unity scenes/prefabs/LFS content, and Cloudflare Worker source stay authoritative.
- **Codespace hosting can be a different GitHub account/repository.** Termux showed `cgfdgvhvg-ship-it/Unity-Game-Aris` contains `aaris-ai-ubuntu-minimal-4qr6v94w9rwvhjgp6` in `Available` state. This proves only GitHub Codespace state, not whether Unity Editor or Turbo agent is actually running.
- Under the selected Codespace owner's GitHub credential, use `gh codespace list --json name,state,repository` **without `-R sundamunda289-art/Unity-Game-Aris`**. Select only the exact named authorized `*/Unity-Game-Aris` Codespace. Do not confuse its hosting repo with the permanent master codebase.
- `Tools/Codespaces/termux_cloudflare_switch.sh` must deploy the Worker from canonical source and then SSH into the selected alternate-account Codespace. `.github/workflows/aaris-turbo-bootstrap.yml` must list Codespaces belonging to the authorized `CODESPACE_PAT` account. Its OIDC claim verification must stay pinned to the master `main` workflow/owner — never weaken it for the hosting fork.
- Switching either GitHub or Cloudflare accounts does not authorize overwriting workspace files, stopping Unity, or exposing private credentials. Verify a genuine live terminal command and nonblank screen capture after reconnection.

### NEW CODESPACE / NEW GITHUB ACCOUNT — exact zero-to-live setup checklist

**Mandatory for future chats whenever the user says "old Codespace quota finished, move the whole setup to a new Codespace."** Follow the stages below in order, using the connected GitHub and Cloudflare tools; **do not treat setup as complete until terminal AND screen E2E tests pass**. This is a transplant of the private control plane, **not** automatic installation/verification of the full Unity Editor and game build toolchain.

**Stage 0 — account and machine discovery:** Check existing Cloudflare registry heartbeat first. Do not touch the exhausted old Codespace. Find the new Codespaces account through the already selected repository Actions secret \`CODESPACE_PAT\` (the owner must previously authorize that account). If changed, ask the user to run the official Termux \`termux_switch.sh\` login once; this rotates \`CODESPACE_PAT\` and dispatches the bootstrap. If account already authenticated, \`bash "$HOME/aaris-switch.sh" --resume\` suffices. This is the ONLY routinely necessary manual step. **Do not ask the user for GitHub PAT, Cloudflare API token, passwords or Unity license data in chat.**

**Stage 1 — create/select/start exactly ONE new Codespace:** \`.github/workflows/aaris-turbo-bootstrap.yml\` supports owner-only \`workflow_dispatch\` with \`machine=auto\` or exact \`machine=<name>\`; the owner-only Issue #77 comment \`TURBO START <exact-name>\` is the other supported trigger. The workflow uses \`gh codespace list -R "$GITHUB_REPOSITORY" --json name,displayName,state\`, then \`Tools/Turbo/select_machine.py\`: if the selected account has **zero** Codespaces, \`auto\` creates one minimal Ubuntu 22.04 SSH devcontainer, using \`.devcontainer/minimal-ubuntu/devcontainer.json\`; if exactly one is eligible it reuses it; if several are ambiguous it fails closed and requires an exact machine name. It starts stopped machines with the GitHub Codespaces API when authorized, waits for \`Available\`, and refuses destructive reset/delete. If the user wants an entire Unity Editor environment rather than merely the remote terminal, choose/verify an appropriate full Unity devcontainer explicitly: the minimal image alone does NOT contain the Editor, SDK or fetched Git LFS binaries. If provisioning stalls or quota is exhausted, report it; do not endlessly create replacement machines or launch repeated Actions.

**Stage 2 — secure one-time GitHub Actions -> Cloudflare agent bootstrap:** The workflow checks out the current reviewed \`main\` and reads \`Tools/Turbo/agent.py\`, \`gui_agent.py\`, \`gui_setup.sh\`, \`remote_install.sh\`. It requests a signed GitHub Actions OIDC token with audience \`aaris-turbo-bootstrap-v1\` and sends it as a Bearer token to the pre-existing \`aaris-turbo-terminal\` Worker \`POST /bootstrap\` with the **exact Codespace name**. The Worker is JavaScript \`Tools/Turbo/worker.mjs\` **deployed on Cloudflare Workers**, with SQL \`TurboMachine\` Durable Objects; **JavaScript does not need to be run/imported by Node.js on the Codespace** to connect. The Worker verifies GitHub issuer/signature/repository/owner/actor/workflow/ref before minting a scoped, expiring agent token. **No user-supplied Cloudflare API key or new public tunnel required.** The workflow SSH-streams reviewed scripts/credential to that exact Codespace only.

**Stage 3 — what actually gets installed INSIDE the new Linux Codespace:** \`Tools/Turbo/remote_install.sh\` is executed once over authorized SSH; it copies files from the GitHub Actions checkout into that Codespace's **private home**:
- \`~/.local/share/aaris-turbo/agent.py\` — Python 3 **standard-library** outbound HTTPS agent; uses \`base64\`, \`json\`, \`urllib.request\`, \`subprocess\`, \`selectors\`, \`threading\`, \`pathlib\`, etc. No \`pip install\` is needed for the terminal transport.
- \`~/.local/share/aaris-turbo/gui.py\` — Python 3 screenshot/mouse/keyboard script. Uses Python **Pillow** (\`from PIL import Image, ImageDraw\`) plus built-in modules (\`subprocess\`, \`io\`, \`json\`, \`base64\`), and the system binaries \`Xvfb\`, \`fluxbox\`, \`scrot\`, \`xdotool\`, \`xclip\`, \`xterm\`. \`gui_setup.sh\` installs these with Ubuntu \`apt-get\` and \`python3-pil\` only if absent; package installation runs non-blockingly after terminal bootstrap, so check readiness separately.
- \`~/.local/share/aaris-turbo/gui_setup.sh\` — optional GUI dependency installer.
- \`~/.config/aaris-turbo/token\`, \`endpoint\`, \`expires_at\` — owner-restricted session metadata (token mode 0600); **never print, copy to git, screenshot, or publish**.
- The process starts detached (\`nohup\`); \`~/.config/aaris-turbo/agent.pid\` and \`agent.log\` are private. \`Tools/Turbo/ensure_agent.sh\` is invoked via the **updated minimal devcontainer's** \`postStartCommand\` to auto-resume an unexpired token after a restart. Existing full Unity devcontainers may have different startup hooks; verify before claiming auto-resume.

**Stage 4 — required real connection proof:** GitHub Actions must report \`TURBO_AGENT_LIVE\`, \`TURBO_CLOUDFLARE_HANDSHAKE_PASS\` and \`TURBO_NONDESTRUCTIVE_BOOTSTRAP_OK\`. The agent establishes outbound HTTPS to Worker \`/agent/<machine>/ping|next|chunk|done\` and polls its command queue. The Codespace-specific token is checked by the Durable Object; the Worker registry receives a \`connected\` heartbeat. The Python client sets \`User-Agent: Mozilla/5.0\` because the default \`python-urllib\` User-Agent was observed to receive HTML 403 at the edge; there is also a token-protected same-host \`GET /health\` header-based fallback for route restrictions. Do not remove credential verification to fix a 403. The ChatGPT Cloudflare connector then uses **private management SQL query/v2 API** (details immediately below), sends a unique \`["pwd"]\` argv job with cwd \`/workspaces/Unity-Game-Aris\` (or another verified directory inside /workspaces), and checks that the returned output is the expected directory and exit code 0. Send a second unique \`["python3","-c","print('AARIS_TURBO_PYTHON_E2E_PASS')"]\` job and verify status \`completed\` and actual stdout. **No GitHub Actions runner for these command tests.** A new Codespace with a different home or workspace path requires discovery, not guesses.

**Stage 5 — actual screen + mouse/keyboard proof on new machine:** After optional GUI dependencies are READY, use the Cloudflare private command queue to run \`["bash","-lc","python3 \"$HOME/.local/share/aaris-turbo/gui.py\" look --fast"]\`. The helper launches/reuses an X11 virtual display (normally \`:99\`, 1280x720), Fluxbox and xterm where installed. It returns stdout markers \`AARIS_GUI_FRAME_SIZE\`, \`AARIS_GUI_FRAME_MODE\`, \`AARIS_GUI_FRAME_BASE64\`, \`AARIS_GUI_FRAME_COMPLETE\`. Read all ordered private chunks and decode a **real JPEG** in the assistant's supported image view; fast previews are usually 800x450 and capped at 32 KiB; full \`look\` can return 1280x720 up to 96 KB. Then test \`gui.py click 300 200 --fast\` and verify cursor movement in the next frame (800x450 -> real 1280x720 position 480x320), and optionally \`gui.py type 'echo AARIS_GUI_INPUT_PASS'\` plus \`gui.py key Return\`; verify the new terminal text visually. **Do not expose a GUI port, public noVNC, unauthenticated WebRTC or trycloudflare tunnel; no permanently running ChatGPT video or hidden ongoing watching is implied.** A screenshot of Xterm or empty Xvfb proves only the virtual desktop, NOT a successfully opened Unity Editor.

**Stage 6 — whole Unity GAME project migration is a separate, explicit phase:** Check \`pwd\`, \`git status --short\`, \`git lfs version\`, LFS object availability/hydration, Unity Editor \`6000.3.26f1\`, required Unity license authorization, URP project, Android JDK/SDK/NDK, scene load and actual game-camera/Play-Mode screenshot. A Codespace may hold just a Git checkout with LFS pointer files; these are not valid native Unity assets. Provision missing packages only when needed, respect quota/storage, avoid running expensive Unity imports on a 2 GB/limited-storage environment without verifying resources, and NEVER overwrite an existing dirty workspace. Record Editor/build success only when backed by actual logs/screenshots. Prefer the project's existing build/release workflows for APKs; the Turbo GUI does not magically install Unity.

**Stage 7 — recovery and handoff:** If only terminal works but GUI fails, run a Cloudflare queued, non-secret \`command -v Xvfb scrot xdotool\` and \`python3 -c 'from PIL import Image'\` diagnostic, then use \`gui_setup.sh\` once and retry; don't recreate Codespace. If the agent does not ping, check private process health, expired grant, Worker \`/health\`, GitHub state and one bootstrap, not a run for every command. If an old Codespace quota ends, switch the account in Termux once, select/create a new machine and replay Stages 1–5. Preserve public repo, Cloudflare Worker, namespace and SQL state rather than redeploying them unnecessarily. Clean up sensitive frames using the Worker 24-hour private retention policy (its live cleanup behavior must still be verified); never publish frames/credentials to Issue #77. When presenting status to user, distinguish **created / started / authenticated / terminal E2E / GUI E2E / Unity scene E2E** explicitly.

### Permanent private Chrome monitor (2026-11-11)

The **same** Cloudflare Worker `aaris-turbo-terminal` also hosts a stable,
mobile-friendly **read-only** dashboard at
`https://aaris-turbo-terminal.aaris-game-control-6f9c.workers.dev/watch`.
The user can bookmark their private **`/watch#key=<64-hex-secret>`** link once.
The capability key belongs in Cloudflare Worker secret
`WATCH_TOKEN_HASH` as its SHA-256 hash; **NEVER put the key or hash in
AGENTS.md, source code, GitHub Issues, screenshots, terminal logs, or query
parameters**. The browser URL fragment is not sent in HTTP requests; the
dashboard sends the key exclusively as a same-origin HTTPS Authorization
Bearer header to `/watch/api`, `/watch/frame`, and `/watch/capture`.
Only a holder of that private bookmark can see the mirrored screen and
terminal activity. If the key is exposed, rotate the Worker secret and
privately issue a replacement bookmark; there is otherwise no per-Codespace
URL rotation.

**What appears on the dashboard:** the latest successfully completed GUI
frame (screenshot-on-action from `gui.py`, whether requested by ChatGPT
or the viewer), last 20 bounded/redacted command summaries and stdout/stderr,
current machine name, last heartbeat and frame timestamp. Existing
`TurboMachine` Durable Objects mirror the latest frame to private SQLite
state, while the global `registry` chooses the most recently connected
machine with a fresh heartbeat. The viewer can select a specific registered
machine. When GitHub accounts/Codespaces change, **the browser link and
Cloudflare Worker stay the same**; only the selected active machine changes.

**Refresh logic:** when Chrome tab is visible and viewer not paused, the
browser polls at 3/5/10-second options. Authenticated `/watch/api` can queue
a bounded fast `gui.py look --fast` job only if the agent is connected,
the last image is stale, and no other viewer capture is already queued/running.
`Snap now` requests one capture via `POST /watch/capture`; `GET
/watch/frame` returns the actual private JPEG. The client fetches a frame
**only when the frame ID changes**; hidden tabs and paused pages request
neither frames nor new captures. This is *not* continuous WebRTC/video and
does not make ChatGPT run while the chat is inactive; normal terminal and
GUI actions still use the connected Cloudflare management SQL bridge.

**Security:** protect the private bookmark like a password. The dashboard
has NO arbitrary-shell input, NO unauthenticated mouse control, NO public
screen URL, and no new Codespace tunnel. Tokens must be managed as
Cloudflare Worker secrets, never literal fallback strings. Before claiming
a working viewer, verify that an unauthorized `/watch/api` request gets
401, the authorized browser shows an actual changed frame and recent
command output, machine switching works, and the existing agent's
`/health`/OIDC bootstrap still work. Detailed installation, API and
security notes live in `Docs/AARIS_TURBO_TERMINAL.md`.

### How ChatGPT's connected Cloudflare tool actually sends commands

The ChatGPT Cloudflare connector has an authenticated management API. In a new chat, discover its `mcp__Cloudflare__execute` action through the available connector tools (Code Mode exposes it as `tools.mcp__Cloudflare__execute({code:"async () => { ... }"})` when installed), not the public Worker URL. The action supports `cloudflare.request({method:"POST",path:"/accounts/"+accountId+"/workers/durable_objects/namespaces/"+namespaceId+"/query/v2",body:{...}})`. Discover/confirm the current account and namespace ID from the connected Cloudflare account; previously verified namespace ID `14f5c66e402c4c0e94614293423f5630` is a **hint, not a universal constant**. Never paste a Cloudflare API token into chat or GitHub. The Worker /health endpoint is for broker/agent health, **not** a public shell execution API.

**Get the machine registry:** send the following JSON body to that *private management query/v2 API*:

```json
{
  "durable_object_name": "registry",
  "jurisdiction": "none",
  "queries": [
    {
      "sql": "SELECT name,stage,last_seen,unixepoch()-last_seen AS age_seconds FROM machines ORDER BY last_seen DESC LIMIT 20"
    }
  ]
}
```

A matching current heartbeat is necessary, not sufficient. The most recently *verified* 2026-11-11 active minimal Codespace was `aaris-ai-ubuntu-minimal-4qr6v94w9rwvhjgp6` for Codespaces account `cgfdgvhvg-ship-it`, but this is **historical test evidence**, not a fixed future destination.

**Send a non-destructive command:** create a fresh unique alphanumeric/underscore/hyphen job ID per request; for example:

```json
{
  "durable_object_name": "machine:<EXACT_MACHINE_NAME>",
  "jurisdiction": "none",
  "queries": [
    {
      "sql": "INSERT OR IGNORE INTO commands(id,argv_json,cwd,max_seconds,expires_at,status) VALUES(?,?,?,?,unixepoch()+600,'queued')",
      "params": [
        "unique_job_id_001",
        "[\"pwd\"]",
        "/workspaces/Unity-Game-Aris",
        30
      ]
    },
    {
      "sql": "SELECT id,status FROM commands WHERE id=?",
      "params": ["unique_job_id_001"]
    }
  ]
}
```

`argv_json` is a **JSON-encoded argv array string**, NOT raw shell commands or arbitrary JSON objects. Python example: `"[\"python3\",\"-c\",\"print('AARIS_TURBO_TEST_PASS')\"]"`. More complicated scripts may use an explicit argv such as `["bash","-lc","<reviewed bounded script>"]`, properly JSON-serialized. All jobs require a **real directory under /workspaces** for `cwd` (for this repo usually `/workspaces/Unity-Game-Aris`), bounded `max_seconds`, and a future expiry. The Codespace Python agent executes without `shell=True`, can dispatch up to two jobs, streams private UTF-8 output chunks, and returns exit code. An INSERT with `queued` status is *NOT proof* that the command ran.

**Read execution status and output**, repeating with increasing `seq` until done:

```json
{
  "durable_object_name": "machine:<EXACT_MACHINE_NAME>",
  "jurisdiction": "none",
  "queries": [
    {
      "sql": "SELECT id,status,exit_code,error,started_at,finished_at FROM commands WHERE id=?",
      "params": ["unique_job_id_001"]
    },
    {
      "sql": "SELECT seq,kind,text FROM chunks WHERE command_id=? AND seq>? ORDER BY seq LIMIT 20",
      "params": ["unique_job_id_001", -1]
    }
  ]
}
```

Inspect `result.results[i].columns` and `.rows` (array rows), not just the first output or an assumed dictionary. Use `seq > last_seen_seq` to avoid duplicate output. For screenshot text/base64, request **small, consecutive chunk batches** (e.g. 4–6 chunk rows each) because tool responses may truncate large payloads. A command succeeds only when its status is `completed`, exit code is `0`, and its expected output appears. Failure statuses are never hidden.

### How to SEE and CONTROL a real Linux desktop without ChatGPT Work

The private GUI helper is `Tools/Turbo/gui_agent.py`, installed inside the selected Codespace at `$HOME/.local/share/aaris-turbo/gui.py`. `Tools/Turbo/gui_setup.sh` installs Xvfb, Fluxbox, xterm, scrot, xdotool, xclip, Pillow as needed. It reuses a valid existing X display when available or starts a virtual `:99` 1280x720 X11 screen. The desktop is a **virtual Linux desktop**, not necessarily Unity Editor.

Use the **same authenticated Cloudflare command queue**, with argv_json equivalent to:

```text
["bash","-lc","python3 \"$HOME/.local/share/aaris-turbo/gui.py\" look --fast"]
["bash","-lc","python3 \"$HOME/.local/share/aaris-turbo/gui.py\" click 300 200 --fast"]
["bash","-lc","python3 \"$HOME/.local/share/aaris-turbo/gui.py\" key Return --fast"]
["bash","-lc","python3 \"$HOME/.local/share/aaris-turbo/gui.py\" look"]
```

Place `--fast` **last**: `look --fast` produces an adaptive preview (typically 800x450, up to 32,768 JPEG bytes), while `look` provides full-res up to 1280x720 (up to 96,000 JPEG bytes). Other CLI actions: `click x y`, `double_click x y`, `right_click x y`, `move x y`, `scroll +/-N`, `key X11_KEYS`, `type bounded_text`, `paste bounded_text`. Each action captures a *new* screenshot in the same job. Output chunks contain `AARIS_GUI_FRAME_SIZE=`, `AARIS_GUI_FRAME_MODE=`, `AARIS_GUI_FRAME_BASE64=<long JPEG base64>`, `AARIS_GUI_FRAME_COMPLETE`. Reassemble all chunk texts **in seq order**; validate the complete marker, then show image via the connected tool's supported private image output if available (Code Mode supports `image("data:image/jpeg;base64,"+base64)` when permitted), not a fake/sandbox/public URL. The grid is every 100 displayed pixels and the pointer is red. **Click coordinates are relative to the latest screenshot**; the helper saves frame size and maps fast-preview coordinates back to X11. For example, 800x450 click `300,200` was verified at real X11 `480,320`.

**Test evidence from 2026-11-11:** connected private terminal `pwd` returned `/workspaces/Unity-Game-Aris` with code 0; Python test returned `AARIS_TURBO_E2E_PASS`; 1280x720 screenshot was reconstructed privately from 62 chunks; a click at 450,280 produced a new cursor position; GUI typed `echo AARIS_GUI_KEYBOARD_PASS` and Enter, and screenshot visibly showed output; fast preview returned an 800x450 image in 20 chunks; the 800x450 click-to-1280x720 mapping was verified. These prove **Codespace terminal and X11 GUI control**, **not** that Unity Editor or the actual native game scene has loaded. Confirm Unity installation, active license, LFS hydration, Editor camera scene screenshot and Play Mode separately before reporting game changes tested. WebRTC, publicly exposed noVNC, public trycloudflare.com tunnels, and unapproved remote MCP services are not needed for this private on-demand screenshot protocol. The ordinary ChatGPT chat does **not** gain an always-running background monitor: each observation/action requires a tool call while assisting; use explicit automation only if requested and supported.

### Security, privacy, resource constraints, and recovery

- **Never** place Cloudflare/Actions bearer tokens, `CODESPACE_PAT`, Unity email/password/license, screenshots, clipboard contents, raw terminal logs, or full environment dumps in public Issues/PRs/commits/release artifacts. Do not print screenshots/base64 on the public Issue #77. The Cloudflare management SQL API is privileged terminal access—treat it as sensitive, respect user-approved scope and validate commands before executing.
- Cloudflare public Worker only exposes limited health and authenticated agent/bootstrap functions. The agent uses outbound HTTPS only, with a scoped rotating token granted by signed GitHub Actions OIDC. No inbound Codespace SSH port or permanent public desktop endpoint. The agent has a tested browser-like User-Agent because the default Python urllib UA returned HTML 403 from the Cloudflare edge; this was resolved in PR #321. No manual Cloudflare token paste is required when the Cloudflare connector is connected.
- Latest code: `Tools/Turbo/worker.mjs`, `agent.py`, `gui_agent.py`, `gui_setup.sh`, `remote_install.sh`, `ensure_agent.sh`, `select_machine.py`, and workflow `.github/workflows/aaris-turbo-bootstrap.yml`. The Worker contains a **24-hour private-output retention cleanup policy**, but its scheduled cleanup must be independently verified—do not claim the garbage-collection job passed solely because the code was deployed.
- Existing editor/APK jobs continue independently. Do not interrupt an active build, reset user worktrees, discard original `.unity`/`.prefab`/`.mat` LFS objects, or treat a green static CI or desktop screenshot as proof a native game build works. For truly long-running jobs, use the bounded command API, status polling and per-job exit codes; no repeated GitHub Actions runners.
- For inactive or expired agent: check the current Codespaces account/selected machine, GitHub `Available` state, and the linked Cloudflare heartbeat; **one** bootstrap via owner-only workflow/Issue is the recovery path. If quota is exhausted or a Codespace remains `Provisioning`, report the blocker; do not create/delete more machines without need. If connected Cloudflare/GitHub tools are unavailable in the new chat, say so and help the user reconnect them instead of inventing an active control connection.
- The reference instructions below and `Docs/AARIS_TURBO_TERMINAL.md` remain available for older pipelines and specialized Unity editor/release tasks, but routine live terminal/gui use follows this section.

## Canonical world
Unity 6000.3.26f1, URP 17.3.0. Native source scene: Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity; 2km by 2km world on four real TerrainData tiles. See Docs/MAP_LAYOUT.json, README.md, the existing AarisExplorerBuild.cs and Tools/Validation before edits. Many assets are Git LFS tracked: a pointer is NOT a valid Unity scene. A Release checkpoint does not replace permanent Git history.

## Existing owner Codespace (non-destructive, preferred for real Editor evidence)
- The original owner-approved, persistent 4-core/16-GB Codespace `aaris-ai-unity-ssh-vp5g97jv4r4gfpjgq` remains protected and is selected only when visible to the currently authenticated Codespace account. After a user-authorized `CODESPACE_PAT` account switch, routine `CS START`, `CS STATUS`, `CS EDITOR`, `CS SHOT`, `CS PLAY DIAG` and diagnostics may target that account's **one uniquely named** `aaris-ai-unity-ssh` Codespace, or an explicit exact name provided through workflow input. Newly created account `tomaaaris1234-pixel` has Codespace `aaris-ai-unity-ssh-r7x67xqjg4qphgx6` (creation verified in Actions run 42514517744). Never choose a first arbitrary entry, guess among duplicates, reset another workspace, or create a replacement with `CS START`. `CS FRESH SSH` is the explicit create operation. Preserve the previous owner's dirty 210-file workspace completely.
- 2026-11-08 owner workspace had **210 tracked modified files** (raw byte-different), so treat it as **dirty user work**. Before any `CS SYNC` / rebuild / save / branch change, preserve and review those files. Do not `git reset --hard`, `git clean`, blanket overwrite, discard or auto-merge its Unity scenes, TerrainData, prefabs, materials or Git LFS objects. Changes for the new map improvements belong on a separate Git review branch until reconciled with the dirty workspace.
- For a verified native Editor segfault (exit code 139), the owner-only `CS EDITOR ISOLATE` operation runs fixed diagnostics in a **new, disposable /tmp blank project**, never the original map, LFS checkout, or source scene. It tries the installed Editor version, blank-project headless import and—only if headless passes—the software-GL path under Xvfb. It reports bounded exit codes/causes, keeps all raw logs private in its temporary directory, and is not visual certification. It may time out on slow initial import; report timeouts as inconclusive. The workflow streams the exact reviewed script over SSH; do not run an old checkout script or allow arbitrary commands on public Issue #77.
- Startup `EDITOR_PROCESS_STARTED_LICENSE_NOT_VERIFIED` is only a transient process check. Require a **later** live process and a nonblank, actual Unity scene screenshot/Play Mode report before claiming that the native world rendered. `CS SHOT` was blocked by `SHOT_BLOCKED_EDITOR_NOT_RUNNING`; editor diagnostics were unclassified, and the last available compiler log had zero matched C# compiler errors. These are separate evidence levels, not confirmation of a successful Editor session.
- The source `AarisSettlementExpansion.cs` adds local three-LOD prefabs at **runtime** from `Assets/Resources/AarisExpansion/WorldExpansionPlan.json`; additions do **not** silently rewrite the native `.unity` scene. To verify new placements visually, run the review PR in a clean isolated worktree/verified Unity Editor session, not on top of the 210 unsaved original files. Tests in `Tools/Validation/test_settlement_expansion.py` are static gates, not visual proof.

## Minimal Ubuntu replacement for a failed recovery Codespace

For the newly selected `tomaaaris1234-pixel` account, if the original `aaris-ai-unity-ssh-r7x67xqjg4qphgx6` stays Alpine/recovery despite rebuild and scoped recovery steps, `CS FRESH MINIMAL` explicitly creates a separate Ubuntu 22.04 Codespace from the current `main`, using a lightweight `.devcontainer/minimal-ubuntu/devcontainer.json` with SSH feature and **no** Unity/LFS startup hook. After it is confirmed working, bootstrap with `CS BOOTSTRAP`, verify LFS, install Unity Editor and authorize license independently. Never equate a machine's `Available` state with a functioning Ubuntu Editor. The broken Codespace remains retained but is stopped after successful creation.

## Alpine recovery on the newly selected account (2026-11-09)

If the selected SSH Codespace returns Alpine (Codespaces recovery environment) without Python, use owner-only `CS RECOVERY SYNC` for the exact `aaris-ai-unity-ssh-r7x67xqjg4qphgx6` machine to fast-forward a clean devcontainer configuration to the reviewed `main` through Bash/git without touching modified source assets. Then `CS FULL REBUILD` runs the official full GitHub Codespaces rebuild after an Alpine-compatible read-only preflight. Follow with Ubuntu/LFS/Editor/license checks. Refuse to claim that a GitHub state of `Available` is proof Unity works. Never copy old account credentials or discard dirty assets.

## New account Codespaces recoverability (2026-11-09)

After switching `CODESPACE_PAT` to `tomaaaris1234-pixel`, `aaris-ai-unity-ssh-r7x67xqjg4qphgx6` was created and START succeeded, but `CS STATUS` reported Alpine 3.23.8 and `editor_installed=no`, even after a regular rebuild. Treat this as non-working Unity setup (possible Codespaces recovery container), not editor success. In response, startup apt/GUI/LFS/Unity installation steps are now allowed to defer transient failures so Ubuntu SSH can remain accessible. The explicit `CS FULL REBUILD` is available for the selected, preflight-verified Codespace; it clears image cache without touching persistent `/workspaces`. After rebuilding, verify OS, LFS and Unity separately; never claim successful rendering without a real Editor scene screenshot or Unity license verification. The old owner's 210 modified files remain protected.

## Hosted GitHub Actions rendering pipeline (separate from Codespaces)
- Primary workflow: .github/workflows/aaris-unity-photographer.yml. Main branch pushes trigger it. workflow_dispatch accepts intent=all, houses, roads, tiles or sky.
- Unity executes Aaris.MapReview.Editor.AarisCICapture.Capture. It opens the checked-in native scene and captures genuine HD PNGs. No -nographics; Xvfb plus Mesa and a real graphics device required.
- Photos and photo-audit.json are uploaded to Actions artifacts and live-photos Release. Black/white/flat images are diagnosed; no success claim for unverified visuals.
- Cache release tags: engine-cache for the compatible Library, project-checkpoint for changed project assets. Archives are SHA-256 verified and individual assets are kept under 2 GiB.
- Never publish credentials, license folders or the Git working database. Library is an expendable import cache, not a source-of-truth snapshot.
- Jobs are ephemeral and limited to 6h maximum. Periodic saves plus if: always() are best-effort; hard termination/cancellation can bypass final cleanup. Persist final project changes in Git.
- CI cannot be a permanently reachable interactive Unity Editor screen. It can automate Editor operations with intent commands; no exposed VNC server or Codespace needed.
- Reproducible cold starts depend on Docker image availability, license, LFS access, disk, cache and network; 1-2 minutes is not guaranteed.
- Do not overwrite real terrain, mesh, scene, prefab, material or .meta files just to make screenshots pass. Capture errors require investigation.
- Test Python checkpoint with: python Tools/CI/session_store.py selftest. Treat Unity activation as a separate required dependency; only use configured GitHub Secrets, never print them.

## 2026-11-06: Separate production pipelines (authoritative)
- **Python (always license-free):** .github/workflows/python-map-operations.yml runs blueprint auditing on relevant pushes; workflow_dispatch supports audit, inspect and review-only material sync. It never blocks on Unity credentials. Source checker: Tools/CI/map_static_check.py. Its PASS is a data/geometry consistency result, not proof that URP renders correctly.
- **Rendering (requires valid license):** .github/workflows/aaris-unity-photographer.yml probes UNITY_LICENSE in a masked step. Missing license SKIPS the expensive photo job while its preflight remains green. UNITY_EMAIL and UNITY_PASSWORD may enable the optional GameCI activation action; these are Unity ID credentials, never Gmail credentials. A ULF from an authorized activation remains essential for Personal.
- **Security:** Never publish .ulf, OAuth tokens, service-account keys, Google credentials or any credential to a public GitHub Release, artifact, cache, Git commit, or logs. No spoofed machine IDs or unapproved automation of licensing.
- **Ops reference:** Docs/UNITY_CI_SECRETS.md contains the three Secrets, mobile setup, limitations and verification links. The Unity CLI service-account flow does not activate Unity Personal; no guaranteed perpetual login on ephemeral hosted runners.

## License-free visual fallback
- Workflow `.github/workflows/python-map-operations.yml` generates five 1920x1080 coordinate-accurate **schematic** map review PNGs from `Docs/MAP_LAYOUT.json` on relevant pushes and dispatches.
- Download from `https://github.com/sundamunda289-art/Unity-Game-Aris/releases/tag/layout-review` or the Actions artifact. Each diagram is labelled **NOT A UNITY RENDER**; never interpret these as evidence that Unity houses, skybox, colors, trees or URP shaders render correctly.
- The schematic workflow stays independent of Unity login, GameCI, `.ulf` files and runner identity. Its outputs can be regenerated on any fresh runner.

## Cloudflare email-protected live Unity session
- Workflow: .github/workflows/aaris-live-unity.yml starts on-demand Xvfb + noVNC through a Cloudflare Quick Tunnel restricted with --allowed-mail, using the UNITY_EMAIL GitHub Secret.
- Local Python camera API: Tools/CI/live_camera_server.py. Editor-only bridge: Assets/AarisWorld/Editor/AarisLiveCameraBridge.cs. Numeric requests cannot execute shell commands or save scene assets.
- No valid UNITY_LICENSE means an accessible Linux desktop, not a functioning Unity Editor. Unity Personal can require interactive Unity Hub activation on a fresh machine.
- Phone guide: Docs/UNITY_LIVE_CLOUDFLARE.md. Quick Tunnel sessions are temporary and cannot supply persistent cloud computing or a stable hostname.

## Free Cloudflare camera hub / GitHub Issue commands
- Persistent read-only Cloudflare hub: https://aaris-unity-live-hub.aaris-game-control-6f9c.workers.dev
- Camera control GitHub issue #77: https://github.com/sundamunda289-art/Unity-Game-Aris/issues/77
- While an explicit live GitHub Actions session runs, Tools/CI/issue_camera_bridge.py polls for CAMERA x y z yaw pitch distance comments from repository owner sundamunda289-art only. It can upload actual Editor PNGs to the public live-photos Release, and responds with immutable PNG links.
- Cloudflare hub has no KV/R2 writes and no paid components. It cannot run Unity; GitHub Actions hosts the temporary desktop. Live view requires email PIN; Unity Editor itself needs authorized Unity licensing.
- ChatGPT can request and read snapshots via the GitHub connector, but cannot receive a continuously streaming video in this chat. See Docs/UNITY_LIVE_CLOUDFLARE.md.


## Verified intent-driven control and license boundary
- Post START_DESKTOP 15, START_DESKTOP 45, or START_DESKTOP 90 as the
  sundamunda289-art repo owner on GitHub Issue #77; ChatGPT with the linked
  GitHub app can post this owner-authorized command. It triggers a temporary
  GitHub Actions desktop without running any phone commands.
- The runner advertises its PIN-protected temporary Cloudflare Tunnel URL in
  a bot issue comment; the stable free Cloudflare Worker hub discovers it.
- Post CAMERA x y z yaw pitch distance on the same issue. The runner polls
  and responds with an actual Unity PNG link only after successful Editor
  camera rendering; otherwise it returns a clear error.
- Test run 42239962330 confirmed start/issue relay; camera request 6688056483
  returned a license-not-available timeout. Real Unity render remains BLOCKED.
- Do not infer that the live desktop is Unity itself without valid activation.

## Fresh-runner Unity Personal activation attempt (2026-11-06)
- Authorized bootstrap: Tools/CI/unity_personal_bootstrap.py. The live workflow uses the pinned GameCI container and tries a legitimately issued UNITY_LICENSE ULF, or official Unity CLI OAuth login followed by "unity license activate --personal --accept-eula --non-interactive", then the actual GUI Unity Editor and initial camera framing.
- For any OAuth request, the one-time Unity sign-in URL is stored only in ci-live-control/license-status.json inside the ephemeral runner, served on the protected Cloudflare noVNC page via /api/license, and never posted publicly or archived. The user may need to approve via phone each new runner. Unity Cloud service accounts and passwords do NOT automatically activate Personal.
- Only sanitized, predefined license states can be reported to GitHub issue #77; OAuth URLs and passwords are never included. The stable Cloudflare Worker hub displays those states.
- Restore only Unity Library import cache with actions/cache@v4; this cache must not contain the license, Hub OAuth state, cookies or other credentials. A cold Docker pull and Git LFS hydration still cost time.
- The CLI path is a documented activation attempt, not a proven entitlement for a new runner. Official Unity 6000.3 Personal docs say Hub sign-in is the supported activation method. Failure is signaled, not bypassed. Do not spoof Machine IDs or fake screenshots.


## Assistant-triggered license retry (owner-only, no keyboard on phone)

A connected GitHub assistant (authorized as repo owner sundamunda289-art)
can now post these exact commands on GitHub Issue #77:
- ACTIVATE_UNITY 15 — start a fresh 15-minute Actions desktop and attempt
  authorized Unity Personal activation (also 45 or 90).
- ACTIVATE_UNITY — retry the official Unity activation process inside the
  currently RUNNING pinned GameCI Unity container; do not replace its host.
- CAMERA -420 22 -210 0 20 22 — move camera if the real Editor is licensed.

The issue relay allows ACTIVATE_UNITY only for owner-authored comments;
it executes a constant, bounded Docker command, not arbitrary shell input.
The container remains alive after an unsuccessful activation so a retry can
be attempted without downloading the same Editor again in that session.

**Important**: An explicit ACTIVATE_UNITY command cannot bypass Unity's
license entitlement, browser sign-in or MFA requirements. It works without
the user's intervention only when a valid authorized ULF / active authorized
session is available in that new container. For Unity Personal, GameCI
documents one-time Hub activation -> ULF -> UNITY_LICENSE GitHub Secret.
There is no supported ALF-to-ULF Personal manual activation at present.
Unity Cloud Service Account credentials alone do not grant an Editor
Personal license. No machine identity spoofing or token caching is used.

Verified repo-level unit test for command handler and the workflow's
issue_comment path ran in GitHub Actions. An actual successful Unity
license activation and true scene PNG have NOT yet been verified.


## 2026-11-07: AI developer hands and eyes (one validated command protocol)

**The canonical GitHub repository is now `sundamunda289-art/Unity-Game-Aris`.** Older owner/repository links may redirect. Use `AGENTS.md` (plural), and never assume that a public dashboard showing Linux proves the Unity Editor was licensed.

**Read-only discovery first.** A GitHub-connected AI should inspect `Docs/MAP_LAYOUT.json` and use the owner-authorized GitHub Issue [#77](https://github.com/sundamunda289-art/Unity-Game-Aris/issues/77) to request a *real* Unity Editor response. Only comments authored by `sundamunda289-art` are processed by the live runner, while a GitHub Actions run is active. A connected AI must use an account/connection authorized as that owner; posting as a different account will not execute the command. Do not broaden this authorization to arbitrary GitHub users.

There are only three operational layers; do not create a separate script per object type:

1. `Tools/CI/ai_world_commands.py`: validates strict owner-issued `MAP {JSON}` intents, exact fields, finite bounded coordinates, object paths, and allowed operations. `python3 Tools/CI/ai_world_commands.py` runs its license-free selftest. No shell or C# execution is accepted in JSON.
2. `Assets/AarisWorld/Editor/AarisAIWorldBridge.cs`: Editor-only adapter; finds unique scene GameObjects, returns hierarchy path, world position/rotation/local scale, world-space renderer/collider bounds (metres), material names, and sampled mesh vertices. It can move/rotate/scale a selected Transform, or inspect/move/scale one native TerrainData tree instance by terrain name and zero-based index. It saves changed scenes/TerrainData through Unity's asset APIs. `blueprint` and `tree_blueprint` yield world-coordinate **PLY mesh-vertex samples** and JSON. Samples are not a watertight volume, a CAD mesh, material texture capture or a full topology reconstruction. Photo requests use a genuine Unity graphics device and reject nearly uniform blank images.
3. `Tools/CI/issue_camera_bridge.py` + `.github/workflows/aaris-live-unity.yml`: owner-only issue relay, timestamped status, public map JSON/PLY/photo Release links, PIN-gated cloud desktop, git-branch saving and best-effort verified session checkpoint. `Tools/CI/session_store.py` handles resumable project deltas, not Unity credentials.

**Supported commands** — one line per comment on Issue #77, with JSON keys/numbers exactly as shown. Start a session first, for example `START_DESKTOP 45`; allowed durations are 15, 45, 90, 240, and 300 minutes. `ACTIVATE_UNITY` can retry only an authorized activation, never bypass it.

```text
MAP {"op":"find","target":"Tree"}
MAP {"op":"nearby","x":-420,"y":20,"z":-210,"radius":40}
MAP {"op":"inspect","target":"Root/House/Wall"}
MAP {"op":"blueprint","target":"Root/Tree","samples":1024}
MAP {"op":"photo","target":"Root/House"}
MAP {"op":"move","target":"Root/Tree","x":2,"y":0,"z":-3}
MAP {"op":"rotate","target":"Root/Car","x":0,"y":15,"z":0}
MAP {"op":"scale","target":"Root/House/Wall","x":1.15,"y":1,"z":1}
MAP {"op":"tree_nearby","terrain":"Terrain","x":-420,"y":20,"z":-210,"radius":40}
MAP {"op":"tree_inspect","terrain":"Terrain","index":12}
MAP {"op":"tree_blueprint","terrain":"Terrain","index":12,"samples":512}
MAP {"op":"tree_photo","terrain":"Terrain","index":12}
MAP {"op":"tree_move","terrain":"Terrain","index":12,"x":2,"z":-3}
MAP {"op":"tree_scale","terrain":"Terrain","index":12,"width":1.25,"height":0.9}
CAMERA -420 22 -210 0 20 22
MAP_SAVE
```

- First run `find`/ `nearby`, then `inspect` for the exact, uniquely identified hierarchy path. Non-unique object names fail safely. `move` uses **world-space metre offsets**; `rotate` uses **degrees**; `scale` multiplies the selected object's **local** X/Y/Z scale by the factors. For a wall's thickness, select the **individual wall child**, not its whole house; for foliage select the leaf mesh child. Confirm the axis/material/mesh before editing. An edit to a prefab instance Transform does not rewrite its source prefab.
- Native TerrainData trees are **not ordinary GameObjects**. Use `tree_nearby` at map coordinates to discover the correct Terrain tree index and prototype, then `tree_inspect` for measured world-space 3D bounds before editing. Identify the correct Terrain and the tree index. `tree_move` uses metre offsets constrained to that Terrain tile; `tree_scale` multiplies width and height independently. This is per-instance; it does not edit shared leaf/trunk source mesh geometry. A PLY of a terrain tree is sampled from its prototype mesh and transformed approximately into world coordinates.
- All map outputs are **discrete** command responses; chat models do not receive continuous screen video. Results are uploaded to the public `live-photos` Release as `ai-world-<comment-id>.json`, `.ply` or `.png` and linked in the issue response. Treat JSON/PLY as data and PNG as a capture to visually verify; screenshot size/variance checks alone do not prove scene quality.
- Edits immediately save to the runner's native Unity scene or TerrainData. Run owner-authorized `MAP_SAVE` after a group of related edits to publish a **reviewable Git branch and PR**, preserving LFS filters. Do not merge automatically; review file diffs, LFS pointers, placement, collision and screenshot evidence first. No change to the source project is inferred from a schematic `Docs/MAP_LAYOUT.json` preview.
- For an interrupted run, a background checkpoint and final `if: always()` step attempt to save changed project assets to the checksummed `project-checkpoint` Release. A fresh runner checks out and hydrates source LFS, then restores the checkpoint **only if its source Git commit SHA matches**. If a Git PR has been saved, the durable edit lives on that branch until reviewed/merged; the checkpoint is not a replacement for merging it. A hard runner kill may interrupt checkpointing. Unity's `Library/` cache is only an import accelerator; no license/password/HUB state is stored.
- For failures, check `aaris-ai-diagnostics` Actions artifact (`issue-camera-relay.log`, `ai-result.json`, cache logs), the job summary, and the PIN-protected `/api/license` page. Never paste private OAuth URLs or license files into issues. If Editor did not activate, output an explicit failure rather than claiming screenshots or edits occurred.
- Keep object edits narrow. Avoid unnecessary modifications to `.meta`, materials, prefabs or LFS scenes. Do not auto-commit generated files from `Library/`, `Temp/`, `ci-live-control/`, license locations or private folders. Validate the actual Editor compilation/scene before declaring the automation production-ready.


## 2026-11-07: Owner-only AI terminal for ephemeral Actions runners

**Purpose:** You do not need Codespaces, Termux, or direct SSH to find out whether the
GitHub Actions computer is ready. Once a fresh live workflow run has started, an
AI connected as `sundamunda289-art` may comment on [Issue #77](https://github.com/sundamunda289-art/Unity-Game-Aris/issues/77)
and read back **sanitized live computer facts**. The bridge lives in
`Tools/CI/terminal_control.py`; the relay is `Tools/CI/issue_camera_bridge.py`.

Exact accepted commands (one comment per request):

```text
TERM PING
TERM HELP
TERM STATUS
TERM DISK
TERM PROCESSES
TERM EDITOR
TERM LICENSE
TERM ERRORS
TERM NETWORK
TERM LFS
TERM HISTORY
TERM ALL
```

- `TERM STATUS`: Editor/license/scene/photo status, last known compiler/error categories,
  free disk. `TERM DISK`: workspace/tmp/root free space and memory/CPU.
  `TERM PROCESSES`: bounded known process counts and Docker/Editor exit info.
  `TERM ERRORS`: only safe compiler error IDs, exception classes and error categories.
  `TERM NETWORK`: localhost VNC/API port checks. `TERM LFS`: checks native
  scene hydration and LFS count. `TERM HISTORY`: recent 30-second sampling.
  `TERM ALL`: comprehensive bounded state. `TERM PING`: confirm relay alive.
- Every owner-authorized command returns `AARIS_TERMINAL #<comment-ID>` with a
  machine-readable JSON response from the **actual current runner**. The output
  is PUBLIC on this GitHub issue; OAuth URLs, email addresses, authentication
  tokens, actual CLI arguments and raw Unity logs are never echoed.
- New sessions also write a sanitized 30-second health timeline to
  `ci-live-control/terminal-timeline-safe.jsonl`; the file is bundled into the
  `aaris-ai-diagnostics` Actions artifact when the job finishes.
- This is an allowlisted read-only terminal, **not general shell access**.
  Never add `bash -c`, arbitrary Docker exec from comments, paths supplied by
  users, raw `tail`/log dumps, or full shell access over public issues.
  To retry authorized licensing, use the existing distinct `ACTIVATE_UNITY`
  command; it cannot bypass Unity Personal entitlement or approvals.
- `editor_starting` means the GUI process was invoked, not that Unity Editor
  imported a scene. Require a real Editor heartbeat, successful map command,
  or genuine PNG capture to claim usable Unity Editor status. A running Docker
  container and a green verify job alone are not enough.
- CI regression test: `python3 Tools/CI/terminal_control.py --selftest`.
  `TERM` commands are supported only in sessions created *after* the changes
  are merged; an already running runner uses its old checked-out code.

## Private direct Unity approval (no Cloudflare email OTP)

When a trusted live GitHub Actions run requests Unity CLI OAuth, the background
`Tools/CI/private_unity_approval_publish.py` sends the short-lived,
Unity-owned `auth_url` to a **separate** Cloudflare Worker using an **OIDC
JWT signed by GitHub**, scoped to this exact repo, workflow, branch, and actor.

- Worker: `aaris-unity-approval-relay.aaris-game-control-6f9c.workers.dev`.
- Worker source: `Tools/CI/cloudflare_private_approval_worker.mjs`.
- Storage: private Cloudflare Workers KV namespace with 480-second expiry;
  public Worker endpoint can **write only when a valid signed GitHub Actions
  OIDC token is verified**. Public GET only exposes `/health`, never auth_url.
- The connected Cloudflare app, authenticated to the user's Cloudflare account,
  is the only available assistant path to read the private namespace via the
  Cloudflare account API and deliver the actual `https://id.unity.com/...`
  link directly in **this private ChatGPT conversation**. The URL does **not**
  go into GitHub public Issues, Actions artifacts, releases, logs, or commits.
- After retrieving the per-run URL, open the **official Unity** webpage from
  the phone and select the necessary Sign in / Allow. No Cloudflare email OTP
  is needed **for that direct Unity approval link**.
- This does not remove the separate email-PIN gate from the full VNC/desktop
  tunnel, which remains protected. It does not bypass Unity account login or
  Unity Personal licensing requirements.
- The bridge writes only a non-sensitive status to
  `ci-live-control/approval-bridge-status.json`, uploaded with diagnostics.
  Worker trust pins immutable GitHub repository/actor IDs and verifies RS256,
  issuer, audience, branch, workflow and timestamps.
- An assistant using the connected Cloudflare account can read KV value under
  key `run-<GITHUB_RUN_ID>` and return the genuine link privately. Delete or
  allow the key to expire after approval. Do not paste it into Issue #77.


## 2026-11-07: GitHub Codespaces AI terminal and screenshots (optional)

New persistent backend: `Docs/CODESPACES_AI_CONTROL.md`, `.github/workflows/aaris-codespaces-control.yml`, `.devcontainer/`, and `Tools/Codespaces/`. This does not replace the existing GitHub Actions world/photographer pipeline.

- As GitHub-linked repo owner `sundamunda289-art`, send one exact owner-only issue comment to Issue #77: `CS MACHINES`, `CS START`, `CS STATUS`, `CS SYNC`, `CS INSTALL`, `CS EDITOR`, `CS LICENSE`, `CS SHOT`, `CS TEST` or `CS STOP`.
- Each comment triggers the dedicated Actions controller (after merge to main). Read the returned issue comment and per-run artifact `aaris-codespace-<run-id>` for bounded terminal output, JSON status and optional PNG. This is not streaming SSH access for the chat.
- `CODESPACE_PAT` is a GitHub Actions Secret switched from Termux after GitHub HTTPS device authorization. The master repository remains unchanged when the Codespace account rotates; never add secrets, PAT, Unity login material, or machine IDs to source or public logs.
- Run `CS MACHINES` before `CS START` to inspect machine availability. `CS STATUS` probes actual CPU, memory, free disk and Editor process. `CS SYNC` hydrates LFS without resetting unsaved native Unity files.
- New Codespaces auto-install Xvfb/tools from `.devcontainer`; Unity Editor 6000.3.26f1 is installed separately on demand to `/tmp/aaris-unity-runtime/editors` and the Library/Temp import cache is symlinked onto /tmp where safe. Editor/cache may be erased by a Codespace restart; source Git/LFS remains authoritative. The project resides in `/workspaces/Unity-Game-Aris`; logs/screens in temporary `/tmp/aaris-codespaces`.
- Do not infer a licensed Unity Editor or real scene render from process launch or nonblank screen alone. Unity Personal still needs authorized Unity Hub sign-in, especially on a different Codespace. Never spoof machine-ID or clone OAuth/license files.
- Only checked-in task IDs can run through issue comments. No arbitrary shell, `eval`, user-provided paths, credential echoes or unsafe background command execution. New tasks require code review and static tests.

## 2026-11-07: Codespace installer remediation
- The optional Codespace Editor installer now uses the official Unity CLI via `Tools/Codespaces/install_unity.sh`; Hub CLI headless commands are deprecated. `CS LFS REPORT` is a read-only diagnostic command with sanitized pointer counts and failure kind. Preserve original LFS pointer SHA-256 and asset bytes; do not replace missing assets with generated surrogates. Genuine Unity Personal authorization may still need a one-time phone approval in the *current* Codespace. Do not print the OAuth URL on the public GitHub issue.

## 2026-11-07 Fast Codespace Resume
- One-time Editor install to persistent `~/.local/share/aaris-unity-editors/6000.3.26f1` using Unity CLI's supported portable `--download-directory` option; the Editor is reused on same Codespace stop/start.
- On restart, `CS START` then `CS EDITOR`, followed by `CS STATUS` and `CS SHOT`. No reinstalls when persistent Editor is available. Unity Library/Temp cache remains on /tmp to avoid running out of persistent disk; import may take longer after ephemeral storage reset.
- New account/new Codespace has its own storage and may require the initial Editor download and official Unity Personal login. Never move authentication cookies, license state, or machine IDs between Codespaces.

## Unity Personal phone approval on existing Codespace

- `CS AUTH` from owner Issue #77 runs Unity CLI browser sign-in on the actual Codespace; an Actions bridge intercepts the Unity-owned authorization URL and publishes it only to the protected Cloudflare OIDC relay.
- Retrieve with connected Cloudflare KV `aaris-unity-approval-private`, key `run-<Github Actions run id>`. Give the URL ONLY privately in ChatGPT, never in a GitHub public Issue, log, artifact or commit.
- User opens official Unity page on phone, signs into Unity (Google/Gmail if configured), taps Allow. Then the automation checks Unity login and attempts Personal entitlement activation, verifying final license status on the Codespace itself. Hub activation fallback may be required.
- User interaction is required when OAuth asks; no reuse of hardware IDs or login cookies across accounts.


## Simplest Unity Personal approval on Android without Cloudflare

The owner's Termux can run Tools/Codespaces/termux_unity_allow.sh, which reuses the saved GitHub Codespace account and opens the genuine Unity-owned, Codespace-bound OAuth URL directly in the phone browser via termux-open-url. The remote reviewed mobile_license.py attempts official Unity Personal activation and checks status after Allow. Do not invent generic authorization URLs, share credentials through GitHub Issues/Actions, or clone Unity auth state across machines. Use this method in preference to the optional Cloudflare CS AUTH relay when that relay fails.

## 2026-11-07: GitHub encrypted OAuth fallback
- If `workers.dev/health` is blocked by provider access policy, `CS AUTH` publishes only RSA-OAEP-SHA256 encrypted Unity URL chunks in Issue #77 using the GitHub-provided token, never a plaintext Unity authorization link.
- The private key is not on GitHub, in the repository, or in Codespaces. Only decrypt privately with the matching key and deliver the genuine URL in private ChatGPT. The current key may be ephemeral; regenerate public/private pair together if the private key is unavailable next time.

Unity CLI and the re-usable Editor now use the Codespace user's writable home directory, because the /workspaces root can reject writes. The original Unity project stays under /workspaces/Unity-Game-Aris. /tmp is only for transient caches and downloads.


## 2026-11-08: Evidence-first native scene screenshot and Play Mode (no noVNC dependence)

**Canon:** `Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity` is the saved Unity 6000.3.26f1 / URP native source; four 1km TerrainData tiles. The new `Assets/AarisWorld/Editor/AarisCodespaceSceneProbe.cs` and `Tools/Codespaces/scene_probe.sh` are a bounded *read-only* Editor pipeline, separate from `CS SHOT` (desktop screenshot). The Unity code opens this exact scene with `EditorSceneManager.OpenScene`, verifies four hydrated TerrainData references, and **renders an overhead camera directly to PNG using `Camera.Render` + `RenderTexture`**, independent of which Linux window is visible.

### Owner-only commands and order
Post only as `sundamunda289-art` to [Issue #77](https://github.com/sundamunda289-art/Unity-Game-Aris/issues/77), after the PR is merged and `CS SYNC` succeeds:

1. `CS STATUS`, `CS LFS REPORT`, `CS CHANGES` to establish machine state, real LFS hydration and preserve local edits.
2. `CS CLOSE EDITOR` requests a graceful close of any existing GUI Editor. If it reports a pending dialog, run `CS EDITOR PROCESS`; `CS EDITOR RECOVER` is permitted only when **zero Unity Editor windows are visible**, targets the exact project-owned process, and issues SIGTERM (never SIGKILL). Do not indiscriminately kill processes, clear Unity cache or reset files. When no editor process remains, proceed.
3. `CS SYNC` fast-forwards to the tested main commit without overwriting real project edits. If blocked by genuine local changes, stop and review them first.
4. `CS SCENE RENDER` launches a **separate Unity batch Editor with an actual Xvfb/Mesa graphics device, no `-nographics`**, imports/compiles scripts, opens the real native scene, sets a temporary orthographic camera at world (0,1600,0), renders the full 2×2km map into a 1600×1600 `map-overview.png`, then verifies the file's PNG header, size, pixel variation, magenta-failure proportion and native scene metadata. It does not save or modify the scene.
5. Only after the real render passes, run `CS SCENE PLAY`. A fresh batch Editor loads the same native scene, enters Unity Play Mode, checks a minimum of 60 frames, four terrains, player, game camera, vehicle manager and zero reported runtime errors, then captures a real `play-frame.png`. It exits Play Mode and reports whether the smoke test passed. This is **not** an exhaustive physics/QA or device-fidelity test.

The controller transfers only the verified files to a two-day GitHub Actions artifact `aaris-codespace-<run-id>`: `map-overview.png` / `render-report.json` or `play-frame.png` / `play-report.json`, with sanitized `result.json` and `terminal.txt`. The raw Unity log, license dialogs, OAuth URLs and credentials stay in the Codespace's private `/tmp/aaris-codespaces/scene-probe/`. GitHub Issue comments contain only allowlisted status codes and counts. This pipeline **does not need noVNC**; Xvfb is only an offscreen OpenGL display. A screenshot of the Ubuntu desktop is never evidence of a loaded Unity map.

**Fail closed** when another Editor holds the project lock, a source scene is a Git LFS pointer, Unity licensing is not activated, four terrains or runtime components are absent, the Editor fails to import/compile, rendering is blank/magenta, a probe exceeds its deadline, or Play Mode logs an exception. Static Python regression checks do not prove the Editor rendered. Do not claim success without a passing Unity batch report **and inspecting the actual PNG visually**. If a user needs interactive GUI inspection, use the separate owner-approved noVNC pathway; never remove its access gate.

**Source safety:** No Unity scene, TerrainData, mesh, material, prefabs or generated `Library/` assets are staged for these inspection operations. Only approved code changes go through review PRs. If Unity auto-migrates `ProjectSettings` during import, preserve and examine those changes separately; never silently push them. On unexpected failures, classify from `CS EDITOR DIAG` and `CS COMPILE REPORT` without publishing raw logs, and fix the genuine issue before re-running.


### AI-controlled coordinate photographs: any point, any viewing direction

The camera is no longer fixed to the four tile overviews. For **any saved world position within X/Z = -1000 to 1000 metres** the owner can request:
- `CS PHOTO -420 -210` — automatically frames the point at its actual TerrainData elevation, using the default 35° yaw / 30° pitch / 85 m distance.
- `CS PHOTO 350 220 90 35 85` — X, Z, **yaw in degrees**, **pitch above horizontal in degrees**, **camera distance in metres**. Valid ranges: yaw -360…360°, pitch 12…75°, distance 20…450 m. Camera X/Y/Z and the framed target X/Y/Z are written to the JSON report; Y is computed from saved terrain elevation.

The controller accepts only these two exact numeric forms on Issue #77. An allowlisted Python parser (`Tools/Codespaces/photo_request.py`) rejects extra arguments, shell metacharacters, non-finite numbers and map-out-of-range coordinates. An AI can therefore ask for new camera vantage points repeatedly without editing source for each picture.

`CS PHOTO` uses the same isolated Unity batch-mode Editor as the full overview capture (close existing Editor process first, `CS SYNC`). It loads the full native LFS scene, determines ground level from real TerrainData, optionally centers framing on a nearby building/tree renderer, raises the camera over terrain that would block line of sight, and renders **both** `photo-detail.png` and `photo-context.png` (wide surrounding landmarks). `photo-report.json` stores camera placement and render variance. All three outputs appear in the GitHub Actions `aaris-codespace-<run-id>` artifact. If either camera frame is blank/magenta, the real Unity Editor cannot run, or the report/PNG is invalid, the command must fail; do not replace these outputs with generated or stock imagery.

For AI visual inspection, begin with a context frame to locate roads, houses, vegetation and terrain, inspect its detail frame, then change yaw/pitch/distance through another `CS PHOTO` command to examine hidden sides. A successful still photo proves rendering from that position, **not** collision correctness or Play Mode health; run `CS SCENE PLAY` separately. The scene is never saved by a photo operation.


### 2026-11-08: Isolated worktree recovery when source contains real Unity changes

**Do not run repeated `CS SYNC` / Git resets against a dirty long-running Unity GUI project.** This incident recorded `changed_files_count=210`, `raw_different_count=210` (many generated/modified model `.mat` files). The guarded sync correctly blocked because they may contain genuine user work; do not erase or stash arbitrary user assets.

The `CS PHOTO`, `CS SCENE RENDER` and `CS SCENE PLAY` commands now use an **immutable, commit-keyed, isolated Git worktree** at `/tmp/aaris-native-photo-worktrees/<40-character-origin-main-SHA>`. The reviewed remote script fetches `origin/main` and checks out this new snapshot with LFS smudge disabled, then runs `git lfs pull` in the isolated directory. The original `/workspaces/Unity-Game-Aris` stays unchanged and retains every dirty Unity import/material edit. Each subsequent camera request for the same commit reuses the isolated checkout and its Unity `Library` cache. A new main commit receives its own new path instead of overwriting another snapshot.

**Safe photo sequence now:** `CS EDITOR PROCESS` then controlled `CS CLOSE EDITOR` and `CS EDITOR RECOVER` only when no windows remain; then `CS PHOTO X Z [YAW PITCH DISTANCE]` without requiring a risky `CS SYNC`. The remote script will fetch the clean main snapshot itself, check LFS hydration and only run the Unity batch process when no conflicting Editor is active. Take screenshot PNG from its Actions artifact and inspect it visually. If either the photo or report fails, diagnose the genuine error. **No force cleanup of the live source or arbitrary LFS mutations.**

The isolated worktree is a read-only *source baseline*, not a copy of unsaved GUI changes; do not say the screenshot includes those local edits. The isolated location is under `/tmp` and may be lost when Codespace host storage is recycled, so project source remains Git authoritative.

### 2026-11-08: Play Mode failure classification

The real native `CS PHOTO -420 -210` run 42439901782 succeeded in Unity 6000.3.26f1 with OpenGLCore and 4 TerrainData tiles; its two actual Unity PNGs are stored in its GitHub Actions artifact. Visual inspection found bright electric-blue near-range foliage in the detail image, versus mostly green distant foliage in the context image; treat this as a **suspected near-LOD/material/shader issue** requiring controlled diagnosis, not a verified cause or an invitation to recolor native assets blindly.

`CS SCENE PLAY` run 42440615214 returned a nonzero Editor exit. For future investigation, use the owner-only Issue #77 command `CS PLAY DIAG`, which reads only the private Codespace `/tmp/aaris-codespaces/scene-probe/play-report.json` and classifies a bounded set of Unity Editor log signatures. It does not publish raw logs, user paths, passwords or OAuth details. Fix each actual Play failure and rerun until both the Play report and Unity camera PNG are real, then inspect them visually. Do not claim Play verified based on still-image success.

### 2026-11-08 native foliage LOD/shading A/B investigation

Latest visual evidence: `CS PHOTO 350 220 90 35 85` produced two genuine Unity 6000.3.26f1 OpenGL captures (run 42441044655). Closer SpeedTree 3D foliage is nearly black, while the wider context shows many yellow/green distance LODs. **Do not assume the same render materials are correct at both distances**. Root cause remains to be established before permanently rewriting any of the 775 LFS-backed Unity source assets.

Issue #77 owner-only `CS FOLIAGE DIAG` runs `AarisCodespaceSceneProbe.DiagnoseFoliage` in a clean immutable LFS-hydrated Git worktree. It renders the same camera from coordinate (350,220) three times: original foliage, a strongly biased distant LOD, and full-LOD with temporary ambient lighting and disabled shadows. Download `foliage-original.png`, `foliage-far-lod.png`, `foliage-ambient-test.png` and `foliage-report.json` from the corresponding Actions artifact. If only the latter improves, investigate lighting/shadows. If only the distant LOD improves, investigate SpeedTree 3D branch shadergraph and materials. **All preview changes are in memory** and are restored in a finally block; they must never modify the saved scene, terrain, meshes or materials. Static CI is not proof of native rendering.

The Play Mode probe also stores an allowlisted `runtimeErrorKind` derived from Unity's first runtime error without publishing the original message, and the shell reports its category if Play Mode exits nonzero. Only the current mode's diagnostic PNG/JSON files are deleted on entry, so taking a photo cannot erase the last Play Mode report. A repaired game must pass the real 60-frame Play Mode test and produce `play-frame.png` before gameplay is declared healthy.

**Git LFS source updates:** after visually proving a specific fix, edit the actual source file at its existing path and verify both `git lfs ls-files` and the newly hydrated asset's content. Update Git LFS pointer OID through a regular reviewed Git commit; keep old historical objects available for recovery. Never mass-delete old TerrainData, change `.meta` GUIDs, or discard the original live Codespace's 210 uncommitted Unity changes.


### 2026-11-08: Verified dark foliage evidence, isolate actual lighting cause

Action #42442605207 produced three original Unity Editor 6000.3.26f1 OpenGL camera images at exactly X=350, Z=220: `foliage-original.png`, `foliage-far-lod.png`, and `foliage-ambient-test.png`. The near trees are dark in original. The **same full 3D tree shapes** are realistic yellow-green when using Flat ambient lighting (0.69/0.70/0.65, 1.2x) together with globally disabled shadows. In the top 55% of the images, black pixel fractions (<55 RGB in every channel) measured ~12.44% original, ~0.76% far-LOD (severely simplified trees), and ~0.85% with ambient + shadows-off **at full LOD**. This proves native URP lighting can recover tree color without replacing the original LFS SpeedTree mesh or chopping all tree branches. It does **not** yet prove whether ambient alone or shadow removal alone is necessary.

The next `CS FOLIAGE DIAG` separates the two factors and returns five genuine PNGs: original, forced far LOD, **ambient-only with normal shadows enabled**, **shadows-only with original ambient**, and both together. Only select a permanent lighting fix after visual comparison. Preserve shadow realism if ambient-only suffices; do not permanently disable shadows or replace LFS TerrainData unnecessarily. After saving the source scene through a minimal reviewed change, re-render with the ordinary `CS PHOTO 350 220 90 35 85` command to ensure default quality is fixed and repeat at a second coordinate (-420,-210) to check the earlier electric-blue problem.


### 2026-11-08: Minimal saved source lighting candidate (pending separate A/B proof)

The actual native scene `Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity` is a **regular Git YAML scene**, not a Git LFS blob; the native TerrainData and SpeedTree `.st` model/texture payloads are Git LFS. Do not overwrite the LFS TerrainData merely to fix a lighting issue. After comparing full-detail black trees with actual Unity A/B render 42442605207, a minimal **shadows-preserving Flat ambient** candidate was prepared: `RenderSettings.m_AmbientMode=3`, `m_AmbientIntensity=1.2`, and `m_AmbientSkyColor=(0.69,0.70,0.65,1)`. Only three lines of the saved scene change, with no terrain, tree prototype, mesh, `.meta` GUID, road, vehicle or source object change. The offline `Tools/MapBuild/world_review.py` generator now writes the same values. `Tools/Validation/check_saved_ambient.py` asserts those fields and preserved sun/sky references.

**Important:** This candidate must remain **draft/unmerged** until the 5-frame `CS FOLIAGE DIAG` experiment proves `foliage-ambient-only.png` is normal green at full SpeedTree detail while retaining shadows. If it does, merge via review and perform a fresh ordinary `CS PHOTO 350 220 90 35 85` and `CS PHOTO -420 -210` from the new main commit without transient overrides. If shadows-off-only is necessary instead, discard or revise this candidate and investigate tree-specific shadow settings. The screenshots of temporary override alone are not proof that saved source quality is fixed.

Git LFS history is content-addressed: new versions update the pointer in the latest commit; do not delete old LFS objects or destroy history without a separately reviewed repository migration.

### 2026-11-08: First Play exception source, without raw logs

The real 60-frame Play Mode at https://github.com/sundamunda289-art/Unity-Game-Aris/actions/runs/42443359568 had 4 terrain tiles, player, camera and vehicle manager, but failed with one `UNCLASSIFIED_EXCEPTION`. The native C# probe now records an allowlisted exception *family* (`UNITY_EXCEPTION`, `INVALID_OPERATION`, `IO_EXCEPTION`, `NOT_SUPPORTED`, etc.) and an allowlisted first stack-trace source (`AARISVEHICLEWORLD`, `AARISWEAPONLOOTWORLD`, `AARISMOBILERUNTIME`, `UNITY_ENGINE`, `UNITY_EDITOR`, etc.). The controller prints only `runtime_error_kind` and `runtime_error_source` on nonzero exit; never logs arbitrary exception messages, trace content, local file paths, license details or secrets. Run `CS SCENE PLAY` after the PR is merged and inspect its result before changing gameplay source. Do not suppress or reclassify the failing exception as success.

### 2026-11-08: Quota-first Codespace Stop and zero-repeat bootstrap

To save the **Codespaces** 4-core compute quota, use Issue #77 owner-only `CS QUOTA` (account-wide monthly billed report, never wakes Codespace) before `CS STOP` (official GitHub Codespace stop). Confirm `CS INFO` shows stopped and report the actual state. GitHub Free personal allowance is 120 core-hours/month, GitHub Pro 180, but precise consumed/remaining hours require authenticated billing API read permission; if denied, disclose unknown and link https://github.com/settings/billing/usage. Machine runtime hours = remaining core-hours divided by number of cores, when usage is reliably reported. Storage is not paused by Stop. Never turn off/delete GitHub repository or Codespace files as a substitute.

For the same machine use `CS START`; for a brand-new one use `CS FRESH SSH`. The committed `.devcontainer/` lifecycle and prebuild-support automatically rerun idempotent Ubuntu dependencies, SSH, LFS hydration, Unity Editor 6000.3.26f1 installation, display. Check `CS STATUS`, `CS LFS REPORT`, `CS LICENSE` before a native camera capture. Do not copy personal Unity license tokens or private caches into GitHub; reauthorize if required. Keep the 210 locally changed original workspace files. Full instructions: `Docs/CODESPACES_FAST_START.md`.

### 2026-11-08: Preserve original Codespace when full LFS pull fails

On the SAME stopped/resumed Codespace, a full `git lfs pull` can fail before Unity starts despite the 18 new SpeedTree mesh OIDs being verified on GitHub LFS. Do not claim the complete 793-file scene is hydrated from that narrow test. Never reset, clean, stash or overwrite the original `/workspaces/Unity-Game-Aris` with its real Unity edits.

`Tools/Codespaces/scene_probe.sh` attempts normal full LFS hydration first. Its verified fallback `Tools/Codespaces/restore_preserved_source_lfs.py` reads original workspace files only if their byte size and SHA-256 match the immutable main commit's exact Git LFS pointers; it copies them to a separate temporary cache, fetches remaining OIDs from the real GitHub LFS endpoint, and validates all 793 isolated files before opening Unity. Nonmatching local edits are NEVER copied, changed or deleted. Missing assets fail closed.

Regression command: `python3 Tools/Codespaces/restore_preserved_source_lfs.py --selftest`. After valid full hydration, perform a real `CS SCENE RENDER`, then `CS SCENE PLAY`, inspect their reports/PNGs and stop the same Codespace to save core-hours.

### 2026-11-08: Authenticated Unity tree mesh handoff when Codespace LFS fetch is denied

Run 42482325804 demonstrated the exact issue after resuming the original Codespace: 775 preserved LFS files matched the current HEAD pointers, but the 18 new meshes could not be fetched from GitHub LFS by the Codespace (`ISOLATED_REMOTE_LFS_FETCH_FAILED`). The host Actions run 42481503533 independently verified that all 18 native mesh OIDs do exist on GitHub LFS and could be freshly refetched by the authorized GitHub Actions runner. Do not misreport this as missing Git history or create replacement tree meshes.

For owner-only `CS SCENE RENDER`, `CS SCENE PLAY`, `CS PHOTO`, `CS FOLIAGE DIAG` and survey, `Tools/Codespaces/controller.sh` retrieves immutable original Actions artifact `aaris-codespace-42472771253` with its authorized short-lived GitHub Actions token, checks exact 4,757,727-byte archive SHA-256 `234024b842e8cc5e1253607ce41b5a6aebec6a7abd3c9fd81837bc960a24197d`, and streams only that non-secret tarball to `/tmp/aaris-codespaces/scene-probe/verified-tree-mass-package.tar.gz` in the same existing Codespace. The remote checksum is verified after transport. Neither account tokens nor Unity licensing state are transferred.

`restore_preserved_source_lfs.py` first imports only byte-identical old objects, then verifies the original archive's provenance, 18 mesh OIDs and sizes against the current immutable Git HEAD pointers, imports only those verified objects into isolated `/tmp` storage, and only if anything is still missing attempts authentic remote Git LFS fetch. The command refuses a wrong/expired/tampered archive or incomplete 793-file hydration. The original dirty GUI project, scene, TerrainData, prefabs, materials and `.meta` GUIDs remain untouched. Archive was an Actions artifact with limited retention; once unavailable, the workflow fails closed until an authorized permanent source is established.

Static CI verifies this exact original package and the selftests before merge. A green static check does NOT imply the true Unity Editor render or Play Mode passed. Run `CS SCENE RENDER` and `CS SCENE PLAY` separately, inspect real Unity output and stop the Codespace after testing.

### 2026-11-09: VERIFIED native Unity, audio, lighting and map QA handoff

Use real run evidence, not a GitHub green badge alone. On the selected `tomaaaris1234-pixel` Ubuntu 22.04 Codespace the Unity Editor 6000.3.26f1 license was independently reported `UNITY_PERSONAL_OR_SUBSCRIPTION_LICENSE_ACTIVE` (owner-controlled run 42544434058). The source scene is the 2×2 km saved `TerrainDemoScene.unity`, with four native TerrainData tiles. 793 LFS asset objects were verified byte-for-byte in clean isolated worktrees. Real native URP scene render (42547321456) and a bungalow coordinate photo (42551108924) succeeded. Do not claim still images prove moving physics, AI bots, accurate house colliders or mobile frame rate.

**Foliage:** The valid five-image comparison is run [42552744195](https://github.com/sundamunda289-art/Unity-Game-Aris/actions/runs/42552744195), *after* merged A/B baseline bug fix PR #227. The saved scene had `AmbientMode=Flat`, `AmbientIntensity=1.2`, and `Shadows=All`; changing ambient or shadow settings in that valid A/B had negligible pixel variation. Earlier pre-load-baseline shadow comparisons were confounded and must not justify disabling shadows or altering original SpeedTree LFS mesh. Keep shadows and native materials unless new coordinate-specific Unity screenshots demonstrate a reproducible defect.

**Play Mode remains STRICTLY NOT PASSED:** The actual native-map 60-frame run [42551935097](https://github.com/sundamunda289-art/Unity-Game-Aris/actions/runs/42551935097) confirmed four TerrainData tiles, saved player, camera, and vehicle manager but logged one `ARRAY_INDEX` with `UNITY_EDITOR/EDITOR_LIFECYCLE`. Crucially, an entirely empty throwaway Unity project reached the same 60-frame `ARRAY_INDEX/EDITOR_LIFECYCLE` error in both software-OpenGL and `-nographics` batch modes (run [42556015594](https://github.com/sundamunda289-art/Unity-Game-Aris/actions/runs/42556015594)). Therefore this is NOT evidence against a specific user game script, model, LFS file, or graphics driver; the Unity Editor batch Play/lifecycle test environment has an independent blocker. Never suppress, reclassify, falsely pass or repeatedly rerun this failing assertion with no new hypothesis. Use separate, controlled editor lifecycle diagnostics and eventual real mobile Play test before certifying game health.

**Offline audio work:** Draft [PR #226](https://github.com/sundamunda289-art/Unity-Game-Aris/pull/226) contains one existing-player-integrated pooled 3D audio director, event hooks, 27 original lightweight synthetic WAV cues, 15 SHA-pinned Kenney CC0 recorded OGG foley clips, stable `.meta` GUIDs and provenance. Licensed Unity 6000.3.26f1 run [42552997584](https://github.com/sundamunda289-art/Unity-Game-Aris/actions/runs/42552997584) verified all 42 native imported AudioClips, all 793 LFS and zero C# compiler errors **at that exact tested commit**. Later event-timing fixes in the same Draft PR must compile again before being called verified. The full APK needs true headphone spatial localization, Play Mode event testing and 2 GB Android profiling; 0.69 MiB of source audio does not establish runtime DSP cost. Do not merge merely because import succeeds, and do not claim all synthetic clips were downloaded CC0 recordings.

**Map/vehicle safeguards:** Merged PR #228 adds tested full road-segment/building-clearance and 45 garage spawn-to-exit blocking tests, preserving 59 saved buildings and 39 roads. Merged PR #232 adds a 14% maximum authored road grade and 3 m maximum exit-to-road gap; all saved 45 vehicle exits joined road centerlines and the steepest current road segment was about 11.6%. These are **blueprint checks only**, not terrain height, bumper/garage collider, NavMesh or actual car physics validation. The existing 21 planned additional settlements are runtime additions, NOT yet baked into the native `.unity` scene; do not represent them as saved core map objects.

**Safety & repeatability:** The original live Codespace workspace was previously found with 210 real local Unity modifications. Never reset/clean/stash/overwrite those files, copy raw license material, delete LFS history or auto-merge native `.unity`, `.prefab`, `.mat`, `.meta` changes. Use GitHub owner-only Issue #77 to invoke the allowlisted `CS SURVEY`, `CS PHOTO`, `CS SCENE RENDER`, `CS LICENSE` etc. Only actual successful Unity camera PNGs qualify as visual evidence. Review scene changes and graphics/physics evidence before committing native asset fixes; keep performance on offline 2 GB Android.


### 2026-11-09: Fresh native photo proof and tree-upgrade gates

**Verified fresh Unity camera evidence, never recycled screenshots:**
- Owner-only Issue #77 `CS SURVEY` request from 2026-11-09 produced genuine Unity 6000.3.26f1 native URP captures in [Actions run 42597555607](https://github.com/sundamunda289-art/Unity-Game-Aris/actions/runs/42597555607) — `AARIS_NATIVE_SURVEY_PASSED`, 12 world positions, 24 nonblank source Camera.Render PNGs, four six-frame `survey-sheet-[1-4].png`, four TerrainData tiles and `survey-report.json`. Artifact: `aaris-codespace-42597555607` (GitHub Actions artifacts expire; save a reviewed copy while accessible).
- New independent close-up `CS PHOTO 418 -350 40 18 30` completed in [Actions run 42598443982](https://github.com/sundamunda289-art/Unity-Game-Aris/actions/runs/42598443982), `AARIS_NATIVE_PHOTO_PASSED`, Unity camera target X=418, Z=-350, camera Y=39.7; actual `photo-detail.png` and `photo-context.png` are in `aaris-codespace-42598443982`. Do not label older shots as fresh.
- The previously noisy `SURVEY PULSE` progress monitor was polling an obsolete `aaris-ai-unity-ssh` display name while the working controller had selected `aaris-ai-ubuntu-minimal-jrg7rgvx5rr435wpp`. This produced **false `0 frames`** despite eventual success. `.github/workflows/aaris-survey-progress.yml` now selects a host identically to `Tools/Codespaces/controller.sh`: exact pinned name first, then exactly one `aaris-ai-ubuntu-minimal`, then exactly one legacy SSH host. It reuses the same selected host for status and first-six transfer. Never report a zero from a different machine as evidence of capture failure.
- Native photo flow is `AGENTS.md` → Issue #77 owner-authorized `CS SURVEY` or `CS PHOTO X Z YAW PITCH DISTANCE` → `Tools/Codespaces/controller.sh` → immutable isolated `Tools/Codespaces/scene_probe.sh` → Unity editor `AarisCodespaceSceneProbe` → validated `codespace-results/` in an Actions artifact → visual inspection. The original dirty GUI workspace with recorded unsaved modifications must never be reset. Keep account tokens and Unity licensing private.
- A green GitHub run alone proves nothing: require `AARIS_NATIVE_SURVEY_PASSED` or `AARIS_NATIVE_PHOTO_PASSED`, `status=passed` JSON report, 4 tiles, expected PNG signature and meaningful variation, and actual human/AI visual inspection. Static preview and sunset/fog tests are not substitutes. The old monitor may read zero before Unity finishes; inspect the actual run and latest same-host report.
- Current genuine near-forest image reveals visually slender tall conifer trunks and patchy conifer branches. Next native upgrade should selectively broaden **lower bark mesh geometry** (not globally inflate whole tree height), gently fill existing foliage cards (no added triangles), and match **trunk collider** to baked lower bark. Inspect all four LODs and preserve original UV/wind/shaders, shade, materials, terrain clearances and approximately 2 GB Android budget. `AarisTreeMassSculpt.cs` is an existing Editor-only one-time bake path; it is not a runtime effect. Never call a source-code change a saved native upgrade unless licensed Editor bake, `.prefab` and mesh `.asset` LFS publish, screenshot A/B, collider and nearby road/house-clearance tests all pass.
- Prefer **measured, photo-guided** changes: compare bark diameter at 0–20% height, tree height/diameter ratio, camera-at-player-height cover, density of crowns, road clearance, vegetation/colliders versus vehicles and player capsule, near/far SpeedTree LOD, shadow/lighting, and real mobile FPS/memory. Preserve tree count and positions; never promise gameplay collisions or 2 GB performance from static images.
