# AUR package: syn3updater-bin

A prebuilt-binary package built from the GitHub release tarballs. **Not published yet.**

The same recipe already produces a native package without the AUR: every release attaches `Syn3Updater-<version>-linux-x64.pkg.tar.zst`
(`scripts/package-arch.sh`), which Arch users install with `sudo pacman -U`. It is named `syn3updater-bin` as well, so installing from the AUR later is an ordinary upgrade.

## Publishing it (once a stable release exists)
1. Create an account on https://aur.archlinux.org and add your SSH public key.
2. `git clone ssh://aur@aur.archlinux.org/syn3updater-bin.git` (an empty repository the first time).
3. Run `scripts/update-aur-package.sh <version>` from this repository: it takes the published `SHA256SUMS` of release `v<version>`,
   writes the version and checksums into `PKGBUILD` and regenerates `.SRCINFO`.
4. Copy `PKGBUILD` and `.SRCINFO` into the clone, commit and `git push`.

Only publish stable releases (the AUR version cannot contain a hyphen, so pre-releases such as `3.0.0-test6` do not fit).

## Checking it
`scripts/verify-aur-package.sh <Syn3Updater-...-linux-x64.tar.gz>` builds the PKGBUILD in an Arch Linux container against a local tarball,
installs the result and runs the same checks as the .deb/.rpm.
