# Releasing

## CI/CD (GitHub Actions)

- **CI** (`.github/workflows/ci.yml`): every push and pull request to `main` builds and tests on Linux, Windows and macOS.
- **Release** (`.github/workflows/release.yml`): pushing a tag like `v3.0.0` runs the tests, builds every package and publishes a GitHub Release with checksums.
  It can also be started by hand (Actions > Release > Run workflow) to build packages without publishing. It also builds the Nightly channel (see below).

## Packages

| Package | Built by | Notes |
|---|---|---|
| `Syn3Updater-<v>-linux-x64.tar.gz`, `-linux-arm64` | `scripts/package-linux.sh` | single file + `install.sh` / `uninstall.sh` (user-level install; the launcher entry is generated with the real path; checks for the tools USB preparation needs) |
| `Syn3Updater-<v>-linux-x64.deb`, `-linux-arm64` | `scripts/package-deb.sh` | installs to `/opt/syn3updater` with a `/usr/bin/syn3updater` command and an application-menu entry; depends on the USB tools and runtime libraries (Debian 11+, Ubuntu 20.04+) |
| `Syn3Updater-<v>-linux-x64.rpm`, `-linux-arm64` | `scripts/package-rpm.sh` | same layout for Fedora / Nobara / RHEL-family; pre-releases get `Release: 0.<pre>.1` so the final release upgrades them |
| `Syn3Updater-<v>-linux-x64.pkg.tar.zst`, `-linux-arm64` | `scripts/package-arch.sh` | native Arch package. Built from the AUR recipe in an Arch container and named `syn3updater-bin`, so installing from the AUR later is a normal upgrade. A pre-release `3.0.0-test7` is versioned `3.0.0test7`, which pacman sorts before `3.0.0`. Not in a repository, so pacman never offers updates. Releases carry no `.sig`, so it must be installed from a downloaded file, not a URL |
| AUR `syn3updater-bin` (not published yet) | `packaging/aur/PKGBUILD`, `scripts/update-aur-package.sh` | uses the release tarballs; see [packaging/aur/README.md](../packaging/aur/README.md) |
| `Syn3Updater-<v>-win-x64.zip`, `-win-arm64` | `scripts/package-windows.sh` | `Syn3Updater.exe` (asks for administrator rights to format drives), the temporary `Launcher.exe` stub (see below) and three hash-pinned transition libraries in `packaging/windows/launcher/` |
| `Syn3Updater-<v>-macos-arm64.dmg`, `-macos-x64` | `scripts/package-macos.sh` (macOS only) | `.app` bundle in a DMG |

CI installs the `.deb`, `.rpm` and Arch package in real Debian, Ubuntu, Fedora and Arch containers (and builds the AUR package in Arch Linux) before a release is
published, so wrong dependency names or a broken launcher stop it.

## Repository secrets

Settings > Secrets and variables > Actions:

| Secret | Needed for |
|---|---|
| `SYN3_API_TOKEN` | the CyanLabs API token compiled into every build (without it the app builds but cannot reach the API) |
| `MINISIGN_PRIMARY_KEY`, `MINISIGN_PASSWORD` | sign each release's `manifest.json` (see below); a release in `CyanLabs/Syn3Updater` fails without them |
| `MACOS_CERTIFICATE`, `MACOS_CERTIFICATE_PASSWORD`, `MACOS_CODESIGN_IDENTITY` | optional: sign the macOS app with a Developer ID (base64 `.p12`, its password, and the identity name) |
| `NOTARY_APPLE_ID`, `NOTARY_TEAM_ID`, `NOTARY_PASSWORD` | optional: notarize the signed app (an app-specific password) |

**Repository variable** (same page, *Variables*): `SYN3_SENTRY_DSN`, the crash report server's DSN, compiled into the Linux, Windows and macOS packages. It is not a secret.
Leave it empty and those builds never offer to send crash reports.

Without the macOS secrets the app is ad-hoc signed: it runs, but users must right-click > Open the first time.

## Signed release manifest (what the app updates from)

Every release carries `manifest.json` and `manifest.json.minisig`. The manifest lists each downloadable file (OS, CPU, kind, address, size, SHA-256) plus the version,
channel and release notes. The app installs nothing that the manifest does not list, and trusts the manifest only if the signature verifies with one of the two public keys
compiled into it (`UpdateKeys` in `src/Syn3Updater.Core/Services/Updates/Minisign.cs`, mirrored in `packaging/update/*.pub`; a test keeps them equal).

- **Written and signed by the release job** (`scripts/make-manifest.sh`, `scripts/sign-manifest.sh`) after every package exists. It refuses to write a manifest when any expected file is
  missing, and verifies its own signature against the public keys, so a wrong key or password stops the release instead of reaching users.
- **The signed comment** is `version:<v> channel:<c>`, so a signature cannot be moved onto another release's manifest. Updates only ever move forward; the one exception is the user
  explicitly choosing another channel in Settings.
- **Release notes** shown in the update banner and the *What's new* box: the *notes* input of the Release workflow, or else the file `release-notes/<version>.md`. Plain text. In the *notes* input type `\n` for a new line and start lines with `- ` for a list (`scripts/format-release-notes.sh` turns them into real lines and tidies the spacing); a `release-notes/<version>.md` file can simply use real lines. Example: `-f notes="- Fixes duplicated packages\n- Windows 11 is shown correctly"`.
- **Keys:** the *primary* private key is the GitHub secret pair `MINISIGN_PRIMARY_KEY` (the `.key` file contents) and `MINISIGN_PASSWORD`. The *backup* private key is **never** in CI: it
  is kept offline and is only used to sign a release if the primary is lost or leaked (then replace the primary public key in a release signed by the backup). Both are in 1Password
  (vault Private, "Syn3Updater minisign PRIMARY" / "BACKUP"). Forks without the secrets publish releases without a manifest: the app then only tells people about them.
- **Install markers:** packages and `install.sh` write `.install-method` (`tar`, `deb`, `rpm`, `pacman`) next to the program. The app updates itself in place only for `tar` (a user-level
  `install.sh` install); package-managed copies are never rewritten and are shown the right `apt`, `dnf` or `pacman` command instead.

## How each install updates itself

| Install | What the banner offers | How |
|---|---|---|
| Windows zip (a folder, normally `C:\Program Files\CyanLabs\Syn3Updater`) | **Update now**, only for a folder that is demonstrably an install: the zip carries a `.install-method` marker, an update leaves an `install-manifest.json`, and the v2 installer put `Launcher.exe` beside the program. A lone `Syn3Updater.exe` copied to the Desktop or Downloads has none of these, is never rewritten, and is shown the download page | The app downloads the zip, re-checks it against the signed manifest, then swaps its own files with the transactional engine in `Services/Updates/Swap` (a port of the Launcher's, same on-disk protocol: `.update-staging`, `.update-backup` with a `COMPLETE` marker, `install-manifest.json`). Windows lets a running program be *renamed* aside but not overwritten, which is all that needs, so the old `Syn3Updater.exe` sits in `.update-backup` until the next start removes it. The app then starts the new program and exits. A failure puts the old files back; a crash mid-swap is undone at the next start (by the app, or by the stub). While the Launcher still exists its prefs file is kept in step (`ReleaseInstalled` = the release tag as four numbers). |
| Linux tar.gz installed by `install.sh` | **Update now** | The archive is unpacked safely and its own `install.sh` is re-run, so the launcher entry and icon stay right; a failure puts the previous program back. |
| Linux deb, rpm, Arch, AUR | **How to update** | The exact `apt` / `dnf` / `pacman` / AUR command. These copies are owned by the package manager and are never rewritten (`.install-method` says which). |
| macOS | **How to update** | Opens the DMG. In-place updating needs a Developer ID signed build, so it is off until releases are signed (`InstallDetector`, `macInPlaceSupported`). |

An update is only ever installed when its signed manifest verifies, it is newer than what runs (or the user chose another channel), and the file matches the manifest's size and SHA-256.
It is never installed while a download or a USB run is in progress.

**Windows shortcuts and link handler.** The v2 installer pointed the Desktop and Start Menu shortcuts and the machine-wide `syn3updater://` handler (`HKLM\Software\Classes\syn3updater`) at `Launcher.exe`.
Once per install, on installs that can update themselves, the app retargets those to `Syn3Updater.exe` (`LauncherMigration`): arguments, descriptions and custom icons are kept, and other copies' shortcuts and the uninstall shortcut are left alone.

## Required updates (forcing everyone onto a newer version)

The backend (Directus, `https://syn3updater.cyanlabs.net`) has a collection `update_policy` with one row per channel (Stable, Beta, Nightly). To force an update, edit the row of the channel:

| Field | Meaning |
|---|---|
| `enabled` | the switch: off = nothing is forced for that channel, whatever the versions say |
| `min_version_all` | the oldest version still allowed, on every OS (`3.0.2`, `3.1.0-beta.2`; empty = no minimum) |
| `min_version_windows`, `min_version_linux`, `min_version_macos` | optional per-OS minimum; the higher of this and `min_version_all` applies |
| `message` | optional text shown to the user in the update dialog (plain English) |

At launch, before anything can be started, the app reads the row of the update channel the user follows. A version **below** the minimum is updated:

- **Windows folder or Linux `install.sh` install:** installed automatically behind a blocking "Update required" screen, then the app restarts. If that fails the user can try again or quit.
- **deb, rpm, Arch, AUR, macOS, anything else the app cannot replace:** a blocking dialog with the exact command (or the download page). The app stays unusable until it has been updated and restarted.

Safety rules, so a mistake here cannot lock people out:

- A minimum **above the newest published release** on that channel is ignored (logged as such): publish the release first, then raise the minimum.
- It is checked **only at launch and never while a download or USB run is in progress**; a development run (including `dotnet run` and IDE builds from a `bin/Debug|Release/net*` folder) is never forced.
- If the newest release cannot be installed or explained from here (no signed manifest, no file for that user's system, failed verification) the minimum is **not** enforced for them (logged): nothing they could do would satisfy it. A temporary network failure is retried by the dialog instead.
- A stalled download (no data for 60 seconds) is given up and can be retried; the partial file is kept.
- The policy is read live at every start and not kept: with the backend unreachable nothing is forced (the app cannot do anything without the backend anyway, and says so), so a stale copy can never force an update that has since been withdrawn.
- Only a release whose signed manifest verifies is ever installed, so a wrong row can at worst force an update to a genuine release.
- `scripts/directus-create-update-policy.sh` creates the collection (idempotent, `--dry-run` by default); values are validated as versions when saved.

## The `Launcher.exe` stub (temporary)

`Launcher.exe` in a Windows package is a small program (`src/Syn3Updater.Launcher`, about 12 MB) that undoes an interrupted update and then starts `Syn3Updater.exe` with the same arguments.
It exists only because old shortcuts, the old link handler and the v2 Launcher (which needs a file of that name in a package) still point at it. The three libraries in `packaging/windows/launcher/` ship
beside it for the same transition: a Launcher that is part-way through installing a package may still load them. Keep all of it for a few months after `3.0.0.0`, then remove it in one release:

1. Delete `src/Syn3Updater.Launcher`, the stub block and the transition-library block in `scripts/package-windows.sh`, `packaging/windows/launcher/`, and the `LauncherStub` code and tests.
2. Update the expected file lists in `.github/workflows/package-check.yml` and the check in `scripts/make-legacy-zip.sh` (the v2 hop needs a `Launcher.exe` only while v2 Launchers exist).
3. The install manifest of each installed copy lists those files, so that release's update removes them (installs made by the old Launcher also have a manifest); the shortcut migration, which does not need `Launcher.exe` to exist, repairs any shortcut that was missed.
4. A release or two later, remove `LauncherPrefs` (the Launcher's prefs file handling) and `LauncherMigration`.

## Update channels

The channel a build belongs to is read from its version, so nothing else has to be stored:

| Channel | Gets | Version looks like | Published as |
|---|---|---|---|
| **Stable** (default for releases) | tested releases | `3.0.0` | a normal GitHub release on `CyanLabs/Syn3Updater` |
| **Beta** (default for test builds) | the newest release *or* pre-release | `3.1.0-beta.2`, `3.0.0-test8` | a pre-release on `CyanLabs/Syn3Updater` |
| **Nightly** (off for now) | automatic, untested builds of the latest code | `3.1.0-nightly.20261108.5` | one rolling pre-release named `nightly` on `CyanLabs/Syn3Updater` |

- The word sent to the API and written in logs as the "branch" is the channel of the running build (`Stable`, `Beta` or `Nightly`).
- **Nightlies are switched off.** `release.yml` has a scheduled run at 03:17 UTC, but it does nothing until the repository variable `NIGHTLY_ENABLED` is set to `true`
  (Settings > Secrets and variables > Actions > Variables). When enabled it builds only when `main` changed since the last nightly, and only in `CyanLabs/Syn3Updater` (forks skip).
  A manual run with "nightly" ticked always goes ahead, so do not do that yet either. The version is `<Version in Directory.Build.props>-nightly.<date>.<run number>`,
  the release tag is always `nightly` and the app reads the version from the release title.
  **Do not enable it until every user has moved off the v2 Launcher:** that Launcher takes the newest release as its Beta update and crashes on a tag that is not a version number
  (the 2.14.2.1+ Launcher ignores such tags). Until then the Nightly choice in Settings simply finds nothing to offer.
- **Windows: the Launcher follows the channel setting.** Like v2's Settings page, choosing a channel writes `ReleaseBranch` into the Launcher's
  `C:\ProgramData\CyanLabs\Syn3Updater\launcherPrefs.json` (0 Stable, 1 Beta, 2 Alpha = Nightly: v2's unfinished third slot is v3's Nightly; the install record next to it is never touched),
  so the next Launcher start installs that channel. The Launchers only read the production repo, which has no nightlies: the 2.14.2.1+ Launcher treats Alpha like Beta,
  and the older 2.14.2.0 Launcher treats anything but Beta as Stable. If the file says something different from the app (changed by v2 or the Launcher), the file wins, as in v2.
  It is only written when that file or a `Launcher.exe` beside the app exists, and never on Linux or macOS.

## Releasing for the v2 Launcher

The v2 Launcher (2.14.2.0 and older) cannot read a tag like `v3.0.0-test9`: it runs `Version.Parse(tag)` unguarded and crashes. It also wants one flat zip.
Run **Actions > Release > Run workflow** with `publish` ticked and `legacy_tag` set:

| `legacy_tag` | Result |
|---|---|
| empty | the normal release: tag `v<version>`, no flat zip. **Do not use it on `CyanLabs/Syn3Updater`** while v2 Launchers exist |
| `auto` | the next free `2.99.99.N` (numbered 1 to 999, compared as numbers: 2.99.99.2 is lower than 2.99.99.12), for test builds |
| an exact tag such as `3.0.0.0` | that tag, for the real release (it fails at once if the tag or release already exists) |

With a legacy tag the workflow also builds `<tag>.zip`: a flat zip (no folder) holding the same files as the Windows package: `Syn3Updater.exe`, the real hybrid Launcher
(`Launcher.exe` plus `Launcher.exe.config`, `Launcher.Core.dll`, `FluentWPF.dll`, `Newtonsoft.Json.dll`, and the `Launcher.pdb` and `Launcher.Core.pdb` symbols), LICENSE and README.txt, uploaded as `application/x-zip-compressed`
(the old Launcher's download picks the first asset of that exact type). The release is created as a **draft**, the zip is attached and its content type checked, and only then
is it published, so an old Launcher never sees a release without its zip. The release title stays `Syn3 Updater <version>`: the in-app update check reads the real version
(`3.0.0-test9`) from the title of a pre-release under a bare numeric tag. The release is flagged pre-release when the version has a suffix, and only Launchers on the Beta channel see pre-releases.

Why `2.99.99.N`: the old Launcher installs any release whose tag merely *differs* from what it has, while the 2.14.2.1+ Launcher offers the **highest version number**.
`2.99.99.N` is higher than every v2 release (2.14.2.1) so both see test builds, and lower than the real `3.0.0.0`, so that release is still the newest.
Keep real releases above `2.99.99.999`.
