# Codespaces fast start: reproducible Unity 6000.3 Editor + LFS

Repository: `sundamunda289-art/Unity-Game-Aris`, branch `main`.
Unity Editor: **6000.3.26f1** (changeset `de8167e7440c`).
All operations preserve the native Unity scene, Git LFS assets, and pre-existing changes. Do not run `git clean`, `git reset --hard`, or overwrite a Unity licence to recover a Codespace.

## How the stored fast-start logic works

The checked-in `.devcontainer/devcontainer.json` configures:

1. `onCreateCommand: bash .devcontainer/prebuild.sh`: installs Ubuntu packages, `git-lfs`, the official SSH server feature, tries an LFS pull, and runs the verified Unity CLI **fresh** installer (using explicit EULA/yes, no blind `--resume`). The Editor is stored in the Codespace user's persistent `~/.local/share/aaris-unity-editors/6000.3.26f1` directory. The command is idempotent.
2. `postCreateCommand: bash .devcontainer/finalize.sh`: starts the virtual display, checks all LFS-tracked paths for missing files or unexpanded pointers, downloads only if required, and checks the Editor binary. A prebuilt machine should reuse the already installed assets.
3. `postStartCommand: bash .devcontainer/start_screen.sh`: starts Xvfb/Fluxbox when the Codespace resumes. This does **not** automatically activate a Unity licence or prove the actual Editor is rendering.

`Tools/Codespaces/verify_lfs.py` is a read-only count-based checker. It never prints asset file contents or private CLI authentication data. Codespaces prebuild images must NOT contain `.ulf`, Unity Hub login state, access tokens, cookies, GitHub PATs, credentials, machine IDs, or OAuth sessions. They are never created by the prebuild script.

## Enable the actual GitHub Prebuild once

1. Visit [Codespaces settings](https://github.com/sundamunda289-art/Unity-Game-Aris/settings/codespaces) as a repository admin.
2. Select **Set up prebuild**. Choose **main** and **.devcontainer/devcontainer.json**.
3. Prefer **On configuration change** or a moderate scheduled cadence to limit storage and Actions usage. Start with **one retained version** and the region you actually use, subject to GitHub's available controls.
4. Click **Create** and confirm the GitHub-managed prebuild finishes successfully. Before creating a new Codespace, check that the machine selection shows **Prebuild ready**.
5. Create a new Codespace using the same branch, devcontainer and a machine size that supports the prebuild (the current target is 4 CPU / 16 GB).

GitHub executes `onCreateCommand` when producing its prebuild snapshot, but **not** `postCreateCommand` at that stage. Without a configured/ready prebuild, a brand new Codespace still has to download and install Unity Editor and LFS on its own. Prebuilds use storage and Actions quotas; they are **not guaranteed free or seconds-fast**, and a full Unity project import can still require time and an authorized licence.

## Verify a Codespace, without claiming false success

Owner-authorized commands can be posted on [Issue #77](https://github.com/sundamunda289-art/Unity-Game-Aris/issues/77) through ChatGPT's connected GitHub account:

```text
CS STATUS
CS LFS REPORT
CS LICENSE
CS DEPS
CS EDITOR
CS EDITOR DIAG
CS SHOT
CS ACCEPT TERMS
CS SAFE MODE
CS COMPILE REPORT
CS REFRESH
CS CLOSE EDITOR
CS EDITOR PROCESS
CS EDITOR RECOVER
```

`CS STATUS` must show `editor_installed=yes`. `CS LFS REPORT` must show `lfs_unhydrated_pointer_count=0`. `CS LICENSE` searches the effective SSH user's CLI home and reports whether the **license status command** succeeds. `CS EDITOR` can only prove that an Editor process started. `CS SHOT` is only a virtual-desktop capture and needs visual inspection; it is not proof that the map is rendered. If the Editor quits after launch because its Linux shared libraries are absent, run `CS DEPS` once; it only installs a fixed set of Ubuntu 22.04 GUI/OpenGL packages and never changes the Unity map or license. This same package step is included in new prebuilds. Then retry `CS EDITOR`. If the Editor still quits, run `CS EDITOR DIAG`: it classifies local launch failures without publishing raw Editor logs. A genuine Unity-rendered scene screenshot and successful scene load are separate acceptance tests.

## Accept Unity Editor Software Terms (explicit user approval only)

The Editor can show a `Unity Editor Software Terms` checkbox dialog on first launch. With the user's **explicit approval for the terms displayed in the current session**, the authorized repo owner can post `CS ACCEPT TERMS` to Issue #77. The one-shot remote script locates exactly one visible window with that title, checks the dialog geometry, selects the agreement checkbox and presses **Accept and Continue**. A follow-up `CS SHOT` is required to inspect what opened. If the title/geometry is different, it fails closed without clicking. It never runs in a prebuild or at Codespace startup. A new version of the Terms needs fresh permission.

## One-time mobile Unity account authorization

Run the existing reviewed `Tools/Codespaces/termux_unity_allow.sh` in Android Termux. It selects the SSH-ready Codespace and opens the **session-specific official Unity login URL** privately on the phone. The user must approve via Google/Unity and may need Unity Hub for Personal activation. No static link can silently approve a new session. Do **not** put login URLs or licence files in this public repository, GitHub Actions artifacts, GitHub Issues or prebuild snapshots.

## Recovery logic

- Missing CLI/Editor: `CS INSTALL` (same idempotent installer) and then `CS STATUS`.
- Missing LFS objects: `CS LFS` followed by `CS LFS REPORT`; never force reset map assets.
- Codespace offline: `CS START`, then `CS STATUS`. Use `CS FRESH SSH` only when a new machine is needed; it consumes additional quota and leaves older workspaces intact.
- License inactive: use approved phone login on the **same Codespace** and verify again. A new machine must not inherit a different machine's license or OAuth tokens.

Official docs: https://docs.github.com/en/codespaces/prebuilding-your-codespaces/configuring-prebuilds

## Recovery when Unity reports compilation errors

When the screenshot displays **Enter Safe Mode?**, the owner may issue `CS SAFE MODE`. The one-shot command checks the exact visible dialog and clicks **Enter Safe Mode**, not Ignore or Quit. This does not delete any assets or change C# source files.

Then issue `CS COMPILE REPORT` to obtain bounded compiler errors from the current Codespace's Unity Editor log in the Actions artifact. It reports paths only under `Assets/`, `Packages/` or `Library/PackageCache/`, C# error codes, and sanitized messages. It does not publish the raw Unity log. Repair C# source in a reviewed PR, then recompile; never reset the LFS project to fix compiler errors.

## Safely updating source while Unity has migrated ProjectSettings

The old SSH Codespace can show exactly 11 known false-positive LFS tracked asset diffs and two genuine changes to `ProjectSettings/ProjectSettings.asset` and `ProjectSettings/ProjectVersion.txt` after first Unity Editor import. Do not run `git reset --hard`, `git clean`, or force a merge to discard them.

The owner-only `CS SYNC` operation verifies that the ONLY tracked changes are those known 11 assets (each byte-for-byte identical to the Git index) and the two specific Unity ProjectSettings files. It saves private persistent copies of the ProjectSettings files under `/workspaces/Unity-Game-Aris/.aaris-recovery/project-settings/`, makes a scoped Git stash of ONLY those two files, and then performs a regular fast-forward to `origin/main` followed by `git lfs pull`. If any unexpected file is different, sync fails closed. It reports `SYNC_SETTINGS_BACKUP_AND_STASH_SAVED` and `SYNC_LFS_READY`, never raw settings.

The saved ProjectSettings migration is deliberately **not reapplied automatically**. Review it separately after the Unity Editor loads the updated project, especially if Unity upgrades its project settings format. Original scene and map files are unchanged.

## Re-compile after a reviewed Unity package fix

After a source fast-forward and hydrated LFS, if Unity remains in Safe Mode, first use the owner-only `CS REFRESH`. It focuses the existing Unity project window and invokes **Assets > Refresh** via Ctrl+R (Unity's built-in shortcut). This explicitly validates `com.unity.modules.vehicles=1.0.0`, updates only the modification times of the checked-in `AarisVehicleMotor.cs` and `Packages/manifest.json` (without changing their bytes), and causes Unity's Asset Database to rescan these files and recompile associated code. Use `CS SHOT` and `CS COMPILE REPORT` to inspect the new result. Do not interpret old timestamped Console errors in the persistent Editor.log as new failures. If a fresh compilation error remains, fix its underlying package/assembly reference or source with a reviewed PR; never remove vehicle physics to suppress an error.

## Restart Unity without deleting the Codespace

If the Unity Safe Mode Console still shows a stale timestamped error after the corrected manifest is present and a forced `CS REFRESH`, the owner can run `CS CLOSE EDITOR`. This sends only an X11 **WM_DELETE_WINDOW** event to the exact running Unity-Game-Aris Editor, then waits for a clean exit. It never uses SIGKILL, deletes caches, resets scenes or stops the Codespace. If Unity shows an unsaved-changes dialog, the command stops and requires handling that dialog before restarting. Once `UNITY_EDITOR_GRACEFUL_EXIT_CONFIRMED` appears, run `CS EDITOR` to reopen the same Unity version and project and perform a fresh startup compilation. Inspect the new screenshot and logs.

## Diagnose an orphan Unity Editor after its window closes

`CS CLOSE EDITOR` deliberately uses WM_DELETE and can return pending if Unity still has an OS process after the GUI window is gone. `CS EDITOR PROCESS` counts only exact owner-owned `.../Editor/Unity -projectPath /workspaces/Unity-Game-Aris` Linux processes, including zombie/non-zombie states, plus matching visible Unity windows, without leaking PIDs or credentials. If the screenshot has no Unity window, `CS EDITOR RECOVER` refuses if **any** matching Unity window is visible; otherwise it sends **SIGTERM** to only the exact live Editor processes and waits for a clean exit. It never uses SIGKILL, touches the map or stops/deletes the Codespace. After confirmed termination, use `CS EDITOR` to restart the same Unity installation and obtain a genuinely new compilation result.

## Investigating an Editor process with no window

If `CS EDITOR PROCESS` shows one live Unity process but zero visible windows, the diagnostic also samples **two seconds of CPU activity** and categorizes the private `Editor.log` modification age (RECENT, AGING, STALE). It reports only allowlisted status markers such as `UNITY_EDITOR_CPU_ACTIVITY_BUSY` or `UNITY_EDITOR_LOG_AGE_STALE`, not raw logs or runtime commands. Busy CPU with a fresh log may mean ongoing project import; do not terminate it automatically. Low CPU with stale logs warrants further investigation. Do not interpret a `CS SHOT` screenshot of the Ubuntu wallpaper as proof that the Unity scene rendered.
