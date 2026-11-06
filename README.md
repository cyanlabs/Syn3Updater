# Syn3 Updater (cross-platform)

A cross-platform (Linux, Windows, macOS) port of CyanLabs' Syn3 Updater, rebuilt on
.NET 10 + [Avalonia](https://avaloniaui.net). It prepares USB drives with the Ford SYNC 3
update packages: pick region / release / maps, and it downloads, checksum-verifies, copies and
writes `autoinstall.lst` / `reformat.lst` for the head unit.

## Run

```sh
scripts/run.sh                 # from source (needs the .NET 10 SDK; ~/.dotnet is used if present)
scripts/install-linux.sh       # build + install a launcher entry (Linux)
scripts/publish.sh             # self-contained builds for all platforms -> artifacts/<rid>/
```

## API token (build-time, never committed)

The CyanLabs backend needs a bearer token. It is compiled into the app at build time from the git-ignored file
`secrets/api-token.txt` (one line), or from the `SYN3_API_TOKEN` environment variable (wins, handy for CI).
See `secrets/README.md`. It is not a user setting and never appears in the UI or in git.
Without a token the app still builds, but API calls are rejected.

## Platform notes

| | Drive listing | Formatting (exFAT, MBR, label `CYANLABS`) |
|---|---|---|
| Linux | `lsblk` (USB/hotplug disks only; never a disk holding `/`, `/home`, `/boot`) | `sfdisk` + `mkfs.exfat` via `pkexec` (needs `exfatprogs`, `polkit`, `udisks2` to mount) |
| macOS | `diskutil` (external only) | `diskutil eraseDisk` |
| Windows | PowerShell storage cmdlets | PowerShell storage cmdlets (`Clear-Disk`, `Initialize-Disk`, `New-Partition`, `Format-Volume`); `diskpart` only if the cmdlets are missing (app requests Administrator) |

**Safe eject** (button beside Refresh on Home and Utilities, on the Downloads page, and offered when a run finishes):

| | Eject |
|---|---|
| Linux | `sync`, then `udisksctl unmount` for every partition and `udisksctl power-off` (needs `udisks2`; falls back to `umount`) |
| macOS | `diskutil eject` |
| Windows | flush the volume cache, then the shell "Eject" verb; confirms the drive has disappeared |

Only drives the app itself would list (USB / external, never a system disk) can be ejected, and the command sequences for all
three platforms are covered by tests that script the OS commands. Before any run the app also checks there is enough free space in
the download folder and on the drive (accounting for files already downloaded or already on the stick).

Before any format the target device is re-validated and the confirmation dialog names the exact device.
Only the Linux path has been run on real hardware by the author of this port; the Windows and macOS
drive code is covered by parser unit tests but untested against real devices.

## Differences from the original

- Redesigned UI: labelled sidebar that collapses on narrow windows, a guided 1-2-3 Home flow with a sticky action bar, live download queue, light/dark themes, settings that save when you leave the tab.
- Cross-platform UI (Avalonia) instead of WPF; no Windows launcher/updater or `syn3updater://` registry handler.
- Settings live in `~/.local/share/CyanLabs/Syn3Updater` (Linux), `%LOCALAPPDATA%` (Windows); macOS uses .NET LocalApplicationData (`~/.local/share`).
- GraphQL via plain HTTP + System.Text.Json (no GraphQL client dependency); downloads write to `*.part` and rename on success.
- `autoinstall.lst`/`reformat.lst` are always written with CRLF line endings.
- The interrogator tool choice covers the version gaps the original left unmatched (it would have used a null package).
- "Create 2.2 Upgrade USB" (an empty stub in the source) is implemented against the `upgrade22` package.

## CI/CD (GitHub Actions)

- **CI** (`.github/workflows/ci.yml`): every push and pull request to `main` builds and tests on Linux, Windows and macOS.
- **Release** (`.github/workflows/release.yml`): pushing a tag like `v3.0.0` runs the tests, builds every package and publishes a GitHub Release
  with checksums. It can also be started by hand (Actions > Release > Run workflow) to build packages without publishing. It also builds the Nightly channel (see *Update channels*).

| Package | Built by | Notes |
|---|---|---|
| `Syn3Updater-<v>-linux-x64.tar.gz`, `-linux-arm64` | `scripts/package-linux.sh` | single file + `install.sh` / `uninstall.sh` (user-level install; the launcher entry is generated with the real path; checks for the tools USB preparation needs) |
| `Syn3Updater-<v>-linux-x64.deb`, `-linux-arm64` | `scripts/package-deb.sh` | installs to `/opt/syn3updater` with a `/usr/bin/syn3updater` command and an application-menu entry; depends on the USB tools and runtime libraries (Debian 11+, Ubuntu 20.04+) |
| `Syn3Updater-<v>-linux-x64.rpm`, `-linux-arm64` | `scripts/package-rpm.sh` | same layout for Fedora / Nobara / RHEL-family; pre-releases get `Release: 0.<pre>.1` so the final release upgrades them |
| `Syn3Updater-<v>-linux-x64.pkg.tar.zst`, `-linux-arm64` | `scripts/package-arch.sh` | native Arch package: `sudo pacman -U <file>` (also Manjaro, Omarchy, CachyOS...). Built from the AUR recipe in an Arch container; named `syn3updater-bin`, so installing from the AUR later is a normal upgrade. A pre-release `3.0.0-test7` is versioned `3.0.0test7`, which pacman sorts before `3.0.0`. Not in a repository, so pacman never offers updates |
| AUR `syn3updater-bin` (not published yet) | `packaging/aur/PKGBUILD`, `scripts/update-aur-package.sh` | uses the release tarballs; see `packaging/aur/README.md` |
| `Syn3Updater-<v>-win-x64.zip`, `-win-arm64` | `scripts/package-windows.sh` | single `Syn3Updater.exe` (asks for administrator rights to format drives) |
| `Syn3Updater-<v>-macos-arm64.dmg`, `-macos-x64` | `scripts/package-macos.sh` (macOS only) | `.app` bundle in a DMG |

CI installs the `.deb`, `.rpm` and Arch package in real Debian, Ubuntu, Fedora and Arch containers (and builds the AUR package in Arch Linux) before a release is published, so wrong dependency names or a broken launcher stop it.

Repository **secrets** (Settings > Secrets and variables > Actions):

| Secret | Needed for |
|---|---|
| `SYN3_API_TOKEN` | the CyanLabs API token compiled into every build (without it the app builds but cannot reach the API) |
| `MACOS_CERTIFICATE`, `MACOS_CERTIFICATE_PASSWORD`, `MACOS_CODESIGN_IDENTITY` | optional: sign the macOS app with a Developer ID (base64 `.p12`, its password, and the identity name) |
| `NOTARY_APPLE_ID`, `NOTARY_TEAM_ID`, `NOTARY_PASSWORD` | optional: notarize the signed app (an app-specific password) |

Without the macOS secrets the app is ad-hoc signed: it runs, but users must right-click > Open the first time.
A token compiled into a public download can be extracted by anyone, so keep it read-only and revocable.

## Diagnostics, logs and updates

- **Diagnostics** (sidebar): checks the tools and permissions the current OS needs (Linux: `lsblk`, `sfdisk`, `mkfs.exfat`, `pkexec`, `udisks2`; Windows: Administrator, PowerShell, `diskpart`;
  macOS: `diskutil`, app location), the data/download/log folders, the API key, the Syn3 Updater and Ford servers and the clock. Each failed check says how to fix it. "Copy report" puts it on the clipboard.
- **Logs** now start with a system section (OS, architecture, runtime, desktop/session, install location, which tools are present, free space). The home folder is replaced by `~` so logs are safe to share.
- **Update notice**: on start-up the app asks GitHub for a newer build on your update channel (below) and shows a banner with a link. It never downloads or installs anything. The check is a single request to GitHub and is always on.
- **Drive verification**: after copying and flushing, every file on the stick is read back and checked against the download (Linux and macOS bypass the OS cache for this read).
- **Languages** live in `src/Syn3Updater.Core/Languages/*.json`. A test keeps every language complete and in step with `en-US`, and fails on keys nothing uses or text hardcoded in XAML.

## Update channels

Settings > Update channel decides which builds the update notice offers. The channel a build came from is read from its version, so nothing else has to be stored:

| Channel | Gets | Version looks like | Published as |
|---|---|---|---|
| **Stable** (default for releases) | tested releases | `3.0.0` | a normal GitHub release on `CyanLabs/Syn3Updater` |
| **Beta** (default for test builds) | the newest release *or* pre-release | `3.1.0-beta.2`, `3.0.0-test8` | a pre-release on `CyanLabs/Syn3Updater` |
| **Nightly** | automatic, untested builds of the latest code | `3.1.0-nightly.20261108.5` | one rolling pre-release named `nightly` on `CyanLabs/Syn3UpdaterDev` only |

- A newer build in your own channel is a normal update. Choosing a *different* channel offers that channel's newest build even if its number is lower (as in v2): for example a Beta tester who picks Stable is offered the latest stable release.
- Nightlies are built by `release.yml` at 03:17 UTC, only when `main` changed since the last one and only in the Dev repo (it can also be run by hand with "nightly"). The version is `<Version in Directory.Build.props>-nightly.<date>.<run number>`,
  and the app reads it from the release title because the tag is always `nightly`. They are kept out of the production repo on purpose: the v2 Launcher treats that repo's newest pre-release as its Beta update.
- The word sent to the API and written in logs as the "branch" is the channel of the running build (`Stable`, `Beta` or `Nightly`).
- **Windows: the Launcher follows this setting.** Like v2's Settings page, choosing a channel writes `ReleaseBranch` into the Launcher's `C:\ProgramData\CyanLabs\Syn3Updater\launcherPrefs.json`
  (0 Stable, 1 Beta, 2 Alpha = Nightly: v2's unfinished third slot is v3's Nightly; the install record next to it is never touched), so the next Launcher start installs that channel.
  Be aware the Launchers only read the production repo, which has no nightlies: the 2.14.2.1+ Launcher treats Alpha like Beta, and the older 2.14.2.0 Launcher treats anything but Beta as Stable.
  If the file says something different from the app (changed by v2 or the Launcher), the file wins, as in v2. It is only written when that file or a `Launcher.exe` beside the app exists, and never on Linux or macOS.

## Releasing for the v2 Launcher

The v2 Launcher (2.14.2.0 and older) cannot read a tag like `v3.0.0-test9`: it runs `Version.Parse(tag)` unguarded and crashes. It also wants one flat zip. Run **Actions > Release > Run workflow** with `publish` ticked and `legacy_tag` set:

| `legacy_tag` | Result |
|---|---|
| empty | the normal release: tag `v<version>`, no flat zip. Fine for the Dev repo and for people who do not use the Launcher; **do not use it on `CyanLabs/Syn3Updater`** while v2 Launchers exist |
| `auto` | the next free `2.99.99.N` (numbered 1 to 999, compared as numbers: 2.99.99.2 is lower than 2.99.99.12), for test builds |
| an exact tag such as `3.0.0.0` | that tag, for the real release (it fails at once if the tag or release already exists) |

With a legacy tag the workflow also builds `<tag>.zip`: a flat zip with `Syn3Updater.exe` and a copy of it as `Launcher.exe` (plus LICENSE and README.txt), uploaded as `application/x-zip-compressed`
(the old Launcher's download picks the first asset of that exact type). The release is created as a **draft**, the zip is attached and its content type checked, and only then is it published,
so an old Launcher never sees a release without its zip. The release title stays `Syn3 Updater <version>`: the in-app update check reads the real version (`3.0.0-test9`) from the title of a pre-release
under a bare numeric tag. The release is flagged pre-release when the version has a suffix, and only Launchers on the Beta channel see pre-releases.

Why `2.99.99.N`: the old Launcher installs any release whose tag merely *differs* from what it has, while the 2.14.2.1+ Launcher offers the **highest version number**. `2.99.99.N` is higher than every v2 release (2.14.2.1)
so both see test builds, and lower than the real `3.0.0.0`, so that release is still the newest. Keep real releases above `2.99.99.999`.

## syn3updater:// links

Links such as `syn3updater://Region=EU;Release=3.4.19274;Maps=Navigation_Map_Name` (on the CyanLabs website and forum, as in v2) open the app and
preselect the region, SYNC release (`Sync <Release>`) and maps (underscores stand for spaces) on Home. Like v2, a link needs `Region` to do anything.
A link can only choose among the options the app already offers: it never presses Start, never formats a drive and cannot name a file or path.
If a link arrives while an update is running it is refused with the usual "another operation is running" message.

- **One copy at a time:** a second launch (for example clicking a link while the app is open) hands the link to the running copy and exits.
- **Windows:** the app registers itself for the current user (`HKCU\Software\Classes\syn3updater`, no administrator rights) each time it starts, pointing at the running `Syn3Updater.exe`, as v2 did.
- **Linux:** `install.sh` and the packages declare `x-scheme-handler/syn3updater` in the launcher entry; running `./Syn3Updater` straight from the archive does not register anything.
- **macOS:** declared in the app bundle's `Info.plist`.

## UI previews (no window)

`dotnet run --project tools/Shots -- out 1100 800 Home Settings --demo [--light]` renders pages to PNG with Avalonia's
headless backend (nothing is shown on screen). Useful for checking layouts at different widths and themes.

## Tests

`dotnet test` runs ~150 tests: core logic, view-models driven through the real workflow with a fake API/dialogs, an end-to-end
download/copy run against a local HTTP server, and a live-API check (runs when a token is compiled in).
`scripts/coverage.sh` prints per-class line coverage (about 95% excluding the third-party partition algorithm, XAML-generated
code, the OS window dialogs and macOS `diskutil` calls).

Licensed GPL-3.0 (see `LICENSE`).
