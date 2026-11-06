#!/usr/bin/env bash
# Points packaging/aur/PKGBUILD at a published release: sets pkgver (pkgrel=1) and the sha256 sums from that release's SHA256SUMS,
# then regenerates .SRCINFO (needs makepkg, i.e. run it on Arch or in an Arch container).
# usage: scripts/update-aur-package.sh <version, e.g. 3.0.0>
set -euo pipefail
VERSION="${1:?stable version, e.g. 3.0.0}"
[[ "$VERSION" =~ ^[0-9]+(\.[0-9]+)*$ ]] || { echo "the AUR needs a stable version like 3.0.0 (no hyphen), got '$VERSION'" >&2; exit 1; }
cd "$(dirname "$0")/../packaging/aur"
sums="$(curl -fsSL "https://github.com/CyanLabs/Syn3Updater/releases/download/v$VERSION/SHA256SUMS")"
sum_of() { awk -v f="$1" '$2 == f { print $1 }' <<<"$sums"; }
x64="$(sum_of "Syn3Updater-$VERSION-linux-x64.tar.gz")"; arm="$(sum_of "Syn3Updater-$VERSION-linux-arm64.tar.gz")"
[[ -n "$x64" && -n "$arm" ]] || { echo "SHA256SUMS of v$VERSION lacks the linux-x64 / linux-arm64 tarballs" >&2; exit 1; }
sed -i -e "s/^pkgver=.*/pkgver=$VERSION/" -e "s/^pkgrel=.*/pkgrel=1/" \
       -e "s/^sha256sums_x86_64=.*/sha256sums_x86_64=('$x64')/" -e "s/^sha256sums_aarch64=.*/sha256sums_aarch64=('$arm')/" PKGBUILD
if command -v makepkg >/dev/null 2>&1; then makepkg --printsrcinfo > .SRCINFO; echo "wrote .SRCINFO"; else echo "makepkg not found: regenerate .SRCINFO on Arch with: makepkg --printsrcinfo > .SRCINFO"; fi
echo "PKGBUILD now builds Syn3 Updater $VERSION"
