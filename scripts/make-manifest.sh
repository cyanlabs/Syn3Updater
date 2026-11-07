#!/usr/bin/env bash
# Writes manifest.json for a release: its version, channel, notes and every downloadable file (os, arch, kind, url, size, sha256). The release workflow then
# signs it with scripts/sign-manifest.sh; the app trusts a release only through that signature.
# usage: scripts/make-manifest.sh <version> <release-tag> <dist-dir> <out.json> [notes-file]
#   version      e.g. 3.1.0, 3.1.0-beta.2, 3.1.0-nightly.20261108.5
#   release-tag  the tag the files are published under (v3.1.0, nightly, 2.99.99.5): it only appears in the download addresses
set -euo pipefail
VERSION="${1:?version}"; TAG="${2:?release tag}"; DIST="${3:?dist dir}"; OUT="${4:?output file}"; NOTES_FILE="${5:-}"
REPO="${GITHUB_REPOSITORY:-CyanLabs/Syn3Updater}"
command -v jq >/dev/null || { echo "jq is required" >&2; exit 1; }

case "$VERSION" in *-nightly*) channel=Nightly ;; *-*) channel=Beta ;; *) channel=Stable ;; esac

# every file the app can pick, and what it is: <file name>|<os>|<arch>|<kind>
expected=(
  "Syn3Updater-$VERSION-win-x64.zip|windows|x64|zip"
  "Syn3Updater-$VERSION-win-arm64.zip|windows|arm64|zip"
  "Syn3Updater-$VERSION-linux-x64.tar.gz|linux|x64|targz"
  "Syn3Updater-$VERSION-linux-arm64.tar.gz|linux|arm64|targz"
  "Syn3Updater-$VERSION-linux-x64.deb|linux|x64|deb"
  "Syn3Updater-$VERSION-linux-arm64.deb|linux|arm64|deb"
  "Syn3Updater-$VERSION-linux-x64.rpm|linux|x64|rpm"
  "Syn3Updater-$VERSION-linux-arm64.rpm|linux|arm64|rpm"
  "Syn3Updater-$VERSION-linux-x64.pkg.tar.zst|linux|x64|arch"
  "Syn3Updater-$VERSION-linux-arm64.pkg.tar.zst|linux|arm64|arch"
  "Syn3Updater-$VERSION-macos-arm64.dmg|macos|arm64|dmg"
  "Syn3Updater-$VERSION-macos-x64.dmg|macos|x64|dmg"
)
sha() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
size() { stat -c %s "$1" 2>/dev/null || stat -f %z "$1"; }

assets="[]"; missing=()
for e in "${expected[@]}"; do
  IFS='|' read -r name os arch kind <<<"$e"
  f="$DIST/$name"
  if [[ ! -f "$f" ]]; then missing+=("$name"); continue; fi
  assets="$(jq -c --arg os "$os" --arg arch "$arch" --arg kind "$kind" --arg name "$name" --arg url "https://github.com/$REPO/releases/download/$TAG/$name" \
              --argjson size "$(size "$f")" --arg sha "$(sha "$f")" \
              '. + [{os:$os, arch:$arch, kind:$kind, name:$name, url:$url, size:$size, sha256:$sha}]' <<<"$assets")"
done
if (( ${#missing[@]} )); then printf 'cannot write the manifest, these files are missing from %s:\n' "$DIST" >&2; printf '  %s\n' "${missing[@]}" >&2; exit 1; fi

notes=""; [[ -n "$NOTES_FILE" && -s "$NOTES_FILE" ]] && notes="$(cat "$NOTES_FILE")"
jq -n --arg version "$VERSION" --arg channel "$channel" --arg published "$(date -u +%Y-%m-%dT%H:%M:%SZ)" --arg notes "$notes" --argjson assets "$assets" \
  '{schema: 1, version: $version, channel: $channel, published: $published, notes: $notes, assets: $assets}' > "$OUT"
echo "wrote $OUT ($channel $VERSION, $(jq '.assets|length' "$OUT") files)"
