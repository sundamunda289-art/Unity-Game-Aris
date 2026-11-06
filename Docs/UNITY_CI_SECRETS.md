# Unity authentication and independent GitHub Actions (mobile setup)

Repository: sundamunda289-art/Unity-Game-Aris (public). Never paste secret contents into chat, Issues, PRs, Release assets, workflow logs, Git cache, or uploaded artifacts.

## Phone-only setup (Unity Personal; no PC and no ULF required to open Hub)

Go to **Settings > Secrets and variables > Actions > New repository secret**.

1. Set **UNITY_EMAIL** to the inbox that should receive the Cloudflare desktop's one-time PIN. This lets the phone open the protected GitHub Actions screen. It is not an Editor license.
2. **Do not create UNITY_LICENSE unless Unity has legitimately issued a compatible ULF**. An empty or missing ULF is expected for a first-time phone-only setup. The *live desktop workflow* now falls back to installing and displaying the **official Unity Hub** in its existing pinned Unity Editor container. Open the temporary Cloudflare/noVNC desktop from the phone, sign in to Unity Hub and activate **Unity Personal** there. The bootstrap waits for a license file before attempting the real Editor.
3. **UNITY_PASSWORD is not needed for this Hub GUI path.** It may be used by *separate GameCI workflows* with supported entitlements and license material, but **a saved email/password is not a supported unattended method to activate Unity Personal on a fresh runner**. Do not paste a password into public GitHub Issues, workflow inputs, commits, logs, or ChatGPT.
4. If Unity Hub legitimately creates a valid `.ulf`, the existing protected export can help download it to the phone for optional `UNITY_LICENSE` Secret setup. **Saving it does not make machine-bound credentials portable**, so another GitHub runner can still need Hub sign-in.

Official Unity 6000.3 licensing documentation explicitly states that **Unity Hub is the only supported method for Personal activation**, not CLI-based activation. No login bypass, reused machine IDs, browser cookie injection or unauthorized seat transfer is implemented.

Official sources: https://docs.unity.com/en-us/engine/6000.3/manual/get-started/install-and-upgrade/licenses-and-activation/license-activation-methods and https://docs.unity.com/en-us/hub/install-hub-linux.

### One-time phone login -> encrypted export -> GitHub Secret -> next runner

This is a *conditional* authorized path, not a method of generating new
Personal entitlements or silently reusing a machine-bound license.

1. On your phone, add **UNITY_EMAIL** to the repository's GitHub Actions
   Secrets so Cloudflare's email-PIN gate can admit your browser. Keep 2FA ON.
2. In Termux, run the checked-in `Tools/CI/termux_secure_license_export.sh`
   from **the PR #79 review branch**, not an unreviewed shell paste.
   The helper uses GitHub CLI to launch `aaris-live-unity.yml` on that branch,
   creates an **age** encryption key locally, and sends **only its public key**
   to the workflow. It does *not* need the Unity password in shell arguments.
3. Open the temporary Cloudflare PIN-protected live desktop on the phone,
   sign in to the **official Unity Hub** with your Unity account credentials
   and any required 2FA. Only the Unity-owned login UI receives the password.
4. If Unity actually issues an authorized `Unity_lic.ulf` on that runner,
   the workflow encrypts it before uploading a short-lived artifact. Termux
   downloads/decrypts it in a private folder, verifies its XML format, stores
   it as `UNITY_LICENSE` using `gh secret set`, and removes the plaintext.
   No unencrypted copy is placed in Android Downloads, Git history, or Releases.
5. Future *review-branch* workflow sessions first try to restore that
   authorized ULF privately into the same Unity Editor Docker container.
   **The Editor must still validate it on each runner**. A license bound to
   the original machine might be rejected, or Hub might issue a different
   named-user XML format rather than a reusable ULF. In that case the helper
   clearly fails the export and Unity Hub sign-in may be needed again.

To use the helper from Termux after inspecting the file:
```bash
pkg install -y curl
curl -fsSLo ~/aaris-license.sh \
  https://raw.githubusercontent.com/sundamunda289-art/Unity-Game-Aris/ai/scene-operator-v1/Tools/CI/termux_secure_license_export.sh
bash ~/aaris-license.sh
```
Once PR #79 is merged and verified on main, replace the branch ref with main
using `AARIS_LICENSE_REF=main bash ~/aaris-license.sh`.

Do not add `Unity.Licensing.Client --activate-ulf --username --password
--include-personal` as an automatic step: these arguments are **not**
documented as a supported Personal activation mechanism in Unity 6000.3.
Do not disable 2FA or give the password to an unreviewed third-party Action.

### Other build workflows and optional credentials

The separate **Unity photographer** workflow checks `UNITY_LICENSE`; without it, the actual rendering job skips but Python map audits continue. Some older GameCI instructions list `UNITY_EMAIL`, `UNITY_PASSWORD`, and `UNITY_LICENSE` for their build actions. Those instructions do NOT make a Personal license work with only a password. `UNITY_SERIAL` belongs to eligible paid serial plans; Unity service-account credentials authenticate to Unity Cloud services, not Unity Personal Editor.

Optional, not required for Personal screenshots: UNITY_SERVICE_ACCOUNT_ID and UNITY_SERVICE_ACCOUNT_SECRET are only for Unity Cloud API/CLI unattended authentication. They DO NOT activate Personal Editor licensing. UNITY_SERIAL belongs to paid serial plans, NOT Unity Personal.

## What currently happens automatically
1. On push, the Unity photographer's license probe only checks whether UNITY_LICENSE exists (without exposing its value).
2. If missing, the photo job is SKIPPED, rather than breaking CI. The independent Python workflow still runs.
3. If present, GameCI unity-activate is attempted using Unity ID credentials when both secrets are configured. Failure is recorded as advisory: the actual Unity Editor validates the supplied ULF in its own process.
4. The runner downloads real Git LFS assets and attempts to restore matching release project + Library checkpoints. A fresh Library is imported if no compatible cache exists.
5. The container uses Xvfb/Mesa and real Unity Editor rendering (not -nographics), takes up to 13 HD map photos, and writes a quality audit. Verified photographs are copied into the live-photos Release and Actions artifacts.
6. The project snapshot is saved through SHA-256 checked chunks; Library is saved after verified successful photo audit. This data is PUBLIC, never include credentials.

## Python worker never waits for Unity
File: .github/workflows/python-map-operations.yml
- Pushes affecting relevant Python/map files perform license-free audit of Docs/MAP_LAYOUT.json.
- Manual mode audit: validate layout, building boxes and spawn clearance.
- Manual mode inspect: inspect approximate road, building and spawn distances from given x/z.
- Manual mode apply-render-materials: invoke existing Python native material writer; open review PR rather than overwriting main directly. It may need Git LFS downloads and source assets. Do not dispatch this mode merely to check an image.
- Native Unity terrain elevation or graphics quality cannot be proven by the blueprint-only audit.

## Limitations
- A GitHub-hosted runner is ephemeral, not a persistent remotely-controllable desktop. CI source changes can still be scripted.
- UNITY_LICENSE must be a valid authorized license for the runtime. Storing a ULF in Secrets does not guarantee Unity accepts it on a different runner; Unity may require license reactivation or a supported license server.
- The Unity CLI service-account flow can authenticate to Unity Cloud but cannot activate Unity Personal.
- A run can terminate before its final checkpoint; commit authoritative map edits to Git. Releases are recovery/caches, not the source of truth.
- Visual success must be supported by generated PNGs and photo-audit.json; an overall green CI run with a skipped photo job means NO render occurred.

Official sources:
- https://game.ci/docs/github/activation/
- https://docs.unity.com/en-us/cloud/accounts/manage-account-security
- https://docs.unity.com/en-us/unity-cli/reference/accounts-licenses-cloud-commands
- https://github.com/Unity-Technologies/skills/blob/main/skills/unity-cli/references/auth-license-cloud.md

## Always-available map preview without any license
The separate Python worker automatically creates five 1920×1080 diagrams: full 2km overview and four 1km terrain-tile views. They show actual stored building bounding boxes, source road centerlines, district centers and vehicle-spawn markers from Docs/MAP_LAYOUT.json. They do **not** depict Unity's real meshes, foliage, textures, shading or skybox. Open `https://github.com/sundamunda289-art/Unity-Game-Aris/releases/tag/layout-review` to download the images on a phone. A `layout-preview-manifest.json` records the source SHA-256. Unity credential availability does not affect this service.
