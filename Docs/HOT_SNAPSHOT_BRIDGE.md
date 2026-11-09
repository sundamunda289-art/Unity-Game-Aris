# Hot Unity snapshot bridge — Codespaces

A real URP Editor screenshot system, not a schematic or simulated frame.
This upgrades the existing AarisLiveCameraBridge.cs: the legacy
Cloudflare camera.json / latest.png protocol continues to work.

## One command on a new Codespace

    ./tools/snap bootstrap

The existing idempotent devcontainer setup installs dependencies, Xvfb
starts, Git LFS is checked, Unity 6000.3.26f1 is installed if missing,
Mesa software OpenGL is checked, then a single GUI Editor is started.
A valid authorized Unity Personal/Editor license is still mandatory.
A bootstrap request does not establish that licensing or the real
URP game scene rendered successfully.

For an already provisioned Codespace:

    ./tools/snap bootstrap --no-install
    ./tools/snap status
    ./tools/snap doctor

## Fast shot commands while the same Editor remains open

    ./tools/snap --id forest-01 --pos 120,18,340 --look 150,12,400 --out ci-live-control/forest.png --persist
    ./tools/snap recapture --id forest-01 --out ci-live-control/forest-after.png
    ./tools/snap batch Tools/Snapshots/example-shots.json --out-dir ci-live-control/review

The lookAt vector is an ABSOLUTE world-space target, not yaw/pitch.
Bounds: X/Z = -1000..1000 and Y = -500..4000. 1280x720 default,
up to 1920x1080. Shot IDs must be safe ASCII names. Batch JSON:

    {
      "shots": [
        {"id":"forest-01","pos":[120,18,340],"lookAt":[150,12,400]},
        {"id":"house-01","pos":[250,8,310],"lookAt":[270,6,330]}
      ]
    }

The queue accepts 1–20 shots; --no-wait queues instantly and returns a
request ID. A reusable camera and RenderTexture render sequential shots
via Unity 6 URP SingleCameraRequest / SubmitRenderRequest. No repeated
Editor startup and no shader substitution are used.

Unique PNGs: ci-live-control/snapshot-photos/<request-id>/
Results: ci-live-control/snapshot-results/<request-id>.json
Heartbeat: ci-live-control/snap-health.json
Interrupted jobs: ci-live-control/snapshot-queue/*.processing

An unresponsive Editor is reported as failure, not a fake saved photo.
Black/white/near-flat images are rejected; strong magenta artifacts are
reported as suspect rather than concealed. These files are Git-ignored.

Latest viewpoint recipes are in ignored
ci-live-control/snapshot-presets.json. The --persist option also saves to
Tools/Snapshots/presets.json: commit this small tracked recipe file to
keep precisely the same camera positions after Codespace migration.

## Boundaries

A native Editor graphics device, licensed Editor, imported URP pipeline,
full LFS project and real scene rendering are necessary to take photos.
Capture speed depends on scene complexity, llvmpipe and shader warmup;
there is no guaranteed subsecond duration on a CPU-only Codespace.
Do not repeatedly retry an interrupted native-crash .processing request
until the root cause is understood.

VFX Graph 17.3.0 and Fog.vfx remain untouched. A passing disposable
project without VFX Graph does not prove VFX is the game crash root cause;
reproduce the problem through isolated A/B testing before removing it.
Never overwrite unsaved scenes, native .unity, .prefab, .mat or .meta files.

## Owner-only ChatGPT intents through GitHub Issue #77

After this review branch is tested and merged (not before), the existing
owner-only Issue #77 workflow can relay strict commands to a **running**
Codespace whose checked-out Editor bridge contains the new snapshot system.
Only comments from `sundamunda289-art` are accepted; the GitHub Action
streams a fixed reviewed script, not user-provided shell commands:

    CS SNAP {"shots":[{"id":"forest-01","pos":[120,18,340],"lookAt":[150,12,400]}]}
    CS SNAP {"recapture":"forest-01"}

The bridge returns a bounded verified PNG artifact (or an explicit error)
through the existing Actions run. The issue reply links the artifact; no
private Unity authorization URLs, assets or licence state are published.
The Codespace still needs a live licensed Editor and verified new scripts.
The PR itself does **not** prove a live photo or activate owner issue commands.
