#!/usr/bin/env bash
# Tidies release notes read from stdin for the update dialogs (plain text):
#   - a typed \n (backslash, n) becomes a real new line, so a one-line "-f notes=..." can still be a list
#   - Windows line endings are removed, trailing spaces are trimmed
#   - leading and trailing blank lines are dropped and runs of blank lines become one
# Lines such as "- Fixed this" are kept as they are, so a list looks like a list.
# usage: printf '%s' "- one\n- two" | scripts/format-release-notes.sh
set -euo pipefail
text="$(cat)"
text="${text//\\n/$'\n'}"
text="${text//$'\r'/}"
printf '%s\n' "$text" | awk '
  { sub(/[ \t]+$/, "") }
  NF == 0 { if (started) blank = 1; next }
  { if (blank) print ""; blank = 0; started = 1; print }
'
