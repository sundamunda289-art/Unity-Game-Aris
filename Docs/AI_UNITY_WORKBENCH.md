# AI Unity Workbench — hands, eyes and 3D shape

This is the **experimental, review-only** extension of the existing GitHub Actions
Unity/Cloudflare desktop. No Codespaces, phone terminal or extra service is needed
to submit commands using the connected **sundamunda289-art** GitHub account.

## Architecture

1. GitHub Actions \`.github/workflows/aaris-live-unity.yml\` creates an ephemeral
   Linux runner, checks out Git + LFS and (if available) restores Unity Library
   import cache. Docker starts the pinned Unity 6000.3.26f1 Editor with a real
   graphics device, Xvfb, noVNC and the email-PIN-protected Cloudflare tunnel.
2. \`Tools/CI/issue_camera_bridge.py\` polls **Issue #77** for NEW owner-written
   comments. \`Tools/CI/ai_intents.py\` validates strict JSON. Anything except
   whitelisted operations and numerical limits is rejected. This does not
   execute shell commands or directly expose a writable internet API.
3. The relay atomically writes \`ci-live-control/ai-command.json\` on the runner.
   \`AarisAIWorkbench.cs\` (Unity Editor only) processes this command, opens the
   real scene, queries/edits it, and writes a nonce-tagged JSON answer.
4. The relay publishes each completed report and optional actual PNG or PLY
   surface-point cloud as uniquely named assets of the \`live-photos\` release,
   then posts their links on the issue. Any connected AI can read the public
   result and choose its next operation.
5. Edits save in the runner working copy. **At a normal session end** Actions
   attempts a new \`ci/ai-editor-<run-id>\` branch and review PR. Only a
   merged PR makes edits part of future fresh runners. Cache is expendable.

## How to start

Authorized repo owner comments \`START_DESKTOP 45\` (or 15/90/180/300 minutes) on:

https://github.com/sundamunda289-art/Unity-Game-Aris/issues/77

Then check the latest tunnel announcement / secure license status. A valid
authorized Unity license is required for real Editor operations. A Linux
desktop alone does **not** mean Unity is running. Phone live view uses the
Cloudflare email PIN. The AI accesses discrete GitHub reports and PNGs,
not the continuously streaming noVNC browser.

## Command reference

Commands below are literal **single-line issue comments**. Replace sample
object names with exact paths returned by \`scan\`.

| Operation | Example | Result |
|---|---|---|
| Nearby discovery | \`AI {"op":"scan","x":-420,"z":-210,"radius":50}\` | Up to 30 nearby scene items / native trees, names, world positions and AABB |
| Exact object dimensions | \`AI {"op":"inspect","target":"Tree_01"}\` | Renderer/collider world-space bounds, size, position and type |
| 3D exterior sample | \`AI {"op":"blueprint","target":"Tree_01"}\` | .ply point cloud sampled from readable mesh vertices; else bounds fallback |
| Real editor photo | \`AI {"op":"photo","target":"Tree_01"}\` | 1280x720 genuine Unity renderer PNG with an auto-framed camera |
| Nudge/move | \`AI {"op":"move","target":"Tree_01","x":-2,"y":0,"z":3}\` | Relative XYZ metres; scene saved after bounds/collision checks |
| Resize world bounds | \`AI {"op":"resize","target":"House/Wall","width":4,"height":3,"depth":0.4}\` | Re-scale that scene transform to approximate desired world-space AABB |
| Native terrain tree | \`AI {"op":"tree","target":"TerrainName","index":5,"width":1.3,"height":0.9}\` | Set native TerrainData TreeInstance.widthScale/heightScale |
| Move native tree | \`AI {"op":"move","target":"terrain-tree:TerrainName:5","x":2,"y":0,"z":-1}\` | Translate tree within its Terrain tile, preserving heightmap snap |
| Native tree shape | \`AI {"op":"blueprint","target":"terrain-tree:TerrainName:5"}\` | Sample prototype mesh surface if readable; otherwise shape proxy |
| Native tree photo | \`AI {"op":"photo","target":"terrain-tree:TerrainName:5"}\` | Unity screenshot around that tree |

All file downloads and issue replies are public. No secrets are posted.

### Reading the output

JSON objects describe names, hierarchy paths, object category, renderer or
collider AABB \`center\` and \`size\` in **metres**. AABB is axis-aligned and
not the same as an oriented bounding box; rotation can affect size. A Unity
native Terrain tree has independent width/height **scale multipliers**, not
guaranteed physical dimensions. It is not yet possible to derive actual
leaf density, trunk diameter, or a full closed/triangulated mesh from those
two scalars. A sampled PLY is useful for shape inspection and visualization,
but is **not** a watertight OBJ/FBX model and does not prove realism.

Resizing \`House/Wall\` changes its Transform scale, not a parametric wall
thickness shader or private procedural wall generator. For a real parametric
house prefab, inspect its hierarchy and original mesh/material authoring
pipeline before applying structural modifications.

## Safety, testing and persistence

- Only exact repo-owner comments are processed. AI intent JSON rejects
  extra keys, shell input, NaN/Infinity, path traversal and out-of-bounds
  measurements. \`move\` is a maximum 100m relative nudge per axis;
  \`resize\` targets 0.05–200m; native tree scale 0.05–10.
- Scan before editing. If two objects have the same name, use the exact
  slash-separated hierarchy path from the report. Terrain/native trees
  require an index from **this checkout**, not a guessed index.
- Editing rejects certain protected player/camera/terrain objects, out-of-map
  placements and newly detected collider or vehicle-spawn overlaps.
  These are conservative checks, NOT full physics/gameplay simulation.
- The Unity scene or TerrainData is written only after validation; review
  actual resulting dimensions and a real screenshot after every edit.
- The normal runner shutdown best-effort pushes a Git LFS-compatible branch;
  check the branch or PR exists **before treating an edit as durable**.
  Hard cancellation/runner timeout may prevent that final push. If the
  Editor is not licensed, no valid editor command result or real photo is
  expected. Never replace a failed render with a Python diagram.
- Self-tests: \`python3 Tools/CI/ai_intents.py\` and
  \`python3 Tools/CI/issue_camera_bridge.py --selftest\` (no license).
  Unity C# compilation and integration photo/edit tests require an actual
  licensed graphical Unity Editor; Python tests alone cannot certify them.
- GitHub-hosted machines have a six-hour maximum job duration and may go
  away sooner. Future runners reload Git history + Git LFS + version-matched
  import cache, not a frozen running desktop or reusable quick tunnel.
