#!/usr/bin/env bash
# Builds a self-contained Windows package: Syn3Updater-<version>-<rid>.zip (+ .sha256). Runs on Linux, macOS or Windows (Git Bash).
# usage: scripts/package-windows.sh <win-x64|win-arm64> <version> [outdir]
set -euo pipefail
RID="${1:?rid (win-x64 or win-arm64)}"; VERSION="${2:?version, e.g. 3.0.0}"; OUT="${3:-artifacts}"
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

NAME="Syn3Updater-$VERSION-$RID"
STAGE="$(mktemp -d)/$NAME"; mkdir -p "$STAGE" "$OUT"
OUT="$(cd "$OUT" && pwd)"

dotnet publish src/Syn3Updater.App -c Release -r "$RID" --self-contained \
  -p:Version="$VERSION" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "$STAGE"
cp LICENSE "$STAGE/"
# The hybrid Launcher goes in every Windows package: when the Launcher installs a package it removes old files the package does not ship (every .dll, on the
# first run after the v2 Launcher), so a package without the Launcher's own libraries would make it delete them. The files are pinned by hash.
LAUNCHER_DIR="packaging/windows/launcher"
( cd "$LAUNCHER_DIR" && if command -v sha256sum >/dev/null 2>&1; then sha256sum -c --quiet SHA256SUMS; else shasum -a 256 -c --quiet SHA256SUMS; fi ) \
  || { echo "the Launcher files in $LAUNCHER_DIR do not match SHA256SUMS" >&2; exit 1; }
while read -r _ file; do cp "$LAUNCHER_DIR/${file#\*}" "$STAGE/"; done < "$LAUNCHER_DIR/SHA256SUMS"
cat > "$STAGE/README.txt" <<TXT
Syn3 Updater $VERSION
Start Syn3Updater.exe (or Launcher.exe, which keeps it up to date and then opens it). Syn3 Updater checks for new versions itself and installs them.
Syn3 Updater asks for administrator rights because formatting a USB drive needs them.
TXT

rm -f "$OUT/$NAME.zip"
if command -v zip >/dev/null 2>&1; then ( cd "$(dirname "$STAGE")" && zip -qr "$OUT/$NAME.zip" "$NAME" )
elif command -v 7z >/dev/null 2>&1; then ( cd "$(dirname "$STAGE")" && 7z a -tzip -bso0 "$OUT/$NAME.zip" "$NAME" )
else echo "need zip or 7z" >&2; exit 1; fi
( cd "$OUT" && if command -v sha256sum >/dev/null 2>&1; then sha256sum "$NAME.zip"; else shasum -a 256 "$NAME.zip"; fi > "$NAME.zip.sha256" )
echo "built $OUT/$NAME.zip"
