# Launcher.Core — the update engine behind the new Launcher

The WPF `Launcher` is a thin shell; every decision lives here so it can be unit-tested on any OS
(`dotnet test Launcher.Core.Tests`).

## What it does on each start
1. Rolls back an interrupted update (a `.update-backup` folder without a `COMPLETE` marker).
2. Reads the GitHub releases of `cyanlabs/Syn3Updater` and picks the **newest release by version** for the user's channel
   (Stable = full releases; Beta/Alpha = full + pre-releases) that this PC can run (v3 needs Windows 10 1607+; older systems stay on the newest v2).
3. Installs only if the release is **newer**, the app is missing, or the user switched channel. (The old launcher installed anything that merely differed.)
4. Downloads, **verifies the SHA-256** (GitHub's `digest`, a `SHA256SUMS` asset, or `<zip>.sha256`; skipped with a log line if none is published),
   unpacks into `.update-staging` and checks the package contains `Syn3Updater.exe`.
5. Across a **major-version jump** (v2 → v3) copies settings aside to `%ProgramData%\CyanLabs\Syn3Updater\Backups` (newest 3 kept).
6. Closes the app and swaps the files **all-or-nothing**: every replaced/removed file is moved into `.update-backup` first; any failure restores them.
   Locked files are retried. The running `Launcher.exe` is replaced the same way (renamed aside, which Windows allows).
7. Only then records the new version in `launcherPrefs.json` (always four numeric parts, e.g. `3.0.0.0`) and writes `install-manifest.json`.

Log: `%ProgramData%\CyanLabs\Syn3Updater\launcher.log`.

## What a release must look like
Both packagings are understood, so the existing v2 flow and the v3 CI output both work:

| | v2 style | v3 style |
|---|---|---|
| tag | `2.14.2.0` | `3.0.0.0`, `v3.0.0`, `3.0.0-beta.1` |
| asset | one `<version>.zip` (`application/x-zip-compressed`) | `Syn3Updater-<version>-win-x64.zip` / `-win-arm64.zip` |
| layout | flat | flat **or** one top-level folder |
| checksum | none | `SHA256SUMS` or GitHub's digest |

The zip must contain `Syn3Updater.exe`. Ship a `Launcher.exe` too if the launcher itself should be replaced; the old launcher renames itself away first,
so a zip without one would leave the shortcuts pointing at nothing.

**Until every user has this launcher**, releases published to the shared repo must still follow the *old* launcher's rules
(bare numeric tag such as `3.0.0.0`, content type `application/x-zip-compressed`, flat zip, `Launcher.exe` included), because that is the code that will install this one.
