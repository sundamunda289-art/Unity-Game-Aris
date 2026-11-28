# Aaris Turbo: dynamic low-latency Codespace terminal

## Current account switching: 2026-11-28

Use [TERMUX_ACCOUNT_SWITCH.md](TERMUX_ACCOUNT_SWITCH.md) for the current
one-time phone installer, `CCF` / `CF` / `cf`, `CD`, safe resume/status and
Editor-first provisioning. The current
[AGENTS.md policy](../AGENTS.md) supersedes older account-switch snippets.
The compatibility `termux_switch.sh` and `termux_cloudflare_switch.sh`
entry points delegate to one locked, receipt-based controller.

The new controller prepares and verifies a candidate before explicit remote
commit and active-configuration publication. It can save
`CF_READY_CODESPACE_PENDING` plus `AARIS_NEXT_BROKER_URL` while preserving the
old active selection until a Codespace is connected. A green Action or
public health response alone does not prove the handover. Retain receipts
when a remote outcome is uncertain; do not dispatch duplicate bootstraps or
promise instant rollback. The legacy Issue/direct-bootstrap entry points
below are lower-level recovery references, not the normal account-switch
procedure.

This is an opt-in, private terminal connection independent of existing Unity and
APK workflows. It controls Codespaces visible to the currently configured
CODESPACE_PAT account. It does not create/delete/reset workspaces, touch native
Unity scenes, or publish command logs in a public GitHub repository.

## Architecture

ChatGPT Cloudflare connector -> authenticated private Cloudflare management SQL
API -> Durable Object queue for machine:<exact-name> -> Codespace Python agent
over outbound HTTPS -> durable output chunks -> Cloudflare account API.

No new GitHub Actions runner is launched for each terminal command.

## Legacy direct initial start and recovery

Owner-only Issue #77: TURBO START <exact-codespace-name>
Or: TURBO START (only when exactly one Codespace is eligible).
The workflow_dispatch machine input accepts the exact machine name.

The isolated .github/workflows/aaris-turbo-bootstrap.yml can start an existing
stopped Codespace if necessary, obtains GitHub Actions OIDC credentials,
authenticates to the Cloudflare Worker, and installs/detaches Python agent
through SSH. The current CCF/CD transaction path stages a candidate and
requires its verified explicit commit; do not apply a legacy immediate
token-rotation assumption to that path. Credentials remain in owner-only
Codespace storage, not Git or public logs; agent grants expire in 7 days.

The Codespace agent uses Python standard library only (no pip); it makes outbound
HTTPS requests. It maintains the session after Actions finishes. A stopped
Codespace cannot answer commands. On an updated minimal container, the
startup hook can reconnect an unexpired installed grant; an absent or
expired grant requires the authorized bootstrap/recovery path.
Changing Codespace accounts still requires the right CODESPACE_PAT permission.

## Command submission from the existing connected Cloudflare account

Cloudflare Worker name: aaris-turbo-terminal
Namespace name: aaris-turbo-terminal_TurboMachine
Endpoint: https://aaris-turbo-terminal.aaris-game-control-6f9c.workers.dev/health

Read current machines using Cloudflare REST management API:
POST /accounts/{account_id}/workers/durable_objects/namespaces/{namespace_id}/query/v2

JSON body:
{
 "durable_object_name":"registry",
 "jurisdiction":"none",
 "queries":[{"sql":"SELECT name,stage,last_seen FROM machines ORDER BY last_seen DESC"}]
}

To submit a command to a specific machine use the same endpoint with:
{
 "durable_object_name":"machine:<exact-codespace-name>",
 "jurisdiction":"none",
 "queries":[{
   "sql":"INSERT INTO commands(id,argv_json,cwd,max_seconds,expires_at,status) VALUES(?,?,?,?,unixepoch()+600,'queued')",
   "params":["unique-job-id","[\"pwd\"]","/workspaces/Unity-Game-Aris",30]
 }]
}

The agent executes argv arrays without shell=True. To run a small Python script
submit argv_json equal to ["python3","-c","print(123)"] as a JSON-encoded string.
The active Codespace command cwd must be under /workspaces and exist on disk.
The agent can execute up to two jobs concurrently, with timeouts and private
stdout/stderr chunks. Its process environment excludes credential-related names.

Read status/output using the same endpoint with:
{
 "durable_object_name":"machine:<exact-codespace-name>",
 "jurisdiction":"none",
 "queries":[
  {"sql":"SELECT status,exit_code,error FROM commands WHERE id=?","params":["unique-job-id"]},
  {"sql":"SELECT seq,kind,text FROM chunks WHERE command_id=? AND seq>? ORDER BY seq LIMIT 100","params":["unique-job-id",-1]}
 ]
}
Continue using seq > last_seen_seq to read only new chunks. Agent polling every
1 second allows quick dispatch but total latency depends on network and API
timing; no fixed end-to-end latency is guaranteed.

## Safety and verification

- Only GitHub Actions OIDC signed for the exact main-branch owner workflow
  can provision agent grants; unauthenticated public command submission is off.
- Only privileged Cloudflare API permissions can add commands to the queue.
  Do not install/enable any unauthenticated public shell endpoint.
- Never put passwords, Unity licenses, API secrets, or OAuth links into any
  public Issue, workflow output or Git commit.
- Preserve any dirty Unity workspace and LFS assets; don't run a broad
  cleanup or reset. Consult AGENTS.md for all existing project invariants.
- A command INSERT is not proof of execution. Verify a matching command ID,
  an actual output chunk, and an exit code. For real Unity screenshots and
  Android build correctness use their independent validation procedures.
- An offline Codespace is detected through missing heartbeat; invoke one
  bootstrap workflow only when necessary.

## One Termux command for future account switches

Install the reviewed command bundle once using the
[current Termux guide](TERMUX_ACCOUNT_SWITCH.md#install-the-commands-once-in-android-termux).
Use `CCF` for Cloudflare, `CD` for the Codespace hosting account plus complete
setup, `CD --resume` to reuse a saved hosting profile, and `CD --status` to
read the recorded/current state. The old downloaded
`bash "$HOME/aaris-switch.sh"` entry point remains a compatibility wrapper
when refreshed from canonical main; it is no longer an independent switch
implementation.

The shared controller stages the hosting profile and bootstrap credentials,
records one operation, and reconciles matching workflow and remote receipts
before repeating a request. It publishes `CODESPACE_PAT` and active broker
state after verified connection commit. Multiple eligible Codespaces
require explicit selection; a stopped/multiple-machine menu also offers
`0` for a new minimal Codespace while preserving old machines. With no
eligible machine, the authorized bootstrap creates a minimal Codespace
within the selected account's quota. Creation uses that hosting user's
token directly on the canonical public repository at the reviewed runtime
SHA, so a new fork is unnecessary. An older stopped machine with unverified
startup hooks requires explicit review/migration before automatic start.
The canonical codebase remains `sundamunda289-art/Unity-Game-Aris`, even when
an existing selected Codespace was originally created from a fork.

The same `CD` operation then verifies/installs the Editor first, starts
single-stream canonical LFS hydration, and runs the four-step same-machine
Unity login in parallel with that hydration. See the current guide for
dirty-file preservation, disk checks, secure-storage consent and the
distinction between project readiness and a rendered scene.

It cannot bypass GitHub Codespaces monthly quotas, temporary GitHub
provisioning delays, account permissions, or an absent Unity Personal license.
A successful GitHub bootstrap proves an agent was installed, not that the
Cloudflare command channel has passed a real end-to-end test. Verify registry
heartbeat and the stdout/exit code for a concrete command.

## Safe reconnect on minimal Codespace restart

The new minimal Ubuntu devcontainer uses postStartCommand to invoke
Tools/Turbo/ensure_agent.sh. After a successful, authenticated one-time
bootstrap, the installer stores the endpoint and seven-day grant expiry as
owner-private files in ~/.config/aaris-turbo alongside a mode-0600 token.
On a normal stop/start, an unexpired grant resumes the agent automatically
without a second GitHub Actions bootstrap. The hook is idempotent and does
nothing if its session files do not exist, are unsafe, or have expired.

This behavior applies to minimal Codespaces created with the updated
devcontainer configuration. Existing full Unity Codespaces and their
postStartCommand are deliberately unchanged. If the selected new account
has no permission, its capacity/quota is exhausted, or the Codespace remains
Provisioning/Shutdown, the agent cannot start; do not invent an online
heartbeat. Only read command status/output from the real Cloudflare broker.

## Private screen vision and GUI actions

The existing authenticated terminal channel also supports screenshot-on-demand:
Tools/Turbo/gui_agent.py. No temporary public trycloudflare.com URL is created.
This is NOT a continuous WebRTC/video feed, and the chat does not run a
background process indefinitely. A trusted assistant requests an action and
reads the new frame from the Cloudflare Durable Object private command output.

The owner-only Turbo bootstrap stages gui.py and gui_setup.sh in private
Codespace storage. In the current CCF/CD transaction path it does not start
a detached GUI package install at the terminal handshake: the sequential
Codespace provisioner owns its dependency setup to avoid competing package
operations. A legacy direct bootstrap may have different dependency
handling; a terminal handshake alone does not certify GUI dependencies.
An existing Unity DISPLAY is
reused when set; otherwise the helper creates its own 1280x720 Xvfb :99
virtual display. No native Unity scene changes are made.

Run via the private Cloudflare commands queue (same machine object as terminal):
argv_json = ["python3","/home/vscode/.local/share/aaris-turbo/gui.py","look"]
Other allowlisted CLI subcommands are init, click x y, double_click x y,
right_click x y, move x y, key keysym, scroll integer, type bounded-text,
paste bounded-text. Never use them to bypass sign-in, license or other gates.
Each command emits AARIS_GUI_FRAME_SIZE, AARIS_GUI_FRAME_BASE64 and
AARIS_GUI_FRAME_COMPLETE to private output chunks. The agent stores these
chunks in the same authenticated Durable Object and reports the exit code.

The assistant can query chunks in order, join their text exactly, extract
the JPEG base64 value and view the actual screenshot privately. Do not
post screenshots or their base64 into public GitHub Issues, Actions logs,
releases or source. GUI screenshots may contain private on-screen content.
The snapshot includes a 100px coordinate grid and the current cursor.
Maximum JPEG payload 96,000 bytes; adaptive resolution and saved click-coordinate mapping preserve accurate GUI targeting. New frames are explicitly requested, never
uploaded continuously.

A screen showing an Xterm or desktop is NOT evidence that Unity has rendered
the game scene; inspect real Editor and scene camera render separately.

## Low-latency fast preview and private retention

Use the same trusted screenshot CLI with `--fast` to request a compact
image (at most 32,768 JPEG bytes) and avoid transmitting a full-resolution
frame for routine cursor/click feedback:

```
python3 ~/.local/share/aaris-turbo/gui.py look --fast
python3 ~/.local/share/aaris-turbo/gui.py click 400 200 --fast
python3 ~/.local/share/aaris-turbo/gui.py key Return --fast
```

The flag must be the **last argument**. Normal `look`, `click` and `key`
return a higher-resolution image (up to 96,000 JPEG bytes). The helper saves
the last frame dimensions privately and scales subsequent click coordinates
back to the real X11 desktop, including after a fast preview. Send coordinates
relative to the screenshot most recently received. Use full resolution when
inspecting Unity settings, small labels or fine visual defects.

For data minimization, the Cloudflare Durable Object automatically prunes
completed/failed command records and their stdout/stderr/screenshot chunks
after 24 hours. Expired unstarted commands are also pruned; active commands
are never pruned mid-run. Cleanup checks are throttled to hourly intervals
and require a valid authenticated agent ping. Avoid sensitive on-screen content
and never publish base64 frames to GitHub issues or Actions logs.

## AUTHORITATIVE FRESH CODESPACE REDEPLOYMENT (same private terminal and screen in any new account)

If user says the old Codespace has exhausted quota, do NOT try to revive it. The
agent in a new Codespace requires its own token; the Cloudflare Worker and
Durable Object namespace are reused across all Codespaces and accounts.

### Actual one-time sequence

1. **GitHub account authorization:** On user's Termux run the canonical
   `Tools/Codespaces/termux_switch.sh` once. It uses GitHub's official browser
   login, stores the two account profiles in the phone's private config,
   replaces repo Actions secret `CODESPACE_PAT` with the selected Codespaces
   account's token, and dispatches **one** `aaris-turbo-bootstrap.yml`.
   Already authenticated with the right account? `bash "$HOME/aaris-switch.sh" --resume`.
   The ChatGPT GitHub connector may directly dispatch the owner workflow if
   account credentials are already configured. No token is ever pasted in chat.
2. **Start/select new Codespace:** The workflow uses the currently selected
   account to list Codespaces, reuses a uniquely eligible instance, or
   creates one lightweight Ubuntu SSH Codespace if no instance exists
   (`.devcontainer/minimal-ubuntu/devcontainer.json`). Ambiguous results
   require the **exact machine name**; never guess. Wait until the machine
   actually reports `Available`, not just `Created` or `Provisioning`.
3. **Worker connection/authorization:** Existing JavaScript Worker code
   `Tools/Turbo/worker.mjs` is **already deployed to Cloudflare** as
   `aaris-turbo-terminal`, with Durable Object class `TurboMachine`.
   **Do not install Node.js/npm in the Codespace just for the Worker.**
   Its code runs on Cloudflare; the Codespace runs the **Python** agent.
   GitHub Actions sends a signed OIDC JWT with audience
   `aaris-turbo-bootstrap-v1` to the Worker's `POST /bootstrap` endpoint.
   The Worker authenticates the GitHub repository/actor/workflow and issues
   an expiring machine-scoped secret. The workflow transfers it only via
   authenticated SSH; no public access URL for arbitrary shell commands.
4. **Private files placed in Codespace:**

   | Private destination | Trusted GitHub source | Runtime dependency |
   | --- | --- | --- |
   | `~/.local/share/aaris-turbo/agent.py` | `Tools/Turbo/agent.py` | Python 3 built-in libraries, no pip |
   | `~/.local/share/aaris-turbo/gui.py` | `Tools/Turbo/gui_agent.py` | Python Pillow plus Xvfb/X11 capture |
   | `~/.local/share/aaris-turbo/gui_setup.sh` | `Tools/Turbo/gui_setup.sh` | sudo + apt for optional GUI packages |
   | `~/.config/aaris-turbo/token` | OIDC bootstrap result | mode 0600, never print |
   | `~/.config/aaris-turbo/endpoint` | OIDC bootstrap result | machine-specific endpoint |
   | `~/.config/aaris-turbo/expires_at` | OIDC bootstrap result | seven-day token expiry |

   The SSH installer `Tools/Turbo/remote_install.sh` checks Python syntax
   and self-tests, installs these files, starts detached `agent.py` via
   `nohup`, checks process status and **proves authenticated Cloudflare ping**.
   The agent uses Python imports `urllib.request`, `urllib.error`,
   `json`, `base64`, `subprocess`, `selectors`, `threading`,
   `pathlib`, etc. These are already in Python 3's standard library.
   The GUI helper additionally needs `from PIL import Image, ImageDraw`.
   On Ubuntu `gui_setup.sh` runs `apt-get install --no-install-recommends
   xvfb fluxbox scrot xdotool xterm xclip python3-pil` only when needed.
   No FastMCP, public tunnel, WebRTC server, pip dependency, or separate
   Cloudflare API key is required for the existing bridge.
5. **Verify terminal end-to-end, without further GitHub Actions:** Look for
   `TURBO_CLOUDFLARE_HANDSHAKE_PASS` in the bootstrap run, then use
   ChatGPT's connected Cloudflare **private management API** to query the
   registry; check a recently updated `last_seen` field. Send command
   `["pwd"]` to `machine:<exact_name>` by the SQL query/v2 instructions
   above, then a second command
   `["python3","-c","print('AARIS_E2E_PASS')"]`. Demand actual stdout
   and `exit_code=0`; **queued is not completed**.
6. **Verify screen end-to-end over same private channel:** When GUI
   dependencies finish, send via the terminal bridge
   `["bash","-lc","python3 \"$HOME/.local/share/aaris-turbo/gui.py\" look --fast"]`.
   Read the ordered `chunks` rows from Cloudflare in small batches, join
   stdout, extract `AARIS_GUI_FRAME_BASE64`, and verify
   `AARIS_GUI_FRAME_COMPLETE` and `exit_code=0`. Decode the JPEG privately
   using a supported image rendering tool; never post the base64 to public
   GitHub issues. Then send a `click 300 200 --fast` command and verify
   the cursor moved in the next frame. Full `look` gives up to 1280x720;
   fast preview typically gives 800x450. Coordinate mapping automatically
   scales from most recent preview to the underlying X11 screen.
7. **Restart without repeating installation:** The updated minimal devcontainer
   runs `Tools/Turbo/ensure_agent.sh` at startup. If unexpired credentials
   are still stored locally, the agent resumes without GitHub Actions.
   Otherwise one owner-authenticated bootstrap is needed.
8. **Unity GAME is additional setup:** A minimal Ubuntu Codespace may not
   have Unity Editor, 6000.3.26f1 licensing, Git LFS hydrated large binaries,
   Android SDK/NDK/JDK, or a running game scene. To claim "whole project
   migrated," validate those independently and safely with actual Unity
   logs/screenshots; preserve dirty work and the currently running APK build.

### If the next AI sees a half-finished setup

- `Created` / `Provisioning` but not `Available`: report GitHub startup
  state and quota; don't spawn more machines without need.
- `Available` but no fresh Cloudflare heartbeat: inspect agent process,
  token expiry/ownership and one OIDC bootstrap. If Cloudflare responds
  HTML 403 to Python urllib, use the reviewed `User-Agent: Mozilla/5.0`
  handling from `Tools/Turbo/agent.py`, not anonymous access.
- Terminal works, screenshots fail: run a **private** environment check
  for `Xvfb`, `scrot`, `xdotool` and `python3 -c 'from PIL import Image'`;
  install `gui_setup.sh` once if dependencies are missing. The Python
  screenshot helper runs under an Xvfb `:99` virtual desktop or an
  existing verified DISPLAY; do not confuse that with Unity Editor.
- GUI works, Unity map not visible: inspect Editor installation, license,
  source scene `Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity`,
  Git LFS assets and renderer separately. Do not claim success from Xterm.
- Connected GitHub/Cloudflare apps missing in a later chat: explain the
  need to connect them; `AGENTS.md` is documentation, not automatic
  persistent permissions or background execution.

## Permanent private Chrome Live Monitor (no new tunnel)

A single, stable, mobile-friendly Worker URL is now the browser display for
the **same private X11 screen and terminal activity the ChatGPT Cloudflare
bridge uses**:

`https://aaris-turbo-terminal.aaris-game-control-6f9c.workers.dev/watch`

The user's **secret bookmark** adds `#key=<64-hex-access-key>` as an URL
**fragment**, not `?key=` as a query. Browser JS obtains the fragment and
uses `Authorization: Bearer <key>` for same-origin
`GET /watch/api`, `GET /watch/frame`, and `POST /watch/capture`.
The fragment is never sent to the server in the URL, including referrer logs.
The SHA-256 hash of the access key is stored in the Cloudflare Worker
`WATCH_TOKEN_HASH` **secret binding**, not in this public repository.
**Never put the key or its hash into public GitHub, Issues, logs or screenshots.**
It is a bearer capability: anyone who receives the full bookmark can watch
private screen and terminal data. If leaked, rotate it via a Cloudflare
secret binding update and privately share a new bookmark.

### Code layout and deployment

- `Tools/Turbo/worker.mjs` imports `Tools/Turbo/watch_page.mjs`. Deploy
  them as **two Worker modules** (main module `worker.mjs`, the second
  module `watch_page.mjs`) with the existing `MACHINES` Durable Object
  binding. The JS Worker runs **on Cloudflare**, not Node inside Codespace.
- Each `TurboMachine` SQL Durable Object maintains a single
  `viewer_frame` entry containing the most recently verified JPEG, its
  dimensions, mode, timestamp and originating command ID. It captures
  screenshots generated by **both ChatGPT's normal GUI commands and the
  browser's viewer capture commands**, only after command exit code 0.
  Commands retain their existing private per-machine queue and normal
  24-hour retention.
- Public `GET /watch` serves only the empty login/browser HTML shell
  with restrictive CSP, no-store headers and no embedded access key.
  Protected `GET /watch/api` selects the latest connected Codespace from
  the global registry (heartbeat age <=90 seconds) or a user-selected
  registered machine, returns bounded last-20 command activity and
  frame metadata. Its active-viewer capture scheduler throttles requests
  and refuses to enqueue when the agent is offline or another capture is
  in progress. The JSON is not publicly accessible.
- Protected `GET /watch/frame` returns the latest JPEG **only with the
  correct bearer token**, and only when the requested frame ID matches
  the stored image. The page fetches the image when its frame ID changes,
  avoiding repeated JPEG downloads for unchanged frames.
- Protected `POST /watch/capture` schedules one `gui.py look --fast`
  or full screenshot on the selected authorized Codespace. There is no
  browser text-to-shell endpoint. Chrome's `document.hidden` and Pause
  switch stop polling while the user isn't viewing. Auto refresh options
  are 3/5/10 seconds; actual captured FPS is bounded by Python/Xvfb,
  Cloudflare, agent queue, and network latency—not guaranteed 3 ms or
  300 microseconds.
- The monitor page persists unchanged when the user switches GitHub
  accounts or creates a new Codespace; new agents connect to the same
  Worker and appear via `registry` with their fresh heartbeat. Old
  Codespaces show offline until their tokens reconnect, rather than
  displaying a falsely live badge.

### Security and end-to-end verification

Keep viewer **read-only**. The original privileged ChatGPT terminal uses
the Cloudflare management API and remains separate; sharing browser
credentials must never enable arbitrary shell execution.

Before providing the secret link, confirm the Worker deployment includes
both JS modules and the `WATCH_TOKEN_HASH` secret; unauthorized API
and frame/capture requests return HTTP 401, bare `/watch` returns HTML
without private data, the original `/health` still returns JSON,
the existing OIDC `/bootstrap` still rejects anonymous access,
and the *authorized* page resolves a freshly connected machine.
Use a harmless GUI capture and terminal command to prove live end-to-end.
A normal desktop screenshot is not proof that Unity Editor's 3D scene is
actually open. ChatGPT can inspect during an active chat but does not
observe the computer continuously after the conversation stops.

Viewer-generated `watch_*` snapshots do **not** count toward the last 20
terminal command history, so continuous screenshots do not hide ChatGPT's
real commands. The last mirrored JPEG is retained privately for up to 24
hours and is replaced when the next authenticated capture succeeds.

