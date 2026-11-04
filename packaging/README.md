# Package-manager distribution

Drafts for distributing the Windows app through winget and Scoop. **Nothing here has been submitted or published.** The steps below are for the owner, because each one is public.

| Path | What it is |
| --- | --- |
| `winget/manifests/g/ghostface2232/AgentGauge/0.7.5/` | First submission to `microsoft/winget-pkgs`, using the same folder layout as that repository |
| `scoop/agentgauge.json` | Manifest for a self-owned Scoop bucket |
| `winget` job in `.github/workflows/release.yml` | Submits each later release to winget-pkgs. Off until `WINGET_PUBLISH` is set |

Both drafts target v0.7.5, the latest published release. The hash is GitHub's digest for that asset.

## Decisions

### Automatic install stays on for winget and Scoop installs

Since v0.7.5, AgentGauge installs newer releases by itself on launch. We keep that default, for these reasons:

- **winget stays correct on its own.** winget reads the installed version from the Apps & Features entry, which the in-app update rewrites because it runs the same installer with the same `AppId`. An app that has already updated simply shows no upgrade. `ProductCode` in the manifest ties that entry to the package, and the auto-install reaches users sooner than a moderated winget PR can.
- **`RequireExplicitUpgrade: true`** is set because the schema defines it for "packages that upgrade themselves". `winget upgrade --all` skips AgentGauge, so a bulk upgrade doesn't close a running tray app for an update it would install anyway. Someone who turned auto-install off upgrades with `winget upgrade --id ghostface2232.AgentGauge`. The cost is that those users don't get it through `--all`. Remove the key if you'd rather have them included.
- **Known limitation: `winget pin` and `scoop hold` do not stop the in-app update.** Microsoft's docs say a pinned package "may still upgrade on its own". Pinning a version requires turning off the settings switch too, and the Scoop manifest's notes say so. A real fix would be app logic, such as detecting a pin, and there is no reliable signal to read.
- **Rejected: `UpgradeBehavior: deny`.** It would remove the manual path for users who turned auto-install off.

### winget manifest choices

- **`Publisher: AgentGauge` and `PackageName: AgentGauge`** match `AppPublisher` and `AppName` in `installer/Gauge.iss`.
- **The identifier is `ghostface2232.AgentGauge`.** It's hard to change after the first merge, so it needs the owner's approval.
- **`ProductCode: {C7092916-…}_is1`** is the Apps & Features key Inno Setup derives from `AppId`, confirmed on an installed copy. It's needed because the display name of releases up to v0.7.5 is translated and versioned (for example `AgentGauge 버전 0.3.4`) and can't match a package name. `installer/Gauge.iss` now sets `UninstallDisplayName`, so later releases show plain `AgentGauge`.
- **`InstallerType: inno`** gives winget's standard switches (`/SP- /VERYSILENT` or `/SILENT`, plus `/SUPPRESSMSGBOXES /NORESTART`), so none are repeated here.
- **`Custom: /FORCECLOSEAPPLICATIONS`** covers a path the in-app updater never hits. The app exits by itself before an in-app update, but under winget or Scoop it can still be running. `CloseApplications=yes` asks it to close through Restart Manager, but Gauge has no session-end handling of its own, so whether it closes politely is untested, and if it doesn't, locked files would fail the install. The switch lets Setup force the close instead. That's safe because the app has no unsaved work and the installer's `WizardSilent` entry restarts it. The restarted app keeps the saved foreground-lock value and restores it when it next exits.
- **`ElevationRequirement: elevationProhibited`** makes winget refuse to run from an elevated shell. Otherwise the installer's silent relaunch would inherit the elevated token. The tray app and the CLIs it runs would then be elevated, and they could rewrite the user's credential files with administrator ownership. With a different admin account, the app would also land in that account's profile.
- **`MinimumOSVersion: 10.0.19041.0`** is the floor the README states.
- **`ManifestVersion: 1.12.0`** is the version the current Microsoft docs use. `wingetcreate` writes its own current version on later updates.
- **`License: MIT`**, with `LicenseUrl` pointing at `LICENSE` on `main`. Merge this branch before submitting, or that link is dead.

### Submission tool: `wingetcreate`

The release workflow uses Microsoft's `wingetcreate update`, the same way [PowerToys does](https://github.com/microsoft/PowerToys/blob/main/.github/workflows/package-submissions.yml). `winget-releaser` also works, but it's a third-party action that would hold a token able to act on the owner's public repositories. Both need a classic PAT and an existing package version.

The job runs only when a stable release is published and `WINGET_PUBLISH` is `true`, so merging this branch changes nothing outward-facing. It can't be tested without a real release, so check its first run.

### Scoop wraps the installer instead of extracting it

Scoop's usual `innosetup: true` unpacks the files into `~\scoop\apps\`. Here that would be wrong: the in-app update always installs to `%LOCALAPPDATA%\Programs\Gauge`, so the first auto-update would leave **two copies**, relaunch from the one Scoop doesn't manage, and leave Scoop's copy behind. The manifest runs the real installer instead, so there is one install, one Apps & Features entry, and the normal update path.

- **Trade-offs:** Scoop's main buckets wouldn't accept this design, which is fine for a self-owned bucket. `scoop list` can also show an older version after a self-update.
- **The uninstaller skips `scoop update`.** Scoop runs a manifest's uninstaller during updates too, and the Inno uninstaller deletes the start-on-boot Run value, so every update would silently turn that setting off. Scoop's wiki documents `$cmd` for telling these apart, but its current source uses `$subCommand`, so the script reads both.
- **`scoop uninstall` doesn't force-close the app.** A hard kill skips the foreground-lock restore, and after an uninstall no later launch would repair it. The manifest's notes ask the user to exit from the tray first.
- **Updates need no workflow here.** The bucket template's Excavator job picks up new releases through `checkver`, and Scoop reads the hash from GitHub's release digest.

## What was and wasn't verified

- **Verified locally:**
  - `winget validate` passes on the manifest set (client v1.29.380).
  - `installer/Gauge.iss` compiles with `ISCC /O-`.
  - The Scoop manifest parses.
  - The uninstaller guard behaves correctly in a simulation of Scoop's scope chain: it skips on `update` and runs on `uninstall`.
- **Not verified:** No install or upgrade has actually run. This machine has no Windows Sandbox or Scoop, and running the installer here would overwrite the owner's own copy. `release.yml` hasn't run either.

## Owner: decide

1. **Identifier.** Keep `ghostface2232.AgentGauge`, or choose another before the first merge.
2. **`RequireExplicitUpgrade`.** Keep it (recommended) or remove it, as described above.

## Owner: do, in order

**Test on a disposable Windows machine or in Windows Sandbox:**

1. Run these in `packaging/winget/manifests/g/ghostface2232/AgentGauge/0.7.5`. The second command needs an elevated shell, and the install itself needs a non-elevated one:

   ```powershell
   winget validate --manifest .
   ```

   ```powershell
   winget settings --enable LocalManifestFiles
   ```

   ```powershell
   winget install --manifest .
   ```

   The popover should open once. `winget list AgentGauge` should show one entry at 0.7.5. It won't show the package ID until the package is in the winget source.
2. **While the app is running**, run `winget install --manifest . --force`. It should close the app, finish, and restart it. If it hangs or fails, fix that before submitting: this path is new.
3. Run `winget uninstall --manifest .` with the app running and check what remains. This also tests that `ProductCode` finds the install. If `Gauge.exe` stays behind while locked, that's an existing limit of the installer and worth a follow-up.

**Submit to winget:**

4. Check `winget search AgentGauge`, then open one PR adding this folder to `microsoft/winget-pkgs` at the same path. `wingetcreate submit <folder>` does this, and first-time contributors must sign the CLA. An unsigned installer is allowed, but its automated scans may need a moderator's review.
5. After it merges, create a **classic** PAT with `public_repo`, and save it as the repository secret `WINGET_TOKEN`.
6. Set the repository variable `WINGET_PUBLISH` to `true`. Check the first automated PR for a preserved `ProductCode`, `RequireExplicitUpgrade`, and `Custom` switch. The `release` event runs the workflow from the tag's commit, so the tag must include this job.

**Publish the Scoop bucket:**

7. Create a repository from [ScoopInstaller/BucketTemplate](https://github.com/ScoopInstaller/BucketTemplate), for example `ghostface2232/scoop-bucket`. Follow its setup, and copy `scoop/agentgauge.json` into its `bucket/` folder.
8. Test on a disposable machine: `scoop bucket add agentgauge <repo url>`, `scoop install agentgauge`, then `scoop uninstall agentgauge`. Confirm that a turned-on start-on-boot setting survives `scoop update agentgauge --force`, which is the guard's real test.

**After both are live, add this to the README's Windows install section:**

```markdown
Or install with a package manager:

    winget install --id ghostface2232.AgentGauge
    scoop bucket add agentgauge https://github.com/ghostface2232/scoop-bucket
    scoop install agentgauge/agentgauge
```
