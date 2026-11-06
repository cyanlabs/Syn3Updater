# Differences from v2

- **UI:** redesigned, with a labelled sidebar that collapses on narrow windows, a guided 1-2-3 Home flow with a sticky action bar, a live download queue, light / dark themes,
  and settings that save when you leave the tab. It is built on Avalonia instead of WPF, so it is cross-platform.
- **Windows Launcher:** the real v2 "hybrid" Launcher (`packaging/windows/launcher/`) ships next to `Syn3Updater.exe` and keeps the app up to date. `syn3updater://` links are registered by the app itself at start-up.
- **Settings location:** `~/.local/share/CyanLabs/Syn3Updater` (Linux), `%LOCALAPPDATA%` (Windows); macOS uses .NET LocalApplicationData (`~/.local/share`).
- **Networking:** GraphQL is plain HTTP + System.Text.Json (no GraphQL client dependency). Downloads write to `*.part` and rename on success.
- **Install files:** `autoinstall.lst` / `reformat.lst` are always written with CRLF line endings.
- **Interrogator:** the tool choice covers the version gaps the original left unmatched (it would have used a null package).
- **2.2 upgrade USB:** "Create 2.2 Upgrade USB" (an empty stub in the v2 source) is implemented against the `upgrade22` package.

## Reading v2 settings

v2 settings files are read as they are.

- The install mode must be `autoinstall`, `reformat` or `downgrade` (any case). Anything else falls back to auto-detect, including the text v2's Settings page saved for "Auto-Detect" in any language.
- `My20v2` keeps its meaning (null auto, true on, false off). A wrongly typed value counts as auto-detect instead of discarding the profile.
- As in v2, a fixed install mode only exists while MY20 protection is explicitly off. Turning protection on (or back to auto-detect), closing Advanced options, or the Interrogator log finding an MY20 unit
  resets the mode to auto-detect, and a profile with a fixed mode but protection not off is read as auto-detect. See [install-mode-logic.md](install-mode-logic.md).
- The download connections v2 kept in the profile file move once into the application settings (`settings.json`) and are no longer written to profiles.
