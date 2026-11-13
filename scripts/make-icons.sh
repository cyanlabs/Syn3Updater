#!/usr/bin/env bash
# Regenerates the Linux icon set from the 512x512 master (packaging/icons/syn3updater.png) with ImageMagick.
# The results are committed (packaging/linux/icons/hicolor/<size>x<size>/apps/syn3updater.png), so building a package needs no ImageMagick.
# Each file must really be <size>x<size>: the icon theme spec treats the directory name as a promise about the image, and
# scripts/verify-installed-linux-package.sh and scripts/verify-linux-package.sh check it.
set -euo pipefail
cd "$(dirname "$0")/.."
MASTER=packaging/icons/syn3updater.png
command -v magick >/dev/null 2>&1 || { echo "ImageMagick (magick) is required" >&2; exit 1; }
[[ "$(magick identify -format '%wx%h' "$MASTER")" == "512x512" ]] || { echo "$MASTER must be 512x512" >&2; exit 1; }
for s in 16 24 32 48 64 128 256 512; do
  out="packaging/linux/icons/hicolor/${s}x${s}/apps/syn3updater.png"
  mkdir -p "$(dirname "$out")"
  magick "$MASTER" -filter Lanczos -resize "${s}x${s}" -strip -define png:compression-level=9 "$out"
  echo "$out $(magick identify -format '%wx%h' "$out")"
done
