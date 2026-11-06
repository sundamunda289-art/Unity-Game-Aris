# Aaris Unity cloud live desktop (phone / GitHub Actions)

## Overview

The on-demand workflow .github/workflows/aaris-live-unity.yml starts a
GitHub-hosted Linux desktop (Xvfb + Openbox + x11vnc + noVNC), a local camera
control HTTP API, and a temporary Cloudflare Quick Tunnel. The URL is HTTPS,
changes on each run, and only works while the runner is alive.

Quick Tunnel email authentication restricts access using the --allowed-mail
flag and the approved UNITY_EMAIL GitHub Secret. The visitor receives an email
one-time PIN to unlock the desktop. Avoid publishing VNC without this gate.
The link alone does not grant access. Anyone controlling the allowed mailbox
can authenticate, so secure that email account.

## Start on phone
1. Ensure your UNITY_EMAIL Secret contains the mailbox that receives PINs.
2. Open https://github.com/sundamunda289-art/Unity-Game-Aris/actions/workflows/aaris-live-unity.yml
3. Tap Run workflow (main) and choose 15, 45 or 90 minutes.
4. Read the live-desktop job's "Start Cloudflare email-protected temporary
   tunnel" step log. It prints the temporary HTTPS URL and job summary.
5. Open the URL, authenticate with the email PIN, and use the web desktop.
6. Camera controls accept position X/Y/Z, yaw, pitch and distance. The Python
   API writes a bounded camera request locally. The Editor C# bridge updates
   Unity Scene view and renders the frame into ci-live-control/latest.png,
   downloadable from the live page if the real Unity Editor is running.
7. The last actual PNG is uploaded to the workflow's Actions artifact.

## Phone-only Unity Personal Hub activation (no PC)

The editor bootstrap has an **official Unity Hub GUI fallback** when the
`UNITY_LICENSE` GitHub Secret is empty. Store `UNITY_EMAIL` once in repository
Actions Secrets for the email-PIN protected browser session.

Start a live desktop and open the **temporary** Cloudflare URL on the phone.
Select **Open full-screen noVNC**. When the cloud installs Unity Hub, its
desktop window should appear on this same screen. Sign in using Unity's
normal, authorized UI and activate Personal there. Do not type a password into
a public GitHub Issue, comment, workflow input or URL. The bridge detects a
valid-looking ULF issued in the container, then tries to start the same
container's Unity Editor; **only a real Editor launch and actual PNG** prove
success. The Hub can require browser OAuth/approval, and some minimal cloud
images may be missing graphical/browser integration; such failures are
reported, never passed off as active Unity.

A `UNITY_PASSWORD` Secret **does not** automatically activate Unity Personal:
Unity 6000.3 requires the Hub; the secret is not passed to the Hub or Editor.
On a *new* GitHub-hosted machine, Unity may still require login again or reject
a previously issued, machine-bound ULF. This workflow cannot promise perpetual
automatic Personal activation, or copy Hub session cookies across runners.
The source map itself persists through Git/LFS, not through the ephemeral
licensed desktop.

Supported official sources:
- https://docs.unity.com/en-us/engine/6000.3/manual/get-started/install-and-upgrade/licenses-and-activation/license-activation-methods
- https://docs.unity.com/en-us/hub/install-hub-linux

## Licensing / authentication

UNITY_LICENSE is OPTIONAL for starting the Linux desktop, but a valid
authorized ULF is needed for Unity Editor rendering. The existing Python
workflow remains fully independent of Unity activation.

Unity 6000.3 Personal activation normally requires Unity Hub; merely saving
UNITY_EMAIL and UNITY_PASSWORD does not give an ephemeral machine a perpetual
Personal seat. Manual ALF/ULF activation of Personal is unsupported in recent
Unity docs. Do not spoof Machine IDs, publish cookies, or auto-click approval
prompts. No secret contents are uploaded to GitHub Releases.

## Important boundaries

- GitHub-hosted runners have a finite lifetime. This is an on-demand desktop,
  NOT permanently running compute. Next session gets a new Quick Tunnel URL.
- Cloudflare Quick Tunnels are development only, have no uptime guarantee,
  and do not require a Cloudflare account, domain or configured Access app.
  A stable private hostname needs a named Tunnel, domain and Access policy.
- Camera intent API binds 127.0.0.1, accepts only bounded numeric inputs,
  and does not provide arbitrary remote shell access.
- Assets, Library cache and checkpoint strategies are separate from this
  session. Source-of-truth stays in Git. ci-live-control and logs are ignored.
- The phone can see the actual desktop during an authorized running session;
  ChatGPT does not itself have a continuous stream of that live page. The AI
  can inspect screenshot artifacts from GitHub when they exist.

Official documents:
https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/
https://docs.unity.com/en-us/engine/6000.3/manual/get-started/install-and-upgrade/licenses-and-activation/manual-license-activation/manual-activation-cmd-mac

## ChatGPT-accessible, free camera command relay

Persistent read-only Cloudflare Worker hub:
https://aaris-unity-live-hub.aaris-game-control-6f9c.workers.dev

The hub's code is Tools/CI/cloudflare_live_hub.mjs. It uses no KV,
R2, paid resource or stored credential. It reads the public issue camera
channel and available screenshot links. Cloudflare Workers Free requests
are limited to 100,000/day; the hub caches GitHub lookups for 45 seconds.

Authorised camera command channel:
https://github.com/sundamunda289-art/Unity-Game-Aris/issues/77

Only comments authored by sundamunda289-art with the format
CAMERA x y z yaw pitch distance are accepted, e.g.
CAMERA -420 22 -210 0 20 22

While the on-demand Actions desktop is running, Python polls this issue
every four seconds. The runner announces its temporary, email-PIN-protected
Tunnel URL in a GitHub comment. After an authorized licensed Unity Editor
processes a camera request, the Python bridge uploads a real 1280x720
screenshot to the live-photos GitHub Release and replies with its link.
The read-only Worker displays that link.

A connected GitHub app can post issue commands and fetch screenshot links.
This is near-real-time discrete frame inspection, NOT a continuously streamed
video channel into ChatGPT. The phone can watch the actual noVNC live desktop
during a running session.

If Unity has not activated on that machine, no true Unity screenshot is
created. The desktop and Cloudflare hub cannot activate Unity by themselves.
Do not put passwords, license tokens, OAuth cookies or any private values
in public issue comments or public release assets.


## Start a desktop by GitHub comment (no Actions buttons)
The repo owner or an authorized ChatGPT GitHub connector can create a comment
on https://github.com/sundamunda289-art/Unity-Game-Aris/issues/77 with exactly
START_DESKTOP 15
or START_DESKTOP 45 / START_DESKTOP 90.

The workflow verifies the issue number and GitHub author and validates the
duration, then starts the temporary desktop in Actions. The runner adds a
comment containing a temporary PIN-protected Tunnel URL. The stable
Cloudflare hub discovers that link and displays the latest bot camera result.

To move the camera while the runner is active, post an owner-authored
CAMERA -420 22 -210 0 20 22
comment. The runner polls comments and, only if a licensed Unity Editor is
running, publishes an authentic PNG. An expired or unlicensed session reports
a timeout rather than claiming rendering succeeded.

### Verified tests (6 Nov 2026)
- Owner comment START_DESKTOP 15 triggered Actions run 42239962330;
  virtual screen, noVNC, Cloudflare protected Tunnel and issue relay started.
- Owner comment CAMERA -420 22 -210 0 20 22 was read by the active relay.
  The runner reported "timed out waiting for a licensed Unity Editor";
  no genuine Unity photo was produced because UNITY_LICENSE was unavailable.
- Independent Python layout-preview workflow continues to work without Unity.

Cloudflare Worker limits matter: Workers Free has 100,000 requests/day; this
implementation does NOT write every video frame to Cloudflare KV or R2.


## Automated Personal activation bootstrap on each fresh GitHub runner (2026-11-06)

Source: Tools/CI/unity_personal_bootstrap.py
Live interface: Cloudflare PIN-gated desktop URL -> /api/license

The live desktop starts after an owner-authored START_DESKTOP 15/45/90
command. The Python bootstrap executes **inside the same GameCI Linux Editor
container** that must later open the actual 2km Unity scene.

Order of operations:
1. Pull the pinned Unity Editor Docker image and hydrate genuine Git LFS
   assets. This is not instantaneous on an empty runner.
2. If UNITY_LICENSE contains a legitimate ULF XML license, install it in the
   container's private Unity license path (permissions 0600). Editor will
   validate the entitlement; the script does not claim it is portable.
3. Otherwise download the official Unity CLI installer from unity.com,
   install the CLI inside the same container and start unity auth login
   --format json. A user-authored OAuth sign-in URL is stored only in the
   local ci-live-control/license-status.json and exposed only via the
   email-PIN-protected Cloudflare desktop's /api/license endpoint.
4. Open the Cloudflare page on a phone, enter the approved email PIN, and
   tap Sign in to Unity securely when shown. Sign in to the actual Unity
   webpage; do NOT send credentials or OAuth URLs in GitHub issue comments.
5. After the **authorized user login** completes the Python process runs
   unity license activate --personal --accept-eula --non-interactive.
   If Unity accepts the license activation, the script launches the pinned
   Editor in GUI mode, importing Assets and opening the saved native scene.
6. If Unity rejects Personal activation (Unity 6000.3 manual currently
   requires Hub sign-in) the private status displays an actionable error
   instead of claiming the screenshot exists.

Service accounts are NOT used for Personal license activation. UNITY_EMAIL and
UNITY_PASSWORD alone cannot silently approve browser sign-in. An accepted ULF
or a successful authorized OAuth login is required and a new ephemeral runner
may require re-authorization. Machine IDs are never altered. No Unity
tokens/ULFs are archived to GitHub cache, Releases or Cloudflare.

For speed, the GitHub Actions workflow now caches only the Unity Library
import data keyed to Unity 6000.3.26f1 and package files. Cache reuse is best
effort (no guarantee of a fast cold start) and never includes auth directories.

Supported official references:
https://docs.unity.com/en-us/unity-cli/reference/accounts-licenses-cloud-commands
https://github.com/Unity-Technologies/skills/blob/main/skills/unity-cli/references/auth-license-cloud.md
https://docs.unity.com/en-us/engine/6000.3/manual/get-started/install-and-upgrade/licenses-and-activation/license-activation-methods


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

## 2026-11-07 update (authoritative for the AI workbench branch)

The no-ULF fallback changed from the experimental Unity CLI Personal command
to **official Unity Hub GUI**. In the pinned Docker container the script installs
Unity Hub from its signed official APT repository and opens its login window on
DISPLAY=:99. The existing Cloudflare PIN / noVNC viewer is the input surface.
The official Hub may need a fresh browser login on each runner; `UNITY_EMAIL`
controls the protected remote desktop and `UNITY_PASSWORD` is **not** fed
to Hub. A candidate ULF alone is not proof of acceptance; the real Editor and
actual scene capture must still be independently validated.

Latest automation changes are pending review on PR #79 and have not been
claimed as an end-to-end Unity activation success.
