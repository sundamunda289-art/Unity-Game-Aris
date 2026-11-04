# Aaris AI World Studio: safe, phone-first architecture

Status: **Control-plane bootstrap only.** Installing these files does not mean that
a Codespace worker, persistent Unity session, image bridge or LFS delta uploader is
running. This document must not be used as evidence that any render/edit succeeded.

## Source of truth

Read `AGENTS.md` and `Docs/WORLD_AUTOMATION.md` first. Preserve the existing
`Tools/World/world.py` schema-v2 transaction rules, evidence/asset hashes, scene
GUID/fileIDs and the `Aaris.WorldAutomation.WorldBridge` engine backend.
The current engine uses an isolated copy per call; a cached editor is a future
optimization, not a current promise.

## Control plane

* GitHub is the *request and version-history* plane, not a remote shell.
* GitHub Actions `aaris-world-wake.yml` runs on `main` request-path pushes or
  explicit dispatch. It checks `AARIS_CODESPACE_NAME` and uses a least-privilege
  `AARIS_CODESPACE_LIFECYCLE_TOKEN` to request a stopped Codespace start.
  It never rebuilds, deletes, accesses shell or claims worker readiness.
* Only `ai/requests/**` pushes wake the Codespace. Request parsing/processing
  is deliberately NOT implemented by this initial control-plane PR.
* Codespace ownership/repository is checked before issuing a start request.
* Termux is the one-time credential/configuration console. Official GitHub CLI
  OAuth login is separate from the restricted Actions lifecycle token.
* The GitHub connector cannot run arbitrary shell commands in a Codespace.
  After wake-up, an independently bootstrapped Codespace worker is required.

## Existing Codespace warning — do not skip

An existing Codespace picks up a changed `.devcontainer/devcontainer.json`
after a container rebuild, not merely a stop/start. Rebuild can delete Unity
installed outside `/workspaces`. **Do not create a new devcontainer, run
`gh codespace rebuild`, or use SSH automatically** until the Unity executable,
modules, license handling and persistent storage layout have been inspected.
This initial patch does not modify the devcontainer.

GitHub's Codespaces REST API supports lifecycle operations but no arbitrary
shell execution endpoint. A one-time worker start/hook needs an interactive
terminal, a safely rebuilt devcontainer, or another already installed agent.
Do not claim that file commits alone will bootstrap a missing local worker.

## Planned worker (separate audited change)

1. Single writer with `flock`, durable journal in a workspace-local ignored
   directory, command IDs and replay protection.
2. Parse only a typed allowlist of read operations: `context`, `site`,
   `inspect`, `capture` through the existing World API. No arbitrary shell.
3. Schema-v2 hash-bound plan/preview/apply for writes with atomic publish and
   rollback on corruption or unsupported engine operations.
4. Full-map atlas from **all four** terrain tiles, paged spatial index and
   evidence-level flags for missing LFS bytes, unknown geometry and physics.
5. Pre- and post-capture using a real graphics-capable Unity runner; no
   fabricated PNG when graphics are unavailable. Pictures must have a
   genuinely readable route back to ChatGPT (e.g. short-lived artifacts or a
   properly authorized image/vision bridge).
6. Publish bounded structured results correlated to request IDs. Do not put
   permanent temporary images into Git history.
7. Commit only changed verified files after review on an edit branch; push
   new LFS objects for modified binary assets. Git LFS is **per-file**
   content-addressed, not in-place block/delta replacement.
8. Checkpoint core edits before idle-stop. A stopped Codespace preserves
   `/workspaces`, but deletion or storage loss is still possible without
   Git/backup. Do not leave crucial accepted work only in a Codespace.

## Termux setup

On Android Termux:

```bash
pkg update -y && pkg install -y gh jq
gh auth status -h github.com || gh auth login -h github.com -p https -w
gh auth refresh -h github.com -s codespace
gh api /user/codespaces --jq '.codespaces[] | [.name, .repository.full_name, .state]'
```

When this PR is merged, install/run the repo's tiny Termux helper instead of
cloning the large LFS project:

```bash
curl -fL https://raw.githubusercontent.com/Aaris-khan-don/Unity-Game-Aris/main/Tools/World/termux_codespace_control.sh -o termux_codespace_control.sh
bash termux_codespace_control.sh
```

Create a *separate* fine-grained lifecycle token in GitHub's security settings,
limited to this repository and `Codespaces lifecycle admin: Write`, then
provide it to the helper's hidden prompt. It will be stored in GitHub Actions
Secrets, never in the source tree. Do not share tokens in chat.

After setup, from Termux test:

```bash
gh workflow run aaris-world-wake.yml --repo Aaris-khan-don/Unity-Game-Aris
gh run list --workflow aaris-world-wake.yml --repo Aaris-khan-don/Unity-Game-Aris --limit 3
```

A successful wake workflow means only the lifecycle API succeeded. Do not
treat that as proof of a Unity import, capture, editing or saved core sync.

## Rollout gates

G0. Discover the actual Codespace, user and Unity installation without SSH.
G1. Validate lifecycle token and manual/Actions wake; observe state.
G2. Preserve Unity installation; safely install/boot worker with a permitted
    one-time access path; verify stop/start replay and idempotence.
G3. Read-only atlas and real URP captures plus image availability to the AI.
G4. Plan -> preview -> verify -> guarded core save; diff/hash/unit tests.
G5. LFS and backup checkpoints; isolated PR and merge authorization.
G6. Performance/visual acceptance via actual Unity/Android evidence.

Never confuse a working control plane with a working Unity editing system.

## Local read-only worker (proposed; not deployed)

The pull request adds `Tools/World/ai_world_worker.py`, a one-shot local executor
that delegates to existing `world.py` read operations. It does **not** pull
GitHub commands, mutate map assets, start a Codespace, or publish images.
The initial allowlist is `schema`, `context`, `scan`, `site` and `capture`.
Requests are size- and type-bounded, the worker takes a single-writer flock,
and each request ID is tied to a content SHA256 for replay protection.
Local receipts and images are stored in ignored `.aaris_ai_world/jobs/`.

With the real project checkout and Python requirements available in a Codespace,
an initial *read-only* smoke test is:

```bash
printf '%s\n' '{"id":"bootstrap-schema-01","mode":"schema"}' > /tmp/aaris-world-job.json
python3 Tools/World/ai_world_worker.py --request /tmp/aaris-world-job.json
cat .aaris_ai_world/jobs/bootstrap-schema-01/status.json
```

A local sector observation request can use:

```json
{"id":"sector-350-167-01","mode":"site","x":350,"z":167.3,"radius":50,"limit":100}
```

For a rendered `capture` request, `--allow-capture` and a verified graphics-capable
Unity installation configured by `UNITY_EDITOR_PATH` are required. The existing
engine launches an isolated imported project per request; it is not yet a
persistent Unity camera service. An actual successful render must be tested.

Run the pure-stdlib safety contract tests without Unity:

```bash
python3 -m unittest discover -s Tools/World/tests -p 'test_ai_world_worker.py' -v
```

No Codespace post-start hook, remote job poller, authenticated image transport,
core editing API, auto-merge, or LFS sync is activated by adding this worker.
These require a further explicitly tested rollout after confirming the Codespace
owner, repository and installed Editor. A GitHub connector alone cannot execute
commands inside a stopped Codespace.
