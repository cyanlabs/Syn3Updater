#!/usr/bin/env bash
# Build for this machine, install to ~/.local/share/syn3updater and add an app-launcher entry.
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
dest="$HOME/.local/share/syn3updater"
dotnet publish src/Syn3Updater.App -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$dest"
mkdir -p "$HOME/.local/share/applications" "$HOME/.local/bin" "$HOME/.local/share/icons/hicolor/128x128/apps"
cp src/Syn3Updater.App/Assets/logo.png "$HOME/.local/share/icons/hicolor/128x128/apps/syn3updater.png"
ln -sf "$dest/Syn3Updater" "$HOME/.local/bin/syn3updater"
cat > "$HOME/.local/share/applications/syn3updater.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=Syn3 Updater
Comment=Ford SYNC 3 update USB creator
Icon=syn3updater
Exec=$dest/Syn3Updater
Terminal=false
Categories=Utility;
DESK
echo "Installed. Run: syn3updater"
