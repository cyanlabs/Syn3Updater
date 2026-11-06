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
  with checksums. It can also be started by hand (Actions > Release > Run workflow) to build packages without publishing.

| Package | Built by | Notes |
|---|---|---|
| `Syn3Updater-<v>-linux-x64.tar.gz`, `-linux-arm64` | `scripts/package-linux.sh` | single file + `install.sh` / `uninstall.sh` (user-level install; the launcher entry is generated with the real path; checks for the tools USB preparation needs) |
| `Syn3Updater-<v>-win-x64.zip`, `-win-arm64` | `scripts/package-windows.sh` | single `Syn3Updater.exe` (asks for administrator rights to format drives) |
| `Syn3Updater-<v>-macos-arm64.dmg`, `-macos-x64` | `scripts/package-macos.sh` (macOS only) | `.app` bundle in a DMG |

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
- **Update notice**: on start-up the app asks GitHub for newer releases of `CyanLabs/Syn3Updater` and shows a banner with a link. It never downloads or installs anything. Turn it off in Settings.
  Pre-releases are only offered to people already on a pre-release.
- **Drive verification**: after copying and flushing, every file on the stick is read back and checked against the download (Linux and macOS bypass the OS cache for this read).
- **Languages** live in `src/Syn3Updater.Core/Languages/*.json`. A test keeps every language complete and in step with `en-US`, and fails on keys nothing uses or text hardcoded in XAML.

## UI previews (no window)

`dotnet run --project tools/Shots -- out 1100 800 Home Settings --demo [--light]` renders pages to PNG with Avalonia's
headless backend (nothing is shown on screen). Useful for checking layouts at different widths and themes.

## Tests

`dotnet test` runs ~150 tests: core logic, view-models driven through the real workflow with a fake API/dialogs, an end-to-end
download/copy run against a local HTTP server, and a live-API check (runs when a token is compiled in).
`scripts/coverage.sh` prints per-class line coverage (about 95% excluding the third-party partition algorithm, XAML-generated
code, the OS window dialogs and macOS `diskutil` calls).

Licensed GPL-3.0 (see `LICENSE`).
