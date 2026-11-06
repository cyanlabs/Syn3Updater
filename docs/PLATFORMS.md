# Platform behaviour

## Drives

| | Drive listing | Formatting (exFAT, MBR, label `CYANLABS`) |
|---|---|---|
| Linux | `lsblk` (USB / hotplug disks only; never a disk holding `/`, `/home`, `/boot`) | `sfdisk` + `mkfs.exfat` via `pkexec` (needs `exfatprogs`, `polkit`, `udisks2` to mount) |
| macOS | `diskutil` (external only) | `diskutil eraseDisk` |
| Windows | PowerShell storage cmdlets | PowerShell storage cmdlets (`Clear-Disk`, `Initialize-Disk`, `New-Partition`, `Format-Volume`); `diskpart` only if the cmdlets are missing (the app requests Administrator) |

Before any format the target device is re-validated and the confirmation dialog names the exact device. Before any run the app also checks there is enough free space in the
download folder and on the drive (accounting for files already downloaded or already on the stick).

After copying and flushing, every file on the stick is read back and checked against the download (Linux and macOS bypass the OS cache for this read).

## Safe eject

The eject button sits beside Refresh on Home and Utilities and on the Downloads page, and is offered when a run finishes.

| | Eject |
|---|---|
| Linux | `sync`, then `udisksctl unmount` for every partition and `udisksctl power-off` (needs `udisks2`; falls back to `umount`) |
| macOS | `diskutil eject` |
| Windows | flush the volume cache, then the shell "Eject" verb; confirms the drive has disappeared |

Only drives the app itself would list (USB / external, never a system disk) can be ejected.

## Testing status

The command sequences for all three platforms are covered by tests that script the OS commands. Only the Linux path has been run on real hardware by the author;
the Windows and macOS drive code is covered by parser unit tests but untested against real devices.

## Diagnostics

The *Diagnostics* page checks, per OS:

- **Linux:** `lsblk`, `sfdisk`, `mkfs.exfat`, `pkexec`, `udisks2`
- **Windows:** Administrator, PowerShell, `diskpart`
- **macOS:** `diskutil`, app location

It also checks the data / download / log folders, the API key, the Syn3 Updater and Ford servers and the clock.

## `syn3updater://` links

- **One copy at a time:** a second launch (for example clicking a link while the app is open) hands the link to the running copy and exits.
- **Windows:** the app registers itself for the current user (`HKCU\Software\Classes\syn3updater`, no administrator rights) each time it starts, pointing at the running `Syn3Updater.exe`, as v2 did.
- **Linux:** `install.sh` and the packages declare `x-scheme-handler/syn3updater` in the launcher entry; running `./Syn3Updater` straight from the archive does not register anything.
- **macOS:** declared in the app bundle's `Info.plist`.

A link needs `Region` to do anything, as in v2. It selects among the options the app already offers (`Sync <Release>` for the release, underscores for spaces in map names).
If a link arrives while an update is running it is refused with the usual "another operation is running" message.
