#!/usr/bin/env bash
# Builds a self-contained Linux package: Syn3Updater-<version>-<rid>.tar.gz (+ .sha256).
# usage: scripts/package-linux.sh <linux-x64|linux-arm64> <version> [outdir]
set -euo pipefail
RID="${1:?rid (linux-x64 or linux-arm64)}"; VERSION="${2:?version, e.g. 3.0.0}"; OUT="${3:-artifacts}"
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

NAME="Syn3Updater-$VERSION-$RID"
STAGE="$(mktemp -d)/$NAME"; mkdir -p "$STAGE" "$OUT"

dotnet publish src/Syn3Updater.App -c Release -r "$RID" --self-contained \
  -p:Version="$VERSION" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "$STAGE"

cp src/Syn3Updater.App/Assets/logo.png "$STAGE/syn3updater.png"
cp LICENSE "$STAGE/"
cat > "$STAGE/syn3updater.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=Syn3 Updater
Comment=Ford SYNC 3 update USB creator
Exec=syn3updater
Icon=syn3updater
Terminal=false
Categories=Utility;
DESK
cat > "$STAGE/install.sh" <<'INST'
#!/usr/bin/env bash
# Installs Syn3 Updater for the current user (no root needed): ~/.local/share/syn3updater, launcher in ~/.local/bin
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; dest="$HOME/.local/share/syn3updater"
mkdir -p "$dest" "$HOME/.local/bin" "$HOME/.local/share/applications" "$HOME/.local/share/icons/hicolor/128x128/apps"
cp "$here/Syn3Updater" "$dest/"; chmod +x "$dest/Syn3Updater"
cp "$here/syn3updater.png" "$HOME/.local/share/icons/hicolor/128x128/apps/syn3updater.png"
ln -sf "$dest/Syn3Updater" "$HOME/.local/bin/syn3updater"
sed "s|^Exec=.*|Exec=$dest/Syn3Updater|" "$here/syn3updater.desktop" > "$HOME/.local/share/applications/syn3updater.desktop"
echo "Installed. Run: syn3updater   (formatting drives also needs: exfatprogs, polkit and udisks2)"
INST
chmod +x "$STAGE/install.sh" "$STAGE/Syn3Updater"

tar -C "$(dirname "$STAGE")" -czf "$OUT/$NAME.tar.gz" "$NAME"
( cd "$OUT" && sha256sum "$NAME.tar.gz" > "$NAME.tar.gz.sha256" )
echo "built $OUT/$NAME.tar.gz"
