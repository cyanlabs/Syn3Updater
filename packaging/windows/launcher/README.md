# Transition libraries of the old Windows Launcher

**Temporary: remove with the `Launcher.exe` stub, a few months after 3.0.0.0 (see `docs/RELEASING.md`).**

`Launcher.exe` in a Windows package is now a stub built from `src/Syn3Updater.Launcher`: it starts `Syn3Updater.exe`. These three files are the old hybrid Launcher's libraries,
built from the `v3launcher` branch (CI run 42368001163, commit `c1134c7`). They are shipped next to the stub only because a Launcher that is part-way through installing a package may
still load them after the swap, and the old v2 Launcher deletes every `.dll` a package does not ship. `SHA256SUMS` pins them; `package-windows.sh` refuses to build if any file differs.

| File | Why |
|---|---|
| `Launcher.Core.dll` | the old Launcher's update engine |
| `FluentWPF.dll`, `Newtonsoft.Json.dll` | the libraries it loads (Newtonsoft stays at 12.0.3, the version the old Launcher and the v2 app agree on) |

The updater's own engine (`src/Syn3Updater.Core/Services/Updates/Swap`) is a port of Launcher.Core's, with the same on-disk protocol, so the two can recover each other's interrupted update.
They are listed in the install manifest, so the release that stops shipping them removes them from installs.
