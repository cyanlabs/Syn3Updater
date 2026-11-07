# Syn3 Updater

Prepare a USB drive to update the Ford SYNC 3 system in your car. Pick your region, SYNC release and maps, and Syn3 Updater
downloads the packages, checks them, copies them to the drive and writes the `autoinstall.lst` / `reformat.lst` the head unit needs.

This is v3 of CyanLabs' Syn3 Updater: a cross-platform rewrite (Windows, macOS, Linux) on .NET 10 and [Avalonia](https://avaloniaui.net).

## Install

Download the file for your system from the [Releases page](https://github.com/cyanlabs/Syn3Updater/releases). To check a download,
compare its checksum with `SHA256SUMS`: `sha256sum -c --ignore-missing SHA256SUMS`. Replace `<v>` below with the version (for example `3.0.0`).

### Windows

Download `Syn3Updater-<v>-win-x64.zip` (`win-arm64` on ARM PCs), extract it and run `Syn3Updater.exe`. It asks for Administrator rights,
which it needs to format USB drives. There is no installer: delete the folder to remove it. The app is not code-signed, so SmartScreen may
warn: click *More info*, then *Run anyway*.

### macOS

Download `Syn3Updater-<v>-macos-arm64.dmg` (Apple Silicon) or `-macos-x64` (Intel), open it and drag *Syn3 Updater* onto *Applications*.
The first time, right-click the app and choose *Open*, then *Open* again (the app is only ad-hoc signed).

### Linux

The `.deb`, `.rpm` and Arch packages install the USB tools the app needs (`util-linux`, `udisks2`, `polkit`, `exfatprogs`). Use the `linux-arm64` files on ARM.

| Distribution | Install | Remove |
|---|---|---|
| Debian, Ubuntu, Mint, Pop!_OS (`.deb`) | `sudo apt install ./Syn3Updater-<v>-linux-x64.deb` | `sudo apt remove syn3updater` |
| Fedora, Nobara, RHEL family (`.rpm`) | `sudo dnf install ./Syn3Updater-<v>-linux-x64.rpm` | `sudo dnf remove syn3updater` |
| Arch, Manjaro, CachyOS, Omarchy (`.pkg.tar.zst`) | download the file, then `sudo pacman -U ./Syn3Updater-<v>-linux-x64.pkg.tar.zst` | `sudo pacman -R syn3updater-bin` |
| Anything else (`.tar.gz`) | `tar xzf Syn3Updater-<v>-linux-x64.tar.gz && cd Syn3Updater-<v>-linux-x64 && ./install.sh` | `./uninstall.sh` |

- **Arch:** download the package first and install the local file. Installing straight from the URL fails because releases carry no signature file.
  The package is not in a repository, so pacman never offers updates: repeat the steps for a new version. An AUR package (`syn3updater-bin`) is planned.
- **`.tar.gz`:** installs for your user only (no `sudo`) and adds a launcher entry. `./Syn3Updater` also runs straight from the folder,
  but that does not register the `syn3updater://` link handler.

Start the app from the application menu (on Linux also with `syn3updater` in a terminal).

## Usage

See the guide at <https://cyanlabs.net/docs/Syn3Updater>.

The install mode (AutoInstall, Reformat, Downgrade) is chosen automatically and protects MY20 units. The rules are in [docs/install-mode-logic.md](docs/install-mode-logic.md).

**Diagnostics** (sidebar) checks the tools and permissions your OS needs, the data / download / log folders, the API key, the Syn3 Updater and Ford servers and the clock.
Each failed check says how to fix it, and *Copy report* puts the result on the clipboard.

> Linux is the only platform the author has run on real hardware. The Windows and macOS drive code is covered by tests but has not been tried against real devices,
> so please report problems.

### Logs

Logs start with a system section (OS, architecture, runtime, desktop, install location, available tools, free space). The home folder is replaced by `~`, so logs are safe to share.

### Updates

On start-up the app asks GitHub for a newer build on your update channel and shows a banner with a link. It never downloads or installs anything.
Choose the channel in *Settings > Update channel*:

| Channel | Gets |
|---|---|
| **Stable** (default for releases) | tested releases, e.g. `3.0.0` |
| **Beta** (default for test builds) | the newest release or pre-release, e.g. `3.0.0-test8` |
| **Nightly** | not available yet |

Choosing a different channel offers that channel's newest build even if its number is lower, for example a Beta tester who picks Stable is offered the latest stable release.

### Crash reports

If the app crashes, the next start asks whether to send a crash report. Nothing is sent unless you click *Yes*, and the question is asked once per crash.
A report holds the error and where it happened, the app version and update channel, and your operating system. Your user name and home folder are removed.
If you had opened a car's log, it also holds that car's VIN, which helps with troubleshooting. You can also add a short comment about what you were doing (optional; email addresses and your home folder are removed from it). It goes to CyanLabs' own [GlitchTip](https://glitchtip.com) server.

### `syn3updater://` links

Links such as `syn3updater://Region=EU;Release=3.4.19274;Maps=Navigation_Map_Name` (used on the CyanLabs website and forum) open the app and preselect the
region, release and maps. A link only chooses options: it never starts a run, never formats a drive and cannot name a file or path.

## Documentation

- [Building and testing](docs/BUILDING.md): build from source, the API token, tests, UI previews
- [Releasing](docs/RELEASING.md): CI, the package formats, secrets, update channels, the v2 Launcher
- [Platform behaviour](docs/PLATFORMS.md): how drives are listed, formatted and ejected on each OS, and how `syn3updater://` is registered
- [Differences from v2](docs/V2-DIFFERENCES.md)
- [Install mode logic](docs/install-mode-logic.md)

## License

GPL-3.0, see [LICENSE](LICENSE).
