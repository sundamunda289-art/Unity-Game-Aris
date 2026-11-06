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

## AI scene developer protocol (2026-11-06; proposed branch `ai/scene-operator-v1`)

**Mission: give a GitHub-connected AI read / measure / inspect / move / resize / photograph abilities without giving it a general shell over a public Issue.**

The canonical scene remains `Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity`. The new system reuses the existing Cloudflare/noVNC desktop and Issue #77 instead of creating many separate executables.

### Only two agent-facing tools

1. `Tools/CI/ai_intents.py` is the validated JSON intent schema, Issue queue dispatcher, response/blueprint publisher, and **safe LFS commit** layer. The existing `issue_camera_bridge.py` imports it and polls owner-authored comments.
2. `Assets/AarisWorld/Editor/AarisAIControlBridge.cs` is the Unity Editor eye/hand. It reads `ci-live-control/ai-command.json` (not untrusted C#), inspects the Unity scene/meshes/terrain trees and writes `ai-result.json`. The existing `AarisLiveCameraBridge.cs` provides the real camera render method.

No terminal/codespace required for a GitHub-connected AI. A GitHub issue comment authored by `sundamunda289-art` on [Issue #77](https://github.com/sundamunda289-art/Unity-Game-Aris/issues/77) is the command transport.

### Start / license / see

- Start owner Issue comment `START_DESKTOP 45` (or `15`, `90`, `240`, `300`). This starts an **ephemeral** Actions Linux desktop and pinned Unity 6000.3.26f1 container. The protected temporary Cloudflare Tunnel is announced in Issue #77. See the stable [Cloudflare hub](https://aaris-unity-live-hub.aaris-game-control-6f9c.workers.dev).
- Use the email-PIN-protected desktop to complete any **official** Unity sign-in / Personal activation. License status is shown at `/api/license`; only a licensed running Editor can return a genuine PNG. OAuth links and ULF files must never appear in issue comments, public photos, Git history, or logs.
- The phone may use noVNC to watch the live screen. AI clients see discrete JSON + authentic PNG snapshots via the GitHub issue/release interface, **not continuous screen video**.
- The 240/300-minute sessions do not extend GitHub's maximum job lifetime. A new runner starts from Git + LFS, and cached `Library/` only accelerates Unity imports; it is **not** permanent compute.

### AI JSON commands: exact format

Post a single line beginning with `AI ` followed by a small valid JSON object. No markdown code fences. Examples (target IDs are illustrative: always find before editing):

```text
AI {"op":"find","query":"House"}
AI {"op":"inspect","x":-420,"y":22,"z":-210,"radius":40}
AI {"op":"bounds","target":"Village[2]/House[1]/Wall[0]"}
AI {"op":"outline","target":"Village[2]/House[1]","max_points":512}
AI {"op":"photo","target":"Village[2]/House[1]","distance":30}
AI {"op":"bounds","target":"terrain-tree:0:17"}
AI {"op":"outline","target":"terrain-tree:0:17","max_points":1024}
AI {"op":"scale","target":"terrain-tree:0:17","sx":1.1,"sy":0.95,"sz":1.1,"dry_run":true}
AI {"op":"move","target":"Village[2]/House[1]","dx":1,"dy":0,"dz":-2,"dry_run":true}
AI {"op":"move","target":"Village[2]/House[1]","dx":1,"dy":0,"dz":-2}
AI {"op":"scale","target":"Village[2]/House[1]/Wall[0]","sx":1.15,"sy":1,"sz":1}
```

`find` searches active-scene GameObject names; `inspect` reports near a position (including real Terrain tree instances); both return at most 80 objects. `bounds` reports world-space renderer bounds and world metre dimensions, or position-only if unavailable. `outline` samples up to 2048 world-space mesh XYZ vertices and publishes both a three-view *dot blueprint* SVG and downloadable ASCII **PLY point cloud** (open with Blender/MeshLab). The PLY contains sparse points, **not** triangles, a closed 3D model, real-time wind deformation, or proof of realistic materials. If a mesh cannot be read, its geometry is explicitly labelled as a bounds-corners fallback.

A native Terrain tree is identified as `terrain-tree:<tile-index>:<instance-index>` from the `inspect` report. This is an instance in a TerrainData asset, not an ordinary GameObject: `move` and `scale` use the Terrain API and save its asset. Native tree `widthScale` is horizontal (so `sx` must equal `sz`) and `heightScale` is vertical. **Targeted `bounds` and `outline` now inspect its real TreePrototype prefab MeshFilter meshes** using the tree instance's rotation and width/height scale, returning approximate world-metre extents and sampled vertices. If a SpeedTree/LOD mesh is unreadable, individual mesh AABB corners replace its vertices and the JSON marks `prototype-mesh-and-bounds-fallback`. This is a geometric approximation, NOT the actual wind-deformed silhouette, complete foliage surface, or a claimed photoreal render. An inaccessible prototype gives an explicit unsupported result for `outline`. A per-leaf or per-trunk modification of native Terrain trees changes shared prototype authoring and is NOT covered by the per-instance `scale` command; never silently change an entire species to fulfil one-tree instructions.

Each `move`/`scale` is limited to bounded numeric deltas/factors and can be previewed with `dry_run:true`. A write saves its Unity scene/TerrainData source, then commits **only the exact allowlisted changed asset** to Git LFS (when LFS-tracked) on `ai-map-edits`, not to main. For big changes: first inspect dimensions, then dry-run, then write one small change, inspect again, capture photo, and review the diff/PR. There is no user-provided shell, path, or arbitrary code execution. A failed Git push reports persistence failure even if Unity saved locally; do not claim the new runner will remember it.

The result JSON and optional three-view SVG / 3D PLY dot blueprints or genuine PNG are published under unique issue-comment IDs to the public `live-photos` Release. The PNG is a real Unity Editor camera render, **only if** license, Editor launch, mesh import and graphics actually worked. The report carries the exact `nonce` so stale photos/results are rejected. Public artifacts should contain no personal data or secrets.

### Persistence / restore / cross-session safety

- `.github/workflows/aaris-live-unity.yml` checks out main with history and either creates `ai-map-edits` or fetches/merges the existing remote edit branch before Unity starts. Conflicts stop the run; never force-push or discard a player's scene silently.
- Native `.unity` and `TerrainData .asset` can be LFS binaries; `git lfs pull` must hydrate before Editor launch. The Git branch holds permanent source changes; `Library/`, `Temp/` and login data never become source-of-truth.
- Because host runners can be killed, all progress must be committed after each authorized write. Running an Editor on a new VM still depends on legitimate Unity activation each session, if needed. There is **no** promise of unattended activation or guaranteed screenshot latency.
- Always retain separate distinction: `PASS` in Python static tests vs compile PASS in Unity Editor vs real rendered PNG verified from actual scene.

### Operator procedure and known run failures

- For a native tree: inspect nearby X/Y/Z, copy its current `terrain-tree:<tile>:<instance>` ID, run `bounds`, then `outline` for the PLY. IDs are snapshot-relative: after tree list changes, inspect again before editing.
- For a house wall, `find` the actual wall hierarchy ID and inspect `boundsSize`. To change thickness only, `scale` the appropriate local axis with `dry_run:true` first, then save. The system does **not** promise automated collision reflow for neighboring objects; inspect the surroundings again.
- `find` searches scene GameObjects, while `inspect` adds native Terrain tree instances within the requested radius. `find Tree` alone can therefore miss native terrain instances.
- Cloudflare/noVNC is for the owner to **watch** the live desktop. A GitHub-connected AI can read owner-authorized Issue #77 JSON and attached authentic files; it cannot continuously ingest a browser video stream from this chat.
- GitHub Actions run 42271635029 failed during a mandatory 90-second `apt-get update` (exit 124), before noVNC or Unity launched. The proposed workflow uses a bounded update with a cached-metadata install fallback and reports failure if required packages are still unavailable. This does not resolve a missing authorized `UNITY_LICENSE` or replace required official activation.
- Results must be described as Python-validated until **Unity Editor C# compile, license activation, actual terrain serialization, and real PNG** have succeeded on a hosted runner. Never claim a photo arrives in a guaranteed number of seconds; a new VM must restore LFS/cache and launch the licensed Editor first.

### Safe validation

```bash
python3 -m py_compile Tools/CI/ai_intents.py Tools/CI/issue_camera_bridge.py
python3 Tools/CI/ai_intents.py
python3 Tools/CI/issue_camera_bridge.py --selftest
python3 Tools/CI/session_store.py selftest
```

The `verify` job checks Python intent handling without a Unity license. Real Editor compile test and authorized capture cannot be asserted until a Unity license is active and the live `ai-map-edits` branch is running.

## License automation: verified boundary (2026-11-07)

A GitHub-connected AI must NOT silently add the purported command
`Unity.Licensing.Client --activate-ulf --username ... --password ... --include-personal`,
nor promise a new Unity Personal license in five seconds. Those flags are
not documented as a supported Personal activation route for Unity 6000.3.
Unity's current official licensing guide says **Personal activation requires
Unity Hub**. Do not tell users to disable 2FA.

A third-party Marketplace action `RageAgainstThePixel/activate-unity-license`
exists (published major tag `@v2`, not the claimed `@v3`), but its claims
do not prove compatibility with Unity 6000.3, an ephemeral Docker container,
or Personal licensing rules. **Do not pass UNITY_PASSWORD or TOTP seeds to
unreviewed third-party code**, and do not install this action automatically
on a public repo. GitHub Actions host-level activation is not automatically
available to a separate Editor Docker container.

**Current review-branch implementation**:
- If an authorized `UNITY_LICENSE` exists, install privately as a candidate
  and let the Editor validate it; never print, archive or publish it.
- If there is no `.ulf` (phone-only owner), install the official **Unity Hub
  GUI in the same Editor container**. Owner signs in using Cloudflare
  email-PIN-protected noVNC and activates Personal via Unity Hub.
- Check both official Linux license-file formats (`Unity_lic.ulf` and
  `UnityEntitlementLicense.xml`) as unverified **signals**, not an entitlement
  or guaranteed Editor launch. Never cache credentials or forge machine IDs.
- Do not expect merely storing `UNITY_EMAIL`/`UNITY_PASSWORD` to remove
  future Unity Hub login requirements on a fresh GitHub-hosted runner.
- Actual Unity Editor compilation, license validation and genuine screenshot
  remain necessary before this workflow can be called successful.

Official licensing: https://docs.unity.com/en-us/engine/6000.0/manual/get-started/install-and-upgrade/licenses-and-activation/license-activation-methods


## Live operator v2: private desktop diagnostics and object-part discovery (2026-11-07)

This section extends the existing two agent-facing tools; do **not** create a
separate control service for each action. The transport is owner-authored
GitHub [Issue #77](https://github.com/sundamunda289-art/Unity-Game-Aris/issues/77);
the live runner remains the Unity Editor host, not GitHub's web UI.

### First diagnose, then operate

1. On an active runner, post **`DIAGNOSE`** from the repository-owner
   account. The Issue relay immediately replies with *sanitized* booleans and
   fixed status names: Cloudflare origin health, VNC handshake, Unity Docker
   process, Hub/Editor process detection, terrain LFS hydration, and whether
   a genuine Editor photo has been written. **It works without a Unity license.**
   It also writes `ci-live-control/ai-diagnostics-<comment-id>.json` on the runner.
2. On the **email-PIN-protected temporary Cloudflare page only**, use
   `/api/diagnostics` for the same status or `/api/desktop.png` for an
   **actual Xvfb desktop screenshot even before the Unity Editor starts**.
   The first endpoint is safe to quote in a public Issue; the second may show
   private Unity Hub sign-in/MFA, so **never post, commit or upload that image**
   to public GitHub Issues, Releases, artifacts, caches or pull requests.
   This desktop screenshot is not a Unity-scene render.
3. Each runner attempts the existing legitimate license/bootstrap flow.
   If the status is `waiting_for_unity_hub_signin`, open noVNC and complete
   official Unity Hub sign-in/Personal activation. A blank screen and a running
   Docker container do **not** establish a licensed Unity Editor.
4. On shutdown or job failure, Actions uploads the safe
   `aaris-runner-diagnostics` artifact. Download its
   `runner-diagnostics.json` to see how far the setup progressed. The
   artifact excludes OAuth URLs, Unity license files, screen pixels and logs.

### Object-part exploration, changes and genuine visuals

- `AI {"op":"find","query":"House"}` locates object names and exact
  hierarchy IDs from the real loaded Unity scene.
- `AI {"op":"children","target":"<exact-id>"}` lists direct children with
  IDs, metres-based renderer dimensions where available, position and local
  scale. Repeat recursively to discover walls, roofs, windows, trunk/branches
  and leaf mesh objects **if they are separate GameObjects**. Never guess IDs.
- `AI {"op":"inspect","x":-420,"y":22,"z":-210,"radius":40}` surveys
  nearby scene objects **and native Terrain tree instances**; it is bounded
  to 80 entries. Use exact `terrain-tree:<tile>:<index>` IDs only after
  inspection because index IDs can change if tree lists are edited.
- `AI {"op":"bounds","target":"<exact-id>"}` measures approximate
  world-space metres. `AI {"op":"outline","target":"<exact-id>","max_points":1024}`
  uploads sampled 3D XYZ vertices as PLY and an orthographic three-view dot
  SVG. This is **not** a watertight model or per-leaf dynamic simulation.
- `AI {"op":"photo","target":"<exact-id>","distance":30}` takes a genuine
  Editor camera PNG after licensed Unity import. A fully black frame is
  rejected, not described as a successful render. Fresh runner downloads,
  import, licensing and shaders prevent any guaranteed screenshot latency.
- `AI {"op":"move","target":"<exact-id>","dx":1,"dy":0,"dz":0,"dry_run":true}`
  previews a displacement; omit `dry_run` for a source edit.
  `AI {"op":"scale","target":"<exact-id>","sx":1.1,"sy":1,"sz":1.1,"dry_run":true}`
  previews scale. Repeat inspection after the edit, then use `photo`.
  Separate house wall/leaf GameObjects can be scaled individually using
  their exact ID, after checking which local axis represents thickness.
- Native Terrain tree `children` reports **read-only, shared prototype
  parts**. A leaf/trunk edit on one terrain instance is *not supported* by
  per-instance `scale`; those meshes belong to a shared prefab and would
  change the entire species. Native tree `scale` changes overall width and
  height only (with `sx == sz`). Do not claim independent per-tree leaf or
  bark thickness editing.
- Mutations are saved only to allowlisted `.unity`/TerrainData `.asset`
  sources on `ai-map-edits`, including the normal Git LFS push. No
  automatic collision reflow is promised; inspect nearby actors, roads and
  buildings before and after scaling. No arbitrary shell/AI-generated code
  can be issued through a public comment.

### Runner lifetime and recovery

GitHub-hosted runners are disposable and capped by Actions job limits. The
workflow restores Git sources, LFS terrain and the safe Unity `Library/`
cache, then resumes `ai-map-edits` on each **new authorized run**. The
existing boot flow does not guarantee a persistent Unity Hub license across
machines. Do not set up an infinite self-triggering six-hour runner loop:
it would exhaust Actions quota and cannot eliminate licensing. Durable state
is Git commits, not an ephemeral VM, temporary license or noVNC session.

### Test and release checklist

- `python3 -m py_compile Tools/CI/ai_intents.py Tools/CI/issue_camera_bridge.py Tools/CI/live_camera_server.py`
- `python3 Tools/CI/ai_intents.py` and
  `python3 Tools/CI/issue_camera_bridge.py --selftest`
- `python3 Tools/CI/unity_personal_bootstrap.py --selftest`
- Inspect the GitHub Actions `verify` result for the PR.
- After authorized live licensing, verify Unity C# compilation and
  actual Editor PNG, then check Git LFS commit/push on a safe dry-run and
  one small non-dry-run operation. Static Python PASS is not a Unity PASS.

## 2026-11-07: metre-based part sizing and better 3D blueprints

**Existing two-tool architecture only:** `Tools/CI/ai_intents.py` validates owner-authored GitHub Issue #77 commands and handles publication/LFS commits. `Assets/AarisWorld/Editor/AarisAIControlBridge.cs` reads those intents inside a legitimately licensed Unity Editor. There is still no general shell endpoint or unprotected remote Editor access.

### Set a wall or editable part to a desired real-world dimension

First use `find`, `children` and `bounds` to obtain an **actual** scene hierarchy ID and establish the part's own local thickness axis. Example IDs below are illustrative; never guess a target.

```text
AI {"op":"find","query":"House"}
AI {"op":"children","target":"Village[2]/House[1]"}
AI {"op":"bounds","target":"Village[2]/House[1]/Wall[0]"}
AI {"op":"size","target":"Village[2]/House[1]/Wall[0]","axis":"z","meters":0.35,"dry_run":true}
AI {"op":"size","target":"Village[2]/House[1]/Wall[0]","axis":"z","meters":0.35}
AI {"op":"photo","target":"Village[2]/House[1]/Wall[0]","distance":15}
```

The `bounds` response now includes `localDimensionsMetres` for editable scene objects: X, Y and Z are measured along **that object's own local axes**, not the world-aligned renderer box. Use these numbers to identify the wall's thin axis. The new `size` command then sets the approximate extent of one editable scene part along its **own local x, y or z axis**, accounting for the transform and parent hierarchy. It derives dimensions from all child mesh bounding boxes; when no MeshFilter exists, it uses a conservative renderer-bounds approximation. The result JSON includes `plannedLocalScale`, `plannedPosition`, and `measureAccuracy`. Use the dry run first, then verify the actual bounds and collision clearance after saving. The requested dimension is 0.05–100 metres and one edit is capped at 0.25–4 times current size. Never assume a wall's local z axis is thickness without inspecting it.

**Native Terrain tree exception:** native instances have one `widthScale` and one `heightScale`, not independently editable leaf/trunk parts. `size` on a native `terrain-tree:tile:index` now explicitly fails; use the existing relative `scale` command instead. A shared TreePrototype mesh edit can affect an entire species, so it is not silently allowed.

### More complete point-cloud sampling

For scene object `outline`, the sampled vertex budget is divided between child meshes rather than exhausted by the first dense mesh. This gives leaves, branches, trunks, roofs, etc. a chance to contribute points. Unreadable mesh parts contribute bounding-box corner points marked with `sampled-mesh-with-partial-bounds-fallback`. The downloadable SVG/PLY remains a sparse **point cloud**, not a watertight model, material scan, or wind-deformed foliage. True photo capture remains dependent on verified Unity Editor activation and rendering.

### Operational checks

```bash
python3 -m py_compile Tools/CI/ai_intents.py Tools/CI/issue_camera_bridge.py
python3 Tools/CI/ai_intents.py
python3 Tools/CI/issue_camera_bridge.py --selftest
```

A successful Python `verify` job does not prove Unity C# compilation, realistic screenshots, authorized license activation, actual asset writes or Git LFS remote persistence. Keep the change in its review branch until those distinctions are checked.

### 2026-11-07: native tree instance tint and stricter photo evidence

The same `AI` intent bridge now accepts an instance-level tree tint without editing
a shared TreePrototype prefab, material asset or unrelated scene object:

```text
AI {"op":"inspect","x":-420,"y":22,"z":-210,"radius":40}
AI {"op":"bounds","target":"terrain-tree:0:20"}
AI {"op":"tint","target":"terrain-tree:0:20","hex":"#7CBF68","dry_run":true}
AI {"op":"tint","target":"terrain-tree:0:20","hex":"#7CBF68"}
AI {"op":"photo","target":"terrain-tree:0:20","distance":18}
```

These IDs are **examples only**; find the real instance in the `inspect` response.
`tint` accepts exactly a six-digit `#RRGGBB` hexadecimal color, returns
`tintHex` in the tree's object report, and (unless `dry_run:true`) saves the
individual tree's `TreeInstance.color` to its existing allowlisted native
TerrainData asset. This is a whole-instance color multiplier: it is **not**
independent leaf-only color control and actual appearance depends on the tree shader.
Scene object/material tint is deliberately rejected to avoid accidentally changing
every object sharing a material. Normal `ai-map-edits` Git LFS persistence applies.

The genuine Unity camera now rejects near-uniform frames as well as completely black
frames. A rejection is diagnostic evidence, **not a successful photo**; a real
licensed Editor render still needs runtime verification. Keep this PR in review
until the C# scripts compile in Unity 6000.3.26f1 and a live tree tint round-trip
is observed in the saved TerrainData and a real non-flat PNG.
