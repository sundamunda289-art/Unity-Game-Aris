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

## 2026-11-07: AI hands & eyes — owner-validated native Unity world control

**Current canonical repository:** sundamunda289-art/Unity-Game-Aris (transferred from Aaris-khan-don); only modify this repository. AI should first inspect and understand the real scene, then propose bounded edits and verify, then publish a review PR. No direct edits to main. All operations require a genuinely running, licensed Unity Editor.

### Three entrypoints (do not proliferate scripts)

1. **GitHub Actions host:** `.github/workflows/aaris-live-unity.yml` runs a disposable GitHub-hosted Linux desktop, Xvfb/noVNC, current pinned Unity Docker Editor, Git LFS hydration, safe source checkpoint restore, Cloudflare email-PIN Quick Tunnel, and a periodic public-safe source checkpoint. Valid sessions: 15, 45, 90, **300** minutes, with a job budget below the six-hour hosted-runner cap. The runner always expires; there is no permanent interactive computer.
2. **Authenticated AI command relay:** `Tools/CI/issue_camera_bridge.py` reads owner-authored comments on [issue #77](https://github.com/sundamunda289-art/Unity-Game-Aris/issues/77), strictly validates a small JSON action schema, writes local command files, waits for the Unity Editor, posts factual results and optional public geometry/photo Release links. No shell/code execution or credentials in these intents.
3. **Native Unity eyes/hands:** `Assets/AarisWorld/Editor/AarisLiveCameraBridge.cs` contains the existing CAMERA flow plus `AarisWorldAgentBridge`. This opens the checked-in scene, inspects actual renderer world-space bounds and mesh exterior support points, focuses a temporary photo camera, and modifies scene transforms or native TerrainData tree instances. Scene and TerrainData edits are saved to the runner; publishing a PR is a **separate explicit step**.

Supporting infrastructure (not additional AI commands): `Tools/CI/unity_personal_bootstrap.py` activates only via authorized Unity processes; `Tools/CI/session_store.py` verifies source checkpoint SHA-256 and requires an exact source-commit match; `Tools/CI/runner_storage.sh` handles temporary disk pressure.

### AI workflow through connected GitHub

- To start: post exactly `START_DESKTOP 15`, `START_DESKTOP 45`, `START_DESKTOP 90` or `START_DESKTOP 300` on issue #77 as **sundamunda289-art**. You can also post `ACTIVATE_UNITY 15` (or 45/90/300) to request an authorized bootstrap on a new runner. `ACTIVATE_UNITY` without a number retries on an existing running Unity container.
- Wait for a runner comment with a temporary email-PIN-gated Cloudflare URL and a sanitized license state. A connected AI cannot watch a continuous live video; it can read issue results and request fresh captures. The human can view noVNC in their browser.
- On an active licensed session, post **a single-line comment beginning `AI `** followed by one JSON object. Use these exact examples:

```text
AI {"op":"status"}
AI {"op":"nearby","value":[-420,22,-210],"radius":35}
AI {"op":"inspect","target":"tree:0:12"}
AI {"op":"parts","target":"tree:0:12"}
AI {"op":"outline","target":"tree:0:12"}
AI {"op":"photo","target":"tree:0:12"}
AI {"op":"move","target":"tree:0:12","value":[2,0,0]}
AI {"op":"scale","target":"tree:0:12","value":[1.2,0.95,1.2]}
AI {"op":"part_scale","target":"tree:0:12","part":"Leaves","value":[1.1,1,1.1],"scope":"prototype"}
AI {"op":"inspect","target":"scene:Root/Building/WallNorth"}
AI {"op":"parts","target":"scene:Root/Building"}
AI {"op":"scale","target":"scene:Root/Building/WallNorth","value":[1,1,1.1]}
AI {"op":"part_scale","target":"scene:Root/Building","part":"WindowFrame","value":[1.1,1,1]}
AI {"op":"photo","target":"scene:Root/Building"}
AI {"op":"save"}
```

**These are syntax examples, not verified object IDs.** First call `AI {"op":"status"}` on an active issue #77 session: it reports allowlisted Unity bootstrap state, Docker running status, local noVNC TCP accessibility, last camera/AI response and remaining disk, with no license file, OAuth URL, token, password or private logs. It is handled by the Python relay and works even before Unity has activated; it does not start a new cloud runner or establish that the Unity Editor has rendered. Then use `nearby` to get real targets. `tree:tile:index` selects an actual native TerrainData tree; tiles sort by world X then Z. These indices can change if source tree instances are regenerated. Scene targets must be unique names or an exact `scene:Root/Child` hierarchy; ambiguous names are refused. Coordinates are Unity world metres. Move is a **relative delta** per axis, bounded to 100m per call; scale is a **multiplicative** local factor, bounded to 0.25..3. For native Terrain trees width X and Z factors must match. Child `part_scale` works for scene-object Transform children. For native Terrain trees, it is permitted ONLY on an editable `.prefab` child and ONLY with `"scope":"prototype"`: **all** tree instances using that prefab will change. Imported `.fbx` models are explicitly rejected. Confirm the child hierarchy and review all affected trees before saving. This changes leaf/branch scale, not actual mesh leaf count or density.

**Expected responses:** `inspect` includes position, rotation, local scale and approximate actual renderer X/Y/Z world bounds (metres); `parts` enumerates up to 80 exact relative child paths plus local scale and per-child renderer dimensions, with a total count, so AI can identify actual leaf, branch, wall and window paths rather than guessing; any larger parts inventory is supplied as a JSON Release asset (PUBLIC); `nearby` lists up to 30 nearest scene objects/tree IDs; `outline` exports up to 256 world-space surface support dots in JSON, a standard 3D PLY point cloud and an isometric dotted SVG projection (not a watertight triangulated 3D mesh); `photo` produces a genuine 1280×720 Unity camera PNG or a clear render error. Meshes with readback disabled use their renderer bounds as an explicit fallback; wind, LOD, invisible colliders and tree prototype differences may affect the visible silhouette.

**Save and review:** Unity writes real scene/TerrainData assets locally and reports `saved:true`; `AI {"op":"save"}` pushes only whitelisted native Assets to a new `ai/live-review-<runid>` Git LFS-backed branch and creates a PR for review. Do NOT mistake a runner-local save or a Release checkpoint for a merge into main. Review actual geometry, terrain boundaries, tree/road collisions, materials, camera render, Unity compilation and Git LFS before merging. A 7-minute temporary checkpoint plus best-effort end checkpoint may recover work on a subsequent runner with **the same base Git commit**; a forced stop can lose the most recent interval. Git PR is the durable persistence path.

### Diagnostics, failure conditions and trust boundaries

- The live workflow's `verify` job and PR static checks test Python validation; a green `verify` does **not** prove that the licensed Unity Editor ever opened. Cloud desktop and Unity editor are separate stages.
- The `aaris-desktop-startup-<run-id>` Actions artifact contains an early screenshot of the Xvfb desktop, specifically **NOT a Unity render**. A status terminal remains visible until minimized; this avoids an unexplained black VNC viewport during imports. `aaris-runner-debug-<run-id>` contains only sanitized workflow stage, Docker running state, disk free space and photo/agent-result existence, with no OAuth links/license contents.
- If Cloudflare protected Quick Tunnel login loops or noVNC fails, request `AI {"op":"status"}` on issue #77 while an Actions session is active, then consult the runner diagnostics and step logs. A VNC TCP success only proves an open endpoint, not an authorized or functioning Unity Editor. Cloudflare protected Quick Tunnels can have provider-side login/session problems; never remove email protection as a quick fix. Do not disable `--allowed-mail` to work around an authentication problem; use owner-only GitHub command inspection or restore verified tunnel authentication instead.
- Actual Editor operation requires a legitimate entitlement. Existing Unity Personal bootstrap and PIN-based OAuth are attempts, not automatic activation guarantees. Never claim a genuine Unity screenshot unless its file was created by Unity and successfully audited.
- No arbitrary issue text is interpreted as terminal code. Never export passwords, `.ulf`, OAuth URLs, browser cookies, Git credentials or private Editor logs to a **public** Release, issue comment, artifact or checkpoint. Source geometry and photographs published to the `live-photos` Release are PUBLIC.
- GitHub workflows run in ephemeral machines; cold boot, Docker pulling, LFS hydration and import time are not guaranteed to take seconds. A successful established Editor request should respond quickly but tool timeouts are reported rather than faking outputs.

### Tests for AI tool changes

Run `python3 Tools/CI/issue_camera_bridge.py --selftest` (including status/parts schema tests), `python3 -m py_compile Tools/CI/issue_camera_bridge.py Tools/CI/live_camera_server.py` and repository `Tools/Validation/check_explorer.py` C# parsing checks on a PR. The workflow's `verify` job runs those on PRs. Real Editor compile, TerrainData write/read, photo/audit, and Cloudflare PIN sessions must be separately verified on a genuinely licensed runner. Never mark those end-to-end stages as passed based solely on static checks.
