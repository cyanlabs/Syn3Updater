#!/usr/bin/env bash
# Signs manifest.json with the release key and checks the signature against the public keys the app embeds, so a bad key or password fails the release
# here and not on a user's machine. The signed comment names the version, so a signature cannot be moved to a different release's manifest.
# usage: MINISIGN_PRIMARY_KEY=<key file contents> MINISIGN_PASSWORD=<password> scripts/sign-manifest.sh <manifest.json> <version>
# writes <manifest.json>.minisig next to it
set -euo pipefail
MANIFEST="${1:?manifest.json}"; VERSION="${2:?version}"
: "${MINISIGN_PRIMARY_KEY:?the minisign secret key (contents of the .key file)}"; : "${MINISIGN_PASSWORD:?the password of that key}"
here="$(cd "$(dirname "$0")/.." && pwd)"
command -v jq >/dev/null || { echo "jq is required" >&2; exit 1; }
command -v minisign >/dev/null || { echo "minisign is required" >&2; exit 1; }
work="$(mktemp -d)"; trap 'shred -u "$work"/* 2>/dev/null || rm -f "$work"/*; rm -rf "$work"' EXIT
( umask 077; printf '%s\n' "$MINISIGN_PRIMARY_KEY" > "$work/key" )
channel="$(jq -r .channel "$MANIFEST")"
printf '%s\n' "$MINISIGN_PASSWORD" | minisign -S -s "$work/key" -m "$MANIFEST" -x "$MANIFEST.minisig" -t "version:$VERSION channel:$channel" >/dev/null
# the app trusts the primary and the backup key: the signature must verify with one of them
if minisign -V -p "$here/packaging/update/primary.pub" -m "$MANIFEST" -x "$MANIFEST.minisig" >/dev/null 2>&1; then
  echo "signed $MANIFEST with the primary key (version:$VERSION channel:$channel)"
elif minisign -V -p "$here/packaging/update/backup.pub" -m "$MANIFEST" -x "$MANIFEST.minisig" >/dev/null 2>&1; then
  echo "signed $MANIFEST with the BACKUP key (version:$VERSION channel:$channel)"
else
  rm -f "$MANIFEST.minisig"; echo "the signature does not verify with any key the app trusts" >&2; exit 1
fi
