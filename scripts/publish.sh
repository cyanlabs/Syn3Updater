#!/usr/bin/env bash
# Self-contained single-folder builds for every supported platform -> artifacts/<rid>/
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
for rid in ${@:-linux-x64 linux-arm64 win-x64 win-arm64 osx-x64 osx-arm64}; do
  echo "== $rid"
  dotnet publish src/Syn3Updater.App -c Release -r "$rid" --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "artifacts/$rid"
done
