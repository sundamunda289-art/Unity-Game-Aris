# AARIS AI Super Control V4 — safe initial implementation

This is an incremental extension to the existing Cloudflare Turbo connection. It
is **not** a new tunnel, a second Unity Editor, a replacement for the real game
camera, or an autonomous background ChatGPT session.

## Included in the first V4 implementation

- Tools/Turbo/gui_v4.py — bounded local X11 input sequences (held keys/buttons,
  taps, mouse motion, explicit delays and event markers), an exclusive V4 input
  lock, and fail-safe key/button release. Captures real X11 screenshots with
  scrot and Pillow while the sequence runs, produces a timestamped manifest,
  a 12-frame contact sheet, and a ZIP containing **all** actual JPEG frames.
- Tools/Turbo/unity_v4.py — collision-safe JSON request client. It never
  overwrites another agent's pending request.
- Assets/AarisWorld/Editor/AarisV4EditorDriver.cs — a small editor-only,
  allowlisted request handler. It reads local requests from
  ci-live-control/ai_v4_request.json and writes
  ci-live-control/ai_v4_result.json. Commands: status, camera, focus,
  play, stop, pause, resume, step, release, uimap, tap_ui, hold_ui, and action
  (Fire/Jump/Crouch/Prone/Sprint/Pickup/Scope/Reload/Drop).
- .github/workflows/aaris-ai-control-v4-tests.yml — Python and Xvfb smoke tests.

This prototype is **not yet** full virtual Android multitouch, event-triggered
pre-roll, high-speed Unity Game View capture, MP4 encoding, automatic UI semantic
mapping, or guaranteed 30-FPS recording. Those need follow-up integration and
real Editor verification, not placeholder success messages.

## Very important: Codespace safety

Do not change the active agent's branch, start a second Unity Editor, reset or
clean the existing workspace, or overwrite Library, Logs, scenes, prefabs, LFS
data, tokens or licenses. Test the Python files in /tmp first. Introduce the
C# driver via normal reviewed Git synchronization only when the Editor
workspace is available for asset import/compilation. Until that import is
complete, Unity will not execute the new bridge.

The active Cloudflare Turbo command queue stays authoritative. Only the
authenticated agent can run the local CLI. Never expose it as a public HTTP
or WebSocket shell, or publish private screenshots in GitHub Actions logs.

## First smoke tests

On a machine with Xvfb, xdotool, scrot and python3-pil installed:

    python3 Tools/Turbo/gui_v4.py --selftest
    python3 Tools/Turbo/unity_v4.py --selftest
    DISPLAY=:99 python3 Tools/Turbo/gui_v4.py \
      --id demo_v4_01 --seconds 3 --fps 15 \
      --sequence '[{"op":"mark","label":"start"},{"op":"key_down","key":"w"},{"op":"wait","seconds":0.4},{"op":"key_up","key":"w"},{"op":"mark","label":"end"}]'

The replay output is private at:

    /tmp/aaris-v4-evidence/demo_v4_01/manifest.json
    /tmp/aaris-v4-evidence/demo_v4_01/contact_sheet.jpg
    /tmp/aaris-v4-evidence/demo_v4_01/replay.zip

Each frame has a measured monotonic time, so recorded FPS is demonstrable.
The recorder **tries** to capture the requested FPS, capped at 30, but drops
below target when Unity CPU rendering or X11 capture is slow. The manifest
contains actual timestamps; never report requested FPS as measured FPS.

After the new Editor script has compiled successfully in the real Unity
6000.3 project, for example:

    python3 Tools/Turbo/unity_v4.py status
    python3 Tools/Turbo/unity_v4.py uimap
    python3 Tools/Turbo/unity_v4.py tap_ui --target "PICK"
    python3 Tools/Turbo/unity_v4.py hold_ui --target "HUD Fire" --seconds 1.5
    python3 Tools/Turbo/unity_v4.py camera --x 450 --y 20 --z 800 --yaw 30 --pitch 15
    python3 Tools/Turbo/unity_v4.py focus --target "House_03"
    python3 Tools/Turbo/unity_v4.py action --target Fire --seconds 1.5

The focus target must be the exact **active** scene object name. Named UI input is restricted to the nine known HUD names; it checks the EventSystem's current topmost raycast and reports `blocked_by` instead of clicking through a popup. It dispatches real Unity UI `pointerDown` / `pointerUp` / `pointerClick` handlers, but does not claim to simulate the legacy `Input.GetTouch` hardware path. Game actions
require unpaused Play Mode and actual player components. They will report an
error if those preconditions are missing; a successful command acknowledgement
alone is not proof that the game animation, bullet, sound or UI is correct.
Use telemetry and replay to verify the effect.

## Evidence interpretation

* The X11 recording is the **actual visible Linux desktop**. It can show
  Unity's Editor if Unity is on DISPLAY=:99 and the Game View is visible.
  It is not a guarantee that a running Unity process has an actual game image.
* The Unity bridge's camera/focus actions adjust the **real Scene View**.
  The existing AarisLiveCameraBridge remains available for scene-camera PNGs.
* The V4 driver never silently bypasses Unity asset import or C# compilation.
* A ZIP is useful for storing every frame, but the assistant needs a supported
  image/file retrieval tool to actually inspect those bytes. A private artifact
  return path will be added only after the chosen connected tool supports it.
* Do not interpret the runner Xvfb smoke test as Unity Editor, Android
  multitouch, or real-device FPS validation.

## Next release gates

1. Python unit validations and real X11 smoke test.
2. Verify existing Codespace heartbeat, process, scene and Editor license.
3. Verify actual C# compilation and play/pause/step commands in Unity.
4. Capture an actual nonblank Unity game frame, inspect character and weapon
   and measure recorder's achieved FPS.
5. Only then consider merge to canonical main. Never overwrite another
   agent's uncommitted changes.
