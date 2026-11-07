# The Windows Launcher (the "hybrid launcher")

These seven files are the .NET Framework 4.7.2 Launcher built from the `v3launcher` branch: the thin WPF shell over `Launcher.Core` that shipped in release 2.14.2.1.
Every Windows package (`scripts/package-windows.sh`) and the flat zip the old v2 Launcher installs (`scripts/make-legacy-zip.sh`) carry them next to `Syn3Updater.exe`, so `Launcher.exe` is the
real Launcher and not a copy of the app.

| File | Why |
|---|---|
| `Launcher.exe`, `Launcher.exe.config` | the Launcher and its runtime setting |
| `Launcher.Core.dll` | its update engine |
| `FluentWPF.dll`, `Newtonsoft.Json.dll` | the libraries it loads (Newtonsoft stays at 12.0.3, the version both the Launcher and the old app agree on) |
| `Launcher.pdb`, `Launcher.Core.pdb` | debug symbols, so Launcher crash stack traces carry file and line numbers |

**Provenance:** built by the `Launcher` workflow (CI run 42364628845, commit `0245c46`, job "Build the launcher (Windows)", artifact `Launcher-net472`). `SHA256SUMS` pins them; `package-windows.sh` refuses to build if any file differs.

**Why they must be in every package, not just the flat zip:** when the Launcher installs a v3 zip it removes old files the new package does not ship, and with no install manifest yet (the first run after the old
v2 Launcher installed us) that means every `.dll`. A package without `FluentWPF.dll`, `Launcher.Core.dll` and `Newtonsoft.Json.dll` would make the Launcher delete its own libraries and fail to start next time.

**Updating the Launcher:** build `v3launcher` (the workflow above), replace the seven files, regenerate `SHA256SUMS` (`sha256sum Launcher.exe Launcher.exe.config Launcher.Core.dll FluentWPF.dll Newtonsoft.Json.dll Launcher.pdb Launcher.Core.pdb > SHA256SUMS`)
and update the provenance paragraph. The new Launcher then reaches users with the next release.
