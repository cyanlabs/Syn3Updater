#!/usr/bin/env bash
# Builds the FLAT zip the old v2 Launcher installs from a release: every file of a Windows package (Syn3Updater.exe, the Launcher.exe stub, the transition libraries,
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
# Launcher.exe is the stub, a program of its own and not a copy of the app (the v2 Launcher renames its own Launcher.exe away and relies on this one being there)
here="$(cd "$(dirname "$0")/.." && pwd)"
[[ "$(unzip -p "$OUT" Syn3Updater.exe | sha256sum | cut -d' ' -f1)" != "$(unzip -p "$OUT" Launcher.exe | sha256sum | cut -d' ' -f1)" ]] || { echo "Syn3Updater.exe and Launcher.exe are the same file" >&2; exit 1; }
[[ "$(unzip -p "$OUT" Launcher.exe | head -c 2)" == "MZ" ]] || { echo "Launcher.exe in the zip is not a Windows program" >&2; exit 1; }
unzip -Z1 "$OUT" | grep -q '/' && { echo "the legacy zip must be flat" >&2; exit 1; }
echo "built $OUT for $TAG: $(unzip -Z1 "$OUT" | tr '\n' ' ')"
