#!/usr/bin/env bash
# Builds Syn3Updater-<version>-<rid>.rpm (+ .sha256) from the tarball made by scripts/package-linux.sh. Needs rpmbuild.
# usage: scripts/package-rpm.sh <linux-x64|linux-arm64> <version> [outdir] [tarball]
set -euo pipefail
RID="${1:?rid (linux-x64 or linux-arm64)}"; VERSION="${2:?version, e.g. 3.0.0}"; OUT="${3:-artifacts}"
cd "$(dirname "$0")/.."
TARBALL="${4:-$OUT/Syn3Updater-$VERSION-$RID.tar.gz}"
case "$RID" in linux-x64) ARCH=x86_64 ;; linux-arm64) ARCH=aarch64 ;; *) echo "unsupported rid $RID" >&2; exit 1 ;; esac
# a pre-release (3.0.0-test6) gets Release 0.test6.1 so that it sorts before the final 3.0.0-1
if [[ "$VERSION" == *-* ]]; then RPMVER="${VERSION%%-*}"; RPMREL="0.${VERSION#*-}.1"; RPMREL="${RPMREL//-/.}"; else RPMVER="$VERSION"; RPMREL="1"; fi

TOP="$(mktemp -d)"; trap 'rm -rf "$TOP"' EXIT
STAGE="$TOP/stage"; mkdir -p "$STAGE" "$OUT"
scripts/stage-linux-root.sh "$TARBALL" "$STAGE" rpm

# The single-file .NET program must not be stripped (that corrupts the bundle) and rpm must not guess dependencies from its contents.
# The soname requirements are what the program loads at run time (X11, fontconfig, ICU, OpenSSL).
cat > "$TOP/syn3updater.spec" <<'SPEC'
%global debug_package %{nil}
%global __strip /bin/true
%define __os_install_post %{nil}
AutoReqProv: no

Name:           syn3updater
Version:        %{rpmver}
Release:        %{rpmrel}
Summary:        Ford SYNC 3 update USB creator
License:        GPL-3.0-only
URL:            https://github.com/CyanLabs/Syn3Updater
# .NET needs OpenSSL 3: by soname, because Fedora 45+ ships OpenSSL 4 as openssl-libs and OpenSSL 3 as openssl3-libs (found by the container check)
Requires:       util-linux, udisks2, polkit, exfatprogs, libicu, fontconfig, libssl.so.3()(64bit), libcrypto.so.3()(64bit)
Requires:       libX11.so.6()(64bit), libICE.so.6()(64bit), libSM.so.6()(64bit), libXcursor.so.1()(64bit), libXi.so.6()(64bit), libXrandr.so.2()(64bit), libXext.so.6()(64bit)

%description
Syn3 Updater prepares USB drives for updating a Ford SYNC 3 system: it picks the right release,
region and maps, downloads and verifies the files, formats the drive as exFAT and writes
autoinstall.lst / reformat.lst. Preparing a drive formats it, which erases everything on it.

%install
mkdir -p %{buildroot}
cp -a %{stagedir}/. %{buildroot}/

%files
%defattr(-,root,root,-)
/opt/syn3updater
/usr/bin/syn3updater
/usr/share/applications/syn3updater.desktop
/usr/share/icons/hicolor/128x128/apps/syn3updater.png
%license /usr/share/licenses/syn3updater/LICENSE
SPEC

rpmbuild -bb --target "$ARCH" --define "_topdir $TOP/rpm" --define "rpmver $RPMVER" --define "rpmrel $RPMREL" --define "stagedir $STAGE" \
  "$TOP/syn3updater.spec" >/dev/null
FILE="Syn3Updater-$VERSION-$RID.rpm"
cp "$TOP/rpm/RPMS/$ARCH/syn3updater-$RPMVER-$RPMREL.$ARCH.rpm" "$OUT/$FILE"
( cd "$OUT" && sha256sum "$FILE" > "$FILE.sha256" )
echo "built $OUT/$FILE ($RPMVER-$RPMREL, $ARCH)"
