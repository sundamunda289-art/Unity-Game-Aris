# Aaris Unity AI: hands, eyes and safe Git LFS saves

## Environment
- Canonical repo: sundamunda289-art/Unity-Game-Aris; Unity 6000.3.26f1.
- Native scene: Assets/TerrainDemoScene_URP/Scenes/TerrainDemoScene.unity
- Four TerrainData tiles cover the 2 x 2 km map. Scene and assets may use Git LFS.
- Live temporary workflow: .github/workflows/aaris-live-unity.yml
- Owner-only AI command queue: https://github.com/sundamunda289-art/Unity-Game-Aris/issues/77
- Read-only Cloudflare hub: https://aaris-unity-live-hub.aaris-game-control-6f9c.workers.dev

## Just two AI tools plus existing infrastructure
- Tools/CI/ai_unity_ops.py validates exact JSON actions, rejects arbitrary code or paths, uses the existing GitHub owner-only Issue relay, publishes JSON reports, and checkpoints modified tracked Assets/ to a run-specific branch with Git LFS.
- Assets/AarisWorld/Editor/AarisLiveCameraBridge.cs opens the real native scene in the licensed Unity Editor, inspects scene objects and Terrain tree instances, gets bounds, samples 3D mesh points, moves/scales/resizes objects and trees, renders PNGs, and saves assets. No shell control or script interpretation.
- Existing issue_camera_bridge.py and live_camera_server.py still handle CAMERA, START_DESKTOP, ACTIVATE_UNITY and the protected Cloudflare noVNC desktop.

## ChatGPT / other GitHub-connected AI workflow
1. Check Issue #77 and workflow live-desktop job. A green verify job does not prove a licensed Editor session exists. If needed, post START_DESKTOP 45 as the linked owner, then check the temporary Cloudflare PIN-protected URL and private Unity license status.
2. While licensed Unity is actually running, post exactly one AI JSON command as an Issue comment (formats below). Read the corresponding bot reply; download the JSON report from the linked live-photos GitHub Release.
3. Always inspect before editing. Copy the exact reported ID (GlobalObjectId_V1-... or terrain:tile:index); never guess a tree index or wall identity. When editing a whole parent object remember it also moves or scales its children.
4. Preview using capture. Post AI {"action":"save"} to commit tracked Unity Asset files to ai-session/<run-id> and open a review PR, then check the diff and LFS assets before merging. At normal session end the workflow also attempts a final checkpoint.
5. After a fresh runner starts, restore from Git/LFS and the optional safe Library cache. Unity license, authentication, active window and unsaved desktop state do NOT carry over. Hard runner termination can bypass the final checkpoint, so SAVE important edits during the session.

## Copy-ready Issue commands
Inspect nearest terrain trees at a given position (world metres):

    AI {"action":"inspect","query":"Tree","x":120,"z":-44,"radius":30,"limit":10}

From its response choose a CURRENT actual target ID. The following is a format EXAMPLE, NOT a verified tree:

    AI {"action":"outline","id":"terrain:1:104"}
    AI {"action":"move","id":"terrain:1:104","dx":2,"dy":0,"dz":-1}
    AI {"action":"scale","id":"terrain:1:104","sx":1.3,"sy":0.9,"sz":1.3}
    AI {"action":"resize","id":"terrain:1:104","axis":"y","value":8.5}
    AI {"action":"capture","id":"terrain:1:104"}
    AI {"action":"save"}

Inspect buildings or a wall by name, choose exact scene object ID, then resize the wall part:

    AI {"action":"inspect","query":"Wall","x":120,"z":-44,"radius":80}
    AI {"action":"resize","id":"<exact-scene-object-ID>","axis":"z","value":0.4}

A rotated or nested mesh may not respond along a world axis in an intuitive way: measure it again and check the photo. Resize is based on world AABB and local Transform scale, not arbitrary vertex remodeling.

Camera without target:

    AI {"action":"capture","x":120,"y":12,"z":-50,"yaw":0,"pitch":10,"distance":25}

Explicitly opt in to a shared material edit (all instances using that same material will change):

    AI {"action":"tint_shared","id":"<exact-scene-object-ID>","part":"Leaves","hex":"#438A42","shared":true}

For a tree that uses a standalone writable .prefab, you can scale exactly one
named leaf/trunk child. The INSPECT/OUTLINE report includes visible renderer
part names. This changes ALL native trees using the same prefab, not just one:

    AI {"action":"part_scale_shared","id":"terrain:1:104","part":"Leaves","sx":1.3,"sy":1.1,"sz":1.3,"shared":true}

The tree ID and part are illustrative; obtain both from INSPECT first.
Only exact single child names in standalone Assets/*.prefab are writable;
imported FBX model trees cannot be restructured by this command.
Standalone Assets/*.mat shared material edits also work on terrain-tree
renderers, affecting every instance using that material. Leaf density,
branch topology, wall vertex remodeling and collision-aware repositioning
still require dedicated authored geometry logic.

## Measurement and reliability
- Inspect sorts by ground-plane distance before expensive mesh sampling: limit 30, radius 250 m.
- Dimensions are world-space axis-aligned bounding-box sizes in metres, NOT exact trunk thickness or a photogrammetric reconstruction.
- Outline provides up to 400 mesh sample dots plus bounding-box corners in 3D world coordinates, not a complete triangle mesh or precise physical scan. Non-readable imported meshes may only provide bounds dots.
- Native Terrain trees use per-instance widthScale / heightScale; Y must stay zero on moves, and moving between tiles is rejected. Each move delta is bounded to 50 m.
- Real screenshots are 1280 x 720 PNGs from the actual licensed Unity renderer; flat/black frames are rejected. Phone browser can watch noVNC; ChatGPT cannot ingest a continuously streaming remote desktop through an Issue or public read-only Worker.
- The live job offers 15/45/90 minutes, not a permanent six-hour session. Cold runner, Docker pull, Unity import, Git LFS and license setup may take minutes and may fail.
- Public Issue and Release reports are NOT private. Never post credentials, sensitive assets, private license files or OAuth links. Only the Cloudflare PIN-protected page may show authorized sign-in prompts.

## Debug
Run: python3 Tools/CI/ai_unity_ops.py selftest
Run: python3 Tools/CI/issue_camera_bridge.py --selftest
The GitHub Actions verify job runs these tests and safe input checks. CI parser success is not Unity C# compilation or a genuine Editor photo. Inspect Unity console, license-status, workflow steps and actual screenshots to confirm rendering. A timeout is a blocker, not a successful photo.
