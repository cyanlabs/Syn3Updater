#!/usr/bin/env bash
# Lays out the file tree shared by the .deb, .rpm and AUR packages from a Syn3Updater-<version>-<rid>.tar.gz:
#   /opt/syn3updater/Syn3Updater                      the program (file name has a capital S, as in the tarball)
#   /usr/bin/syn3updater                              lower-case command -> the program
#   /usr/share/applications/syn3updater.desktop       launcher (Exec is the exact path of the program)
#   /usr/share/icons/hicolor/128x128/apps/syn3updater.png
#   licence: /usr/share/doc/syn3updater/copyright (deb) or /usr/share/licenses/syn3updater/LICENSE (rpm, arch)
# usage: scripts/stage-linux-root.sh <tarball> <destdir> <deb|rpm|arch>
set -euo pipefail
TARBALL="$(readlink -f "${1:?path to Syn3Updater-<version>-<rid>.tar.gz}")"; DEST="${2:?destination directory}"; FLAVOR="${3:?deb, rpm or arch}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
tar -xzf "$TARBALL" -C "$work"
src="$(find "$work" -mindepth 1 -maxdepth 1 -type d | head -1)"
[[ -x "$src/Syn3Updater" ]] || { echo "Syn3Updater was not found in $TARBALL" >&2; exit 1; }

install -Dm755 "$src/Syn3Updater" "$DEST/opt/syn3updater/Syn3Updater"
install -d "$DEST/usr/bin"; ln -sfn /opt/syn3updater/Syn3Updater "$DEST/usr/bin/syn3updater"
install -Dm644 "$src/syn3updater.png" "$DEST/usr/share/icons/hicolor/128x128/apps/syn3updater.png"
install -Dm644 "$HERE/packaging/linux/syn3updater.desktop" "$DEST/usr/share/applications/syn3updater.desktop"
case "$FLAVOR" in
  deb) lic="usr/share/doc/syn3updater/copyright" ;;
  rpm|arch) lic="usr/share/licenses/syn3updater/LICENSE" ;;
  *) echo "unknown flavor $FLAVOR" >&2; exit 1 ;;
esac
install -Dm644 "$src/LICENSE" "$DEST/$lic"
