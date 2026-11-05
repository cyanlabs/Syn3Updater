#!/usr/bin/env bash
# Run Syn3 Updater from source. Uses ~/.dotnet if the system has no SDK.
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
exec dotnet run --project src/Syn3Updater.App -c Release -- "$@"
