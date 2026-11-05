#!/usr/bin/env bash
# Builds "Syn3 Updater.app" and a DMG. MUST run on macOS (needs sips, iconutil, codesign, hdiutil).
# usage: scripts/package-macos.sh <osx-arm64|osx-x64> <version> [outdir]
#
# Signing is optional and driven by environment variables:
#   MACOS_CODESIGN_IDENTITY   e.g. "Developer ID Application: Your Name (TEAMID)"  -> real signature (hardened runtime)
#   NOTARY_APPLE_ID, NOTARY_TEAM_ID, NOTARY_PASSWORD (app-specific password)         -> also notarize + staple
# Without an identity the app is ad-hoc signed: it runs, but Gatekeeper asks users to right-click > Open (or clear quarantine).
set -euo pipefail
[[ "$(uname)" == "Darwin" ]] || { echo "package-macos.sh must run on macOS" >&2; exit 1; }
RID="${1:?rid (osx-arm64 or osx-x64)}"; VERSION="${2:?version, e.g. 3.0.0}"; OUT="${3:-artifacts}"
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
ARCH="${RID#osx-}"; [[ "$ARCH" == "x64" ]] && ARCH_LABEL="x64" || ARCH_LABEL="arm64"
NUMERIC="${VERSION%%-*}"                       # 3.0.0-beta.1 -> 3.0.0 (Apple wants dotted numbers)
BUILD="${GITHUB_RUN_NUMBER:-1}"
NAME="Syn3Updater-$VERSION-macos-$ARCH_LABEL"
WORK="$(mktemp -d)"; APP="$WORK/Syn3 Updater.app"; mkdir -p "$OUT" "$APP/Contents/MacOS" "$APP/Contents/Resources"
OUT="$(cd "$OUT" && pwd)"

# 1. publish as a folder (not single-file: single-file would unpack its native libraries on every launch)
dotnet publish src/Syn3Updater.App -c Release -r "$RID" --self-contained -p:Version="$VERSION" -p:DebugType=none -o "$APP/Contents/MacOS"
chmod +x "$APP/Contents/MacOS/Syn3Updater"

# 2. Info.plist
sed -e "s/@VERSION@/$NUMERIC/g" -e "s/@BUILD@/$BUILD/g" packaging/macos/Info.plist.template > "$APP/Contents/Info.plist"

# 3. icon: logo.png -> .icns (the source is small, so the large sizes are upscaled; supply a bigger logo.png for a crisper icon)
ICONSET="$WORK/Syn3Updater.iconset"; mkdir -p "$ICONSET"
SRC=src/Syn3Updater.App/Assets/logo.png
for s in 16 32 128 256 512; do
  sips -z $s $s "$SRC" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) "$SRC" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Syn3Updater.icns"

# 4. sign
if [[ -n "${MACOS_CODESIGN_IDENTITY:-}" ]]; then
  echo "signing with: $MACOS_CODESIGN_IDENTITY"
  # inside-out: every native library and executable first, then the bundle
  find "$APP/Contents/MacOS" -type f \( -name "*.dylib" -o -name "*.so" -o -perm -u+x \) -print0 |
    xargs -0 -n1 codesign --force --timestamp --options runtime --entitlements packaging/macos/entitlements.plist --sign "$MACOS_CODESIGN_IDENTITY"
  codesign --force --timestamp --options runtime --entitlements packaging/macos/entitlements.plist --sign "$MACOS_CODESIGN_IDENTITY" "$APP"
else
  echo "no MACOS_CODESIGN_IDENTITY: ad-hoc signing (users must right-click > Open the first time)"
  codesign --force --deep --sign - "$APP"
fi
codesign --verify --deep --strict "$APP"

# 5. notarize + staple (only with a real signature and credentials)
if [[ -n "${MACOS_CODESIGN_IDENTITY:-}" && -n "${NOTARY_APPLE_ID:-}" && -n "${NOTARY_TEAM_ID:-}" && -n "${NOTARY_PASSWORD:-}" ]]; then
  ditto -c -k --keepParent "$APP" "$WORK/notarize.zip"
  xcrun notarytool submit "$WORK/notarize.zip" --apple-id "$NOTARY_APPLE_ID" --team-id "$NOTARY_TEAM_ID" --password "$NOTARY_PASSWORD" --wait
  xcrun stapler staple "$APP"
else
  echo "notarization skipped (needs MACOS_CODESIGN_IDENTITY + NOTARY_APPLE_ID/TEAM_ID/PASSWORD)"
fi

# 6. DMG with an Applications shortcut
STAGE="$WORK/dmg"; mkdir -p "$STAGE"; cp -R "$APP" "$STAGE/"; ln -s /Applications "$STAGE/Applications"
rm -f "$OUT/$NAME.dmg"
hdiutil create -volname "Syn3 Updater" -srcfolder "$STAGE" -ov -format UDZO "$OUT/$NAME.dmg" >/dev/null
[[ -n "${MACOS_CODESIGN_IDENTITY:-}" ]] && codesign --force --timestamp --sign "$MACOS_CODESIGN_IDENTITY" "$OUT/$NAME.dmg"
( cd "$OUT" && shasum -a 256 "$NAME.dmg" > "$NAME.dmg.sha256" )
echo "built $OUT/$NAME.dmg"
