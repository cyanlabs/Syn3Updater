#!/usr/bin/env bash
# Builds Syn3Updater-<version>-<rid>.pkg.tar.zst (+ .sha256), a native Arch Linux package that `sudo pacman -U` installs, from the tarball
# made by scripts/package-linux.sh. It uses the same recipe as the AUR package (packaging/aur/PKGBUILD) inside an Arch Linux container,
# so it needs docker, and the package is named syn3updater-bin: a later install from the AUR is an ordinary upgrade.
# usage: scripts/package-arch.sh <linux-x64|linux-arm64> <version> [outdir] [tarball]
set -euo pipefail
RID="${1:?rid (linux-x64 or linux-arm64)}"; VERSION="${2:?version, e.g. 3.0.0}"; OUT="${3:-artifacts}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"; cd "$HERE"
TARBALL="$(readlink -f "${4:-$OUT/Syn3Updater-$VERSION-$RID.tar.gz}")"
case "$RID" in linux-x64) ARCH=x86_64 ;; linux-arm64) ARCH=aarch64 ;; *) echo "unsupported rid $RID" >&2; exit 1 ;; esac
# pacman versions cannot contain a hyphen. The suffix is joined on directly (3.0.0-test7 -> 3.0.0test7): `vercmp` then sorts it BEFORE 3.0.0, so the final
# release upgrades a test build. A "_" or "." separator (3.0.0_test7) would sort AFTER 3.0.0 and the final release would look like a downgrade.
PKGVER="${VERSION//-/}"
mkdir -p "$OUT"; OUT_ABS="$(readlink -f "$OUT")"
FILE="Syn3Updater-$VERSION-$RID.pkg.tar.zst"

docker run --rm -e RID="$RID" -e ARCH="$ARCH" -e VERSION="$VERSION" -e PKGVER="$PKGVER" -e FILE="$FILE" -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" \
  -v "$TARBALL":/in/pkg.tar.gz:ro -v "$HERE/packaging/aur":/aur:ro -v "$OUT_ABS":/out archlinux:latest bash -ec '
  pacman -Sy --noconfirm --needed base-devel >/dev/null
  useradd -m builder
  # the PKGBUILD unpacks Syn3Updater-<pkgver>-<rid>: repack the tarball under the pacman version, and point the recipe at it instead of GitHub
  mkdir -p /tmp/repack && tar -xzf /in/pkg.tar.gz -C /tmp/repack && mv /tmp/repack/Syn3Updater-* "/tmp/repack/Syn3Updater-$PKGVER-$RID"
  install -d -o builder -g builder /home/builder/b
  tar -C /tmp/repack -czf "/home/builder/b/Syn3Updater-$PKGVER-$RID.tar.gz" "Syn3Updater-$PKGVER-$RID"
  cp /aur/PKGBUILD /home/builder/b/PKGBUILD
  sed -i -e "s|^pkgver=.*|pkgver=$PKGVER|" -e "s|^pkgrel=.*|pkgrel=1|" \
         -e "s|^source_$ARCH=.*|source_$ARCH=(\"Syn3Updater-\${pkgver}-$RID.tar.gz\")|" -e "s|^sha256sums_$ARCH=.*|sha256sums_$ARCH=(SKIP)|" /home/builder/b/PKGBUILD
  flags=(-f --nodeps --noconfirm)
  # the package only contains prebuilt files, so an aarch64 package can be assembled on an x86_64 builder
  if [[ "$ARCH" != "$(uname -m)" ]]; then sed -i "s/^CARCH=.*/CARCH=\"$ARCH\"/" /etc/makepkg.conf; flags+=(--ignorearch); fi
  chown -R builder:builder /home/builder/b
  su builder -c "cd /home/builder/b && PACKAGER=\"CyanLabs <https://cyanlabs.net>\" makepkg ${flags[*]}" >/dev/null
  cp /home/builder/b/*.pkg.tar.zst "/out/$FILE"
  chown "$HOST_UID:$HOST_GID" "/out/$FILE"'

( cd "$OUT" && sha256sum "$FILE" > "$FILE.sha256" )
echo "built $OUT/$FILE (syn3updater-bin $PKGVER-1, $ARCH)"
