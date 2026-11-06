#!/usr/bin/env bash
# Run INSIDE a container, after a .deb/.rpm/Arch package has been installed: checks what a user would meet.
set -euo pipefail
fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { echo "ok: $*"; }

APP=/opt/syn3updater/Syn3Updater
DESK=/usr/share/applications/syn3updater.desktop
[[ -x "$APP" ]] || fail "$APP is missing or not executable"
[[ "$(readlink /usr/bin/syn3updater)" == "$APP" ]] || fail "/usr/bin/syn3updater does not point at $APP"
[[ -f /usr/share/icons/hicolor/128x128/apps/syn3updater.png ]] || fail "icon is missing"
compgen -G "/usr/share/licenses/syn3updater*/LICENSE" >/dev/null || [[ -f /usr/share/doc/syn3updater/copyright ]] || fail "licence file is missing"
ok "files installed"

[[ -f "$DESK" ]] || fail "launcher entry is missing"
exec_path="$(grep '^Exec=' "$DESK" | head -1 | cut -d= -f2-)"; exec_path="${exec_path%% *}"
[[ "$exec_path" == "$APP" ]] || fail "Exec is '$exec_path', not $APP"
[[ -x "$exec_path" ]] || fail "Exec points at a file that does not exist: $exec_path"      # exact case: this was the Nobara bug
[[ "$(grep '^TryExec=' "$DESK" | cut -d= -f2-)" == "$APP" ]] || fail "TryExec is wrong"
grep -q '^Icon=syn3updater$' "$DESK" || fail "Icon is not syn3updater"
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
