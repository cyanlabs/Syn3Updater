#!/usr/bin/env bash
# Builds packaging/aur/PKGBUILD in an Arch Linux container against a LOCAL tarball (instead of a GitHub release), installs the result with
# pacman and runs verify-installed-linux-package.sh. Needs docker; x86_64 only.
# usage: scripts/verify-aur-package.sh <Syn3Updater-<version>-linux-x64.tar.gz>
set -euo pipefail
TARBALL="$(readlink -f "${1:?path to the linux-x64 tarball}")"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
docker run --rm -v "$TARBALL":/in/pkg.tar.gz:ro -v "$HERE/packaging":/packaging:ro -v "$HERE/scripts":/scripts:ro archlinux:latest bash -ec '
  pacman -Sy --noconfirm --needed base-devel >/dev/null
  useradd -m builder
  # the PKGBUILD names its tarball after pkgver: repack the test tarball under that version
  . /packaging/aur/PKGBUILD
  mkdir -p /tmp/repack && tar -xzf /in/pkg.tar.gz -C /tmp/repack && mv /tmp/repack/Syn3Updater-* "/tmp/repack/Syn3Updater-$pkgver-linux-x64"
  install -d -o builder -g builder /home/builder/b
  tar -C /tmp/repack -czf "/home/builder/b/Syn3Updater-$pkgver-linux-x64.tar.gz" "Syn3Updater-$pkgver-linux-x64"
  cp /packaging/aur/PKGBUILD /home/builder/b/PKGBUILD
  sed -i -e "s|^source_x86_64=.*|source_x86_64=(\"Syn3Updater-\${pkgver}-linux-x64.tar.gz\")|" -e "s|^sha256sums_x86_64=.*|sha256sums_x86_64=(SKIP)|" /home/builder/b/PKGBUILD
  chown -R builder:builder /home/builder/b
  pacman -S --noconfirm --needed "${depends[@]}" desktop-file-utils >/dev/null      # proves every dependency name exists
  su builder -c "cd /home/builder/b && makepkg -f --nodeps --noconfirm" >/dev/null
  pacman -U --noconfirm /home/builder/b/*.pkg.tar.zst >/dev/null
  /scripts/verify-installed-linux-package.sh
  cmp <(sed -n "/^\[Desktop Entry\]/,\$p" /packaging/linux/syn3updater.desktop) /usr/share/applications/syn3updater.desktop && echo "ok: desktop entry matches packaging/linux/syn3updater.desktop"
  pacman -R --noconfirm syn3updater-bin >/dev/null
  [[ ! -e /opt/syn3updater && ! -e /usr/bin/syn3updater && ! -e /usr/share/applications/syn3updater.desktop ]] || { echo "FAIL: removal left files behind" >&2; exit 1; }
  echo "ok: removed cleanly"'
echo "AUR package verified"
