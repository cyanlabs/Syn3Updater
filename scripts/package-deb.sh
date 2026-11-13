#!/usr/bin/env bash
# Builds Syn3Updater-<version>-<rid>.deb (+ .sha256) from the tarball made by scripts/package-linux.sh. Needs dpkg-deb.
# usage: scripts/package-deb.sh <linux-x64|linux-arm64> <version> [outdir] [tarball]
set -euo pipefail
RID="${1:?rid (linux-x64 or linux-arm64)}"; VERSION="${2:?version, e.g. 3.0.0}"; OUT="${3:-artifacts}"
cd "$(dirname "$0")/.."
TARBALL="${4:-$OUT/Syn3Updater-$VERSION-$RID.tar.gz}"
case "$RID" in linux-x64) ARCH=amd64 ;; linux-arm64) ARCH=arm64 ;; *) echo "unsupported rid $RID" >&2; exit 1 ;; esac
# 3.0.0-test6 must sort BEFORE 3.0.0 in dpkg, which is what '~' does
TILDE='~'; DEBVER="${VERSION//-/$TILDE}-1"

STAGE="$(mktemp -d)/root"; trap 'rm -rf "$(dirname "$STAGE")"' EXIT
scripts/stage-linux-root.sh "$TARBALL" "$STAGE" deb
mkdir -p "$STAGE/DEBIAN" "$OUT"
SIZE="$(du -sk --exclude=DEBIAN "$STAGE" | cut -f1)"

# util-linux/udisks2/exfatprogs/polkit: what preparing a USB drive needs. The libraries are what the self-contained .NET + Avalonia program
# loads at run time (ICU, OpenSSL, X11, fontconfig); alternatives cover the package names of Debian 11-13 and Ubuntu 20.04-25.10.
cat > "$STAGE/DEBIAN/control" <<CONTROL
Package: syn3updater
Version: $DEBVER
Architecture: $ARCH
Maintainer: CyanLabs <https://cyanlabs.net>
Installed-Size: $SIZE
Section: utils
Priority: optional
Homepage: https://github.com/CyanLabs/Syn3Updater
Depends: libc6, util-linux, udisks2, exfatprogs, pkexec | policykit-1, libicu76 | libicu74 | libicu72 | libicu70 | libicu67 | libicu66, libssl3t64 | libssl3 | libssl1.1, libx11-6, libice6, libsm6, libfontconfig1, libxcursor1, libxi6, libxrandr2, libxext6
Description: Ford SYNC 3 update USB creator
 Syn3 Updater prepares USB drives for updating a Ford SYNC 3 system: it picks the
 right release, region and maps, downloads and verifies the files, formats the
 drive as exFAT and writes autoinstall.lst / reformat.lst.
 .
 Preparing a drive formats it, which erases everything on it.
CONTROL
# Refresh the icon cache and the desktop database (which maps syn3updater:// to the launcher). Debian's own triggers usually do this, but only when the trigger packages
# are present and working; without it a stale /usr/share/icons/hicolor/icon-theme.cache hides the new icon and the launcher shows a blank one.
for script in postinst postrm; do
  case "$script" in postinst) when='configure' ;; postrm) when='remove|purge' ;; esac
  cat > "$STAGE/DEBIAN/$script" <<SCRIPT
#!/bin/sh
set -e
case "\$1" in
  $when)
    if command -v gtk-update-icon-cache >/dev/null 2>&1; then gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor >/dev/null 2>&1 || true; fi
    if command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database -q /usr/share/applications >/dev/null 2>&1 || true; fi
    ;;
esac
exit 0
SCRIPT
  chmod 755 "$STAGE/DEBIAN/$script"
done
chmod 755 "$STAGE/DEBIAN"; chmod 644 "$STAGE/DEBIAN/control"

FILE="Syn3Updater-$VERSION-$RID.deb"
dpkg-deb --root-owner-group -Zxz --build "$STAGE" "$OUT/$FILE" >/dev/null
( cd "$OUT" && sha256sum "$FILE" > "$FILE.sha256" )
echo "built $OUT/$FILE ($DEBVER, $ARCH)"
