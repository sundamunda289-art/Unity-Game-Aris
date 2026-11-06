# Aaris Unity CI agent instructions

Repository: sundamunda289-art/Unity-Game-Aris; branch: main. Public repository: NEVER commit or release passwords, tokens, Unity license files, Hub state, private content or secrets.

## Canonical world
Unity 6000.3.26f1, URP 17.3.0. Native source scene: Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity; 2km by 2km world on four real TerrainData tiles. See Docs/MAP_LAYOUT.json, README.md, the existing AarisExplorerBuild.cs and Tools/Validation before edits. Many assets are Git LFS tracked: a pointer is NOT a valid Unity scene. A Release checkpoint does not replace permanent Git history.

## GitHub Actions only; no Codespaces
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


## 2026-11-07: Unified AI hands and eyes (draft upgrade)

The repository lives at **sundamunda289-art/Unity-Game-Aris** (the older
Aaris-khan-don URL redirects here). Do not accidentally edit the former repo.
Use the existing **GitHub Issue #77** owner-authorized relay plus the existing
**AarisLiveCameraBridge.cs**. Do NOT add lots of duplicate control scripts.

### Human / any connected AI: how to use

1. Check Unity licensing first. A successful **verify** workflow is *not*
   evidence of a running Unity Editor. Real Unity render/mesh inspection
   needs the licensed 6000.3.26f1 Editor to finish import on the Actions runner.
2. As the connected repo owner, post **START_DESKTOP 45** to
   https://github.com/sundamunda289-art/Unity-Game-Aris/issues/77 .
   Use 15/45/90 (minutes); an Actions runner is temporary. When running, the
   bot posts a fresh Cloudflare Quick Tunnel URL. Its noVNC screen is protected
   by the UNITY_EMAIL one-time PIN. A connected GitHub AI can read issue replies
   and the PUBLIC, non-secret photo/JSON report assets on the live-photos Release;
   it **cannot** silently receive a continuous live screen stream inside chat.
3. For automatic camera positioning, owner-post:
   CAMERA -420 22 -210 0 20 22
   The response contains a genuine Unity 1280x720 PNG *only* after the
   C# bridge actually rendered it. Earlier results with license timeouts
   are **not** Unity photo evidence.
4. For semantic map operations, post one compact JSON per comment prefixed
   with **AGENT**. Operations are bounded, typed, owner-only, no arbitrary code:

       AGENT {"action":"health"}
       AGENT {"action":"scan","x":-420,"z":-210,"radius":45}
       AGENT {"action":"describe","target":"tree:Terrain_Tile_Name:123"}
       AGENT {"action":"photo","target":"scene:GlobalObjectId_V1-2-..."}
       AGENT {"action":"move","target":"<exact ID from scan>","x":-418,"y":21,"z":-208}
       AGENT {"action":"move","target":"<exact ID from scan>","x":-418,"y":21,"z":-208,"apply":true}
       AGENT {"action":"scale","target":"<exact ID from scan>","x":1.2,"y":1.0,"z":1.2,"apply":true}
       AGENT {"action":"save"}

   **Replace placeholders with real ID values returned by scan.** For
   native Terrain tree instances, horizontal width uses x==z; y changes
   height. For a scene mesh, x/y/z are *multipliers* for its Transform.localScale.
   For move, x/y/z are *absolute world metres*, not deltas. Default
   apply=false does a dry run and returns overlap warnings. Any reported
   building/vehicle-spawn clearance conflict blocks applying the edit.
   Do not scale a complete house to change only one wall thickness; choose
   an independently modeled wall subobject, otherwise revise the source model.
5. Every successful AGENT action publishes an
   `agent-report-<issue-comment-id>.json` file under
   https://github.com/sundamunda289-art/Unity-Game-Aris/releases/tag/live-photos .
   Scan returns up to 35 nearby real scene meshes/native Terrain tree instances,
   altitude, slope, bounds, names, stable scene IDs or tree tile/index IDs.
   Describe returns dimensions in metres and a sampled 3D exterior point cloud
   (`outer_surface_points_m`), computed from readable mesh vertices and
   angular silhouette bins. This is an **approximation**, not exact foliage
   topology or an editable CAD solid. Non-readable meshes fall back to AABB
   corner sampling; quality is labeled. Photo returns an actual PNG URL.
6. While the email-PIN live page is up, it exposes
   /api/agent/report, /api/agent/photo and a rotatable 3D point viewer,
   plus the independent noVNC live screen. Never put Unity OAuth URLs, ULFs,
   credentials, emails, tokens or cookies into public issue commands or Releases.

### Saving, resuming and QA

- Changes apply *only when apply=true*, save Unity scene/TerrainData on the
  runner, and periodically checkpoint **tracked** source assets only to the
  dedicated `ai-map-draft` branch (every ~240s, with best-effort final flush).
  An explicit AGENT save forces a Git checkpoint. This is not main; review
  ai-map-draft vs main before merging, including Git LFS object availability,
  changed .unity/.asset files, and map clearances. It NEVER overwrites .meta
  on purpose, never stages secrets/Library, and never pushes directly to main.
- On the next GitHub-hosted runner, the workflow fetches ai-map-draft and
  fast-forward checks against main, then hydrates Git LFS before opening Editor.
  If main and the draft diverged, **stop rather than reset** native scene files.
  Fresh Docker pulls, Unity activation and scene import can still take time.
  6-hour runner lifetime does not imply a persistent 6-hour live UI.
- No automatic save can be guaranteed at a hard runner kill or network failure.
  The last successfully pushed draft commit is the recovery point. Do not
  pretend unpushed RAM/working-tree state survives.
- The workflow's `aaris-live-diagnostics` artifact contains
  `ci-diagnostics/report.json`, checkpoint status and storage metrics.
  It deliberately omits login URL, ULF and private Unity logs.
  Read it to find the last stage (virtual display, API, Cloudflare, LFS,
  Docker, licensing, camera). The GitHub Actions verify job tests protocol
  rejection of malformed/unsafe commands but does not prove Unity C# compiled.
- Offline `Docs/MAP_LAYOUT.json` diagrams and Python checks are helpful for
  unlicensed scene planning but must **never** be labeled authentic Unity
  screenshots, native mesh dimensions or verified gameplay collisions.

## 2026-11-07: Render correctness and troubleshooting contract

- **Capture implementation:** `Assets/AarisWorld/Editor/AarisLiveCameraBridge.cs`
  builds an explicit URP Base Camera and uses
  `RenderPipeline.SupportsRenderRequest` +
  `RenderPipeline.SubmitRenderRequest(StandardRequest)` for Unity 6 URP.
  Only the legacy Built-in Render Pipeline uses `Camera.Render()`.
  Offscreen captures must be actual licensed Unity Editor renders, never
  placeholders from Python/OpenGL/schematic diagrams.
- **Stale-image protection:** At the beginning of each photo attempt,
  invalidate the preceding `latest.png` or `agent-latest.png` path.
  Publish only after rendering passes the black/white/flat-frame check
  and the new PNG is written. A failed render must not return an old photo.
- **Two validation levels:** Pull-request `verify` runs Python protocol
  tests and checks URP capture source markers *without* starting a Unity
  Editor. These checks passing does **not** validate C# compilation,
  URP screenshot fidelity, licensing or successful cloud desktop startup.
  Full integration requires a legitimate active Unity license and a fresh
  live session; confirm an AGENT health, scan, describe and photo request.
- **Failure visibility:** A Git LFS hydration, Docker image pull or
  Unity bootstrap launcher failure must fail the Actions startup step,
  rather than being silently treated as success. Even when activation
  is legitimately unavailable, publish `aaris-live-diagnostics` on
  the workflow's `if: always()` cleanup path where possible.
- **Black desktop diagnosis:** Start with the Action's
  `ci-diagnostics/report.json`, not with an assumption that Unity is
  running: distinguish Xvfb -> noVNC WebSocket -> Cloudflare PIN ->
  Docker container -> Unity authorization -> scene import -> real camera
  frame. Seeing a Linux desktop is NOT proof the Unity Editor started.
  Cloudflare Quick Tunnel URLs change on each new runner.
- **AI edit boundaries:** `AGENT describe` reports a sampled 3D
  exterior point cloud and world-space bounds; it does not reconstruct
  exact trunks, leaf topology or watertight CAD. `AGENT scale` on a
  terrain tree adjusts overall width/height, *not foliage alone*.
  For individual leaves, walls, doors or roof thickness use dedicated
  scene/model subobjects or add an explicitly reviewed prefab/model
  authoring operation. Do not claim that generic mesh scaling edits
  geometry of a merged house or a tree prefab.
- **Persisted progress:** Only a successfully pushed `ai-map-draft`
  commit survives a new GitHub-hosted runner. The repo's permanent assets
  and Git LFS are authoritative; a runner's Unity Library, tunnel URL,
  desktop state and Unity login session are not persistent.
