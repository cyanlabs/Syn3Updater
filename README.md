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

## API token

The CyanLabs backend needs a bearer token. It is **not** in this repository. Set it in
*Settings > CyanLabs API token* (stored in your per-user settings file), or export `SYN3_API_TOKEN`.

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

- Cross-platform UI (Avalonia) instead of WPF; no Windows launcher/updater or `syn3updater://` registry handler.
- Settings live in `~/.local/share/CyanLabs/Syn3Updater` (Linux), `%LOCALAPPDATA%` (Windows); macOS uses .NET LocalApplicationData (`~/.local/share`).
- GraphQL via plain HTTP + System.Text.Json (no GraphQL client dependency); downloads write to `*.part` and rename on success.
- `autoinstall.lst`/`reformat.lst` are always written with CRLF line endings.
- The interrogator tool choice covers the version gaps the original left unmatched (it would have used a null package).
- The unfinished "Upgrade to 2.2" utility (a stub upstream) is not included.

## Tests

`dotnet test` - includes an end-to-end run against a local HTTP server. Set `SYN3_API_TOKEN` to also run the live-API test.

Licensed GPL-3.0 (see `LICENSE`).
