#!/usr/bin/env bash
# Installs the .deb and .rpm in real distro containers (apt / dnf resolve the declared dependencies for real), runs
# verify-installed-linux-package.sh inside, then removes the package and checks nothing is left. Needs docker; x86_64 packages only.
# usage: scripts/verify-linux-distro-packages.sh <dir with Syn3Updater-*-linux-x64.deb/.rpm> [image ...]
set -euo pipefail
DIR="$(readlink -f "${1:?directory with the packages}")"; shift || true
HERE="$(cd "$(dirname "$0")" && pwd)"
DEB="$(ls "$DIR"/Syn3Updater-*-linux-x64.deb 2>/dev/null | head -1 || true)"; RPM="$(ls "$DIR"/Syn3Updater-*-linux-x64.rpm 2>/dev/null | head -1 || true)"
IMAGES=("$@"); ((${#IMAGES[@]})) || IMAGES=(debian:12-slim ubuntu:24.04 fedora:latest)

for image in "${IMAGES[@]}"; do
  case "$image" in
    fedora*|rockylinux*|almalinux*|opensuse*)
      [[ -n "$RPM" ]] || { echo "no .rpm found, skipping $image"; continue; }
      docker run --rm -v "$RPM":"/pkg/$(basename "$RPM")":ro -v "$HERE":/scripts:ro "$image" bash -ec "
        dnf install -y -q /pkg/$(basename "$RPM") desktop-file-utils
        /scripts/verify-installed-linux-package.sh
        rpm -V syn3updater && echo 'ok: rpm -V clean'
        dnf remove -y -q syn3updater
        [[ ! -e /opt/syn3updater && ! -e /usr/bin/syn3updater && ! -e /usr/share/applications/syn3updater.desktop ]] || { echo 'FAIL: removal left files behind' >&2; exit 1; }
        echo 'ok: removed cleanly'" ;;
    *)
      [[ -n "$DEB" ]] || { echo "no .deb found, skipping $image"; continue; }
      echo "=== $image: $(basename "$DEB")"
      docker run --rm -e DEBIAN_FRONTEND=noninteractive -v "$DEB":"/pkg/$(basename "$DEB")":ro -v "$HERE":/scripts:ro "$image" bash -ec "
        apt-get update -qq >/dev/null
        apt-get install -y -qq /pkg/$(basename "$DEB") desktop-file-utils >/dev/null
        /scripts/verify-installed-linux-package.sh
        dpkg -s syn3updater | grep -E '^(Version|Depends)'
        apt-get remove -y -qq syn3updater >/dev/null
        [[ ! -e /opt/syn3updater && ! -e /usr/bin/syn3updater && ! -e /usr/share/applications/syn3updater.desktop ]] || { echo 'FAIL: removal left files behind' >&2; exit 1; }
        echo 'ok: removed cleanly'" ;;
  esac
done
echo "all distro packages verified"
