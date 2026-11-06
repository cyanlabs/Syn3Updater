#!/usr/bin/env bash
# Builds the FLAT zip the old v2 Launcher installs from a release: every file of a Windows package (Syn3Updater.exe, the hybrid Launcher.exe and its libraries,
# LICENSE, README.txt) at the top level, with no folder. That Launcher renames its own Launcher.exe away and copies the zip over its folder, so a nested folder or a
# missing Launcher.exe leaves a broken install. (The zip must also be uploaded as application/x-zip-compressed: the release workflow does that.)
# usage: scripts/make-legacy-zip.sh <Syn3Updater-<v>-win-x64.zip> <tag> <out.zip>
set -euo pipefail
WIN="${1:?the Windows package zip}"; TAG="${2:?tag, e.g. 2.99.99.1}"; OUT="$(readlink -f "${3:?output zip}")"
WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
unzip -q "$WIN" -d "$WORK/in"
inner="$(find "$WORK/in" -mindepth 1 -maxdepth 1 -type d | head -n1)"
[[ -n "$inner" && -f "$inner/Syn3Updater.exe" && -f "$inner/Launcher.exe" ]] || { echo "$WIN has no Syn3Updater.exe and Launcher.exe inside one folder" >&2; exit 1; }
rm -f "$OUT"
( cd "$inner" && zip -q -j "$OUT" ./* )
# the Launcher in the zip must be the pinned one, and distinct from the app
here="$(cd "$(dirname "$0")/.." && pwd)"
want="$(awk '$2 == "Launcher.exe" || $2 == "*Launcher.exe" { print $1 }' "$here/packaging/windows/launcher/SHA256SUMS")"
got="$(unzip -p "$OUT" Launcher.exe | sha256sum | cut -d' ' -f1)"
[[ "$got" == "$want" ]] || { echo "Launcher.exe in the zip is not the pinned Launcher" >&2; exit 1; }
[[ "$(unzip -p "$OUT" Syn3Updater.exe | sha256sum | cut -d' ' -f1)" != "$got" ]] || { echo "Syn3Updater.exe and Launcher.exe are the same file" >&2; exit 1; }
unzip -Z1 "$OUT" | grep -q '/' && { echo "the legacy zip must be flat" >&2; exit 1; }
echo "built $OUT for $TAG: $(unzip -Z1 "$OUT" | tr '\n' ' ')"
