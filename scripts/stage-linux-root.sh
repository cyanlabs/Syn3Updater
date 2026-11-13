#!/usr/bin/env bash
# Lays out the file tree shared by the .deb, .rpm and AUR packages from a Syn3Updater-<version>-<rid>.tar.gz:
#   /opt/syn3updater/Syn3Updater                      the program (file name has a capital S, as in the tarball)
#   /usr/bin/syn3updater                              lower-case command -> the program
#   /usr/share/applications/syn3updater.desktop       launcher (Exec is the exact path of the program)
#   /usr/share/icons/hicolor/<size>x<size>/apps/syn3updater.png   (16 to 512, each really that size)
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
# tells the app how it was installed, so it never tries to rewrite files its package manager owns (see src/Syn3Updater.Core/Services/Updates/InstallDetector.cs)
case "$FLAVOR" in arch) marker=pacman ;; *) marker="$FLAVOR" ;; esac
printf '%s\n' "$marker" > "$DEST/opt/syn3updater/.install-method"; chmod 644 "$DEST/opt/syn3updater/.install-method"
install -d "$DEST/usr/bin"; ln -sfn /opt/syn3updater/Syn3Updater "$DEST/usr/bin/syn3updater"
[[ -d "$src/icons/hicolor" ]] || { echo "no icons/hicolor in $TARBALL" >&2; exit 1; }
for icon in "$src"/icons/hicolor/*/apps/syn3updater.png; do
  size="$(basename "$(dirname "$(dirname "$icon")")")"
  install -Dm644 "$icon" "$DEST/usr/share/icons/hicolor/$size/apps/syn3updater.png"
done
install -Dm644 "$HERE/packaging/linux/syn3updater.desktop" "$DEST/usr/share/applications/syn3updater.desktop"
case "$FLAVOR" in
  deb) lic="usr/share/doc/syn3updater/copyright" ;;
  rpm|arch) lic="usr/share/licenses/syn3updater/LICENSE" ;;
  *) echo "unknown flavor $FLAVOR" >&2; exit 1 ;;
esac
install -Dm644 "$src/LICENSE" "$DEST/$lic"
