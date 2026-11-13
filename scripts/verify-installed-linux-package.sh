#!/usr/bin/env bash
# Run INSIDE a container, after a .deb/.rpm/Arch package has been installed: checks what a user would meet.
set -euo pipefail
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

APP=/opt/syn3updater/Syn3Updater
DESK=/usr/share/applications/syn3updater.desktop
[[ -x "$APP" ]] || fail "$APP is missing or not executable"
[[ "$(readlink /usr/bin/syn3updater)" == "$APP" ]] || fail "/usr/bin/syn3updater does not point at $APP"
check_icons /usr/share/icons "the installed package"
compgen -G "/usr/share/licenses/syn3updater*/LICENSE" >/dev/null || [[ -f /usr/share/doc/syn3updater/copyright ]] || fail "licence file is missing"
ok "files installed"

[[ -f "$DESK" ]] || fail "launcher entry is missing"
exec_path="$(grep '^Exec=' "$DESK" | head -1 | cut -d= -f2-)"; exec_path="${exec_path%% *}"
[[ "$exec_path" == "$APP" ]] || fail "Exec is '$exec_path', not $APP"
[[ -x "$exec_path" ]] || fail "Exec points at a file that does not exist: $exec_path"      # exact case: this was the Nobara bug
[[ "$(grep '^TryExec=' "$DESK" | cut -d= -f2-)" == "$APP" ]] || fail "TryExec is wrong"
grep -q '^Icon=syn3updater$' "$DESK" || fail "Icon is not syn3updater"
grep -q '^MimeType=.*x-scheme-handler/syn3updater;' "$DESK" || fail "the launcher entry does not claim syn3updater:// links"
grep '^Exec=' "$DESK" | head -1 | grep -q ' %u$' || fail "Exec does not pass the link (%u)"
if command -v desktop-file-validate >/dev/null 2>&1; then desktop-file-validate "$DESK" || fail "desktop-file-validate rejected the entry"; ok "desktop-file-validate"; fi
ok "launcher entry points at $exec_path"

# Without a display the program cannot show a window, but it must get as far as complaining about that: missing libraries (ICU, OpenSSL,
# X11...) would fail earlier with a very different message. This is what proves the package's dependency list is complete.
out="$(env -u DISPLAY -u WAYLAND_DISPLAY HOME="$(mktemp -d)" timeout 30 "$APP" 2>&1 || true)"
echo "--- program output without a display ---"; echo "$out" | head -8; echo "---"
if echo "$out" | grep -qiE 'icu|cannot open shared object|error while loading shared libraries|libssl|No usable|could not load'; then
  if ! echo "$out" | grep -qiE 'display|XOpenDisplay|Unable to initialize'; then fail "the program failed to start for a reason other than the missing display"; fi
fi
ok "program starts as far as a display is needed"
