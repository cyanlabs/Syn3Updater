#!/usr/bin/env bash
# Checks a Linux package the way a user meets it: extract it, run install.sh into a throwaway home, and confirm the launcher entry
# points at a file that really exists (exact case, as on a case-sensitive file system), then uninstall.
# usage: scripts/verify-linux-package.sh <Syn3Updater-...-linux-*.tar.gz>
set -euo pipefail
PKG="$(readlink -f "${1:?path to the .tar.gz}")"
fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { echo "ok: $*"; }

# The icon theme spec treats a folder name such as 128x128 as a promise about the images in it, so every icon must really be that size
# (the first .deb shipped a 100x100 picture in 128x128/). PNG width and height are the big-endian numbers at bytes 16 to 23.
SIZES="16 24 32 48 64 128 256 512"
png_size() { od -An -tu1 -j16 -N8 "$1" | awk '{ print ($1*16777216+$2*65536+$3*256+$4) "x" ($5*16777216+$6*65536+$7*256+$8) }'; }
check_icons() {   # check_icons <icon-theme root holding hicolor/> <where, for the message>
  local s f
  for s in $SIZES; do
    f="$1/hicolor/${s}x${s}/apps/syn3updater.png"
    [[ -f "$f" ]] || fail "the $s pixel icon is missing from $2 ($f)"
    [[ "$(png_size "$f")" == "${s}x${s}" ]] || fail "$f is $(png_size "$f"), not ${s}x${s}"
  done
}

WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/extract dir" "$WORK/home"                 # the space catches unquoted paths
tar -xzf "$PKG" -C "$WORK/extract dir"
PKGDIR="$(find "$WORK/extract dir" -mindepth 1 -maxdepth 1 -type d | head -1)"
[[ -d "$PKGDIR" ]] || fail "the archive has no top-level folder"

# what the archive must (not) contain
for f in Syn3Updater install.sh uninstall.sh README.txt LICENSE; do [[ -f "$PKGDIR/$f" ]] || fail "missing $f in the archive"; done
[[ -x "$PKGDIR/Syn3Updater" ]] || fail "Syn3Updater is not executable"
if compgen -G "$PKGDIR/*.desktop" >/dev/null; then fail "the archive carries a ready-made .desktop file (its Exec cannot know the install path)"; fi
grep -q "install.sh" "$PKGDIR/README.txt" || fail "README.txt does not mention install.sh"
check_icons "$PKGDIR/icons" "the archive"
ok "archive contents (icons in every size, each really that size)"

if command -v shellcheck >/dev/null 2>&1; then
  shellcheck -S warning "$PKGDIR/install.sh" "$PKGDIR/uninstall.sh" || fail "shellcheck found problems in the generated scripts"
  ok "shellcheck on install.sh and uninstall.sh"
fi

# install into an empty home, with XDG_DATA_HOME unset
export HOME="$WORK/home"; unset XDG_DATA_HOME
"$PKGDIR/install.sh" >"$WORK/install.log" 2>&1 || { cat "$WORK/install.log"; fail "install.sh failed"; }
APP="$HOME/.local/share/syn3updater/Syn3Updater"
DESK="$HOME/.local/share/applications/syn3updater.desktop"
[[ -x "$APP" ]] || fail "the program was not installed at $APP"
[[ "$(readlink "$HOME/.local/bin/syn3updater")" == "$APP" ]] || fail "the syn3updater command does not point at $APP"
check_icons "$HOME/.local/share/icons" "the installed home"
[[ -f "$DESK" ]] || fail "the launcher entry was not created"
ok "files installed"

# the launcher entry must name the real file, with the exact case
exec_line="$(grep '^Exec=' "$DESK" | head -1 | cut -d= -f2-)"
exec_path="${exec_line#\"}"; exec_path="${exec_path%\"*}"
[[ "$exec_path" == "$APP" ]] || fail "Exec is '$exec_path' but the program is '$APP'"
[[ -x "$exec_path" ]] || fail "Exec points at a file that does not exist or is not executable: $exec_path"
try_exec="$(grep '^TryExec=' "$DESK" | cut -d= -f2-)"; [[ "$try_exec" == "$APP" ]] || fail "TryExec is '$try_exec'"
grep -q '^Icon=syn3updater$' "$DESK" || fail "Icon is not 'syn3updater'"
grep -q '^MimeType=.*x-scheme-handler/syn3updater;' "$DESK" || fail "the launcher entry does not claim syn3updater:// links"
grep '^Exec=' "$DESK" | head -1 | grep -q ' %u$' || fail "Exec does not pass the link (%u)"
if command -v xdg-mime >/dev/null 2>&1; then
  [[ "$(xdg-mime query default x-scheme-handler/syn3updater)" == "syn3updater.desktop" ]] || fail "syn3updater:// links are not associated with syn3updater.desktop"
  ok "syn3updater:// is associated with the launcher"
fi
if command -v desktop-file-validate >/dev/null 2>&1; then desktop-file-validate "$DESK" || fail "desktop-file-validate rejected the entry"; ok "desktop-file-validate"; fi
ok "launcher entry points at $exec_path"

# installing twice (an upgrade) works and changes nothing important
"$PKGDIR/install.sh" >/dev/null 2>&1 || fail "a second install.sh run failed"
[[ -x "$APP" ]] || fail "the program is gone after reinstalling"
ok "reinstall"

# a wrongly cased command must NOT be what is launched (guards against reintroducing the bug)
[[ ! -e "$HOME/.local/share/syn3updater/syn3updater" ]] || fail "unexpected lower-case program file"

"$PKGDIR/uninstall.sh" >/dev/null 2>&1 || fail "uninstall.sh failed"
if [[ -f "$HOME/.config/mimeapps.list" ]] && grep -q 'x-scheme-handler/syn3updater' "$HOME/.config/mimeapps.list"; then fail "uninstall left the syn3updater:// association behind"; fi
for f in "$HOME/.local/share/syn3updater" "$HOME/.local/bin/syn3updater" "$DESK" "$HOME"/.local/share/icons/hicolor/*/apps/syn3updater.png; do
  [[ ! -e "$f" && ! -L "$f" ]] || fail "uninstall left $f behind"
done
ok "uninstall"
echo "package verified: $(basename "$PKG")"
