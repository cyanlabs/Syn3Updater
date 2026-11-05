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
| Windows | PowerShell storage cmdlets | `diskpart` (app requests Administrator) |

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

## UI previews (no window)

`dotnet run --project tools/Shots -- out 1100 800 Home Settings --demo [--light]` renders pages to PNG with Avalonia's
headless backend (nothing is shown on screen). Useful for checking layouts at different widths and themes.

## Tests

`dotnet test` runs ~150 tests: core logic, view-models driven through the real workflow with a fake API/dialogs, an end-to-end
download/copy run against a local HTTP server, and a live-API check (runs when a token is compiled in).
`scripts/coverage.sh` prints per-class line coverage (about 95% excluding the third-party partition algorithm, XAML-generated
code, the OS window dialogs and macOS `diskutil` calls).

Licensed GPL-3.0 (see `LICENSE`).
