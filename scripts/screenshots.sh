#!/usr/bin/env bash
# Captures screenshots of the whole app with tools/Shots (Avalonia's headless backend: no window is shown) into ONE folder,
# artifacts/screenshots/<timestamp>/, named <what>-<size>-<theme>.png :
#
#   tab-<Page>-<size>-<theme>.png              every tab (Home, Download, Utility, News, Logs, Settings, About, Diagnostics), at each size
#   scroll-<Page>-<N>-<size>-<theme>.png       every tab that is longer than the window, scrolled down one screenful at a time (N = 2, 3 ...; 1 is the tab- shot): Settings > Application Options, the rest of Home ...
#   home-<mode>-<size>-<theme>.png             Home in each install mode (autoinstall, reformat, downgrade): the package list shows each mode's tools
#   update-banner-<state>-<size>-<theme>.png   the update banner in each state (install, progress, howto, notify)
#   update-required-<size>-<theme>.png         the blocking "update required" screen
#   dialog-<welcome|disclaimer>-<size>-<theme>.png   the first-start welcome and the disclaimer
#
# usage: scripts/screenshots.sh [--quick] [--only GROUPS] [--out DIR]
#   --quick   only dark, and only the first size (about a minute for the tabs) instead of the whole set
#   --only    just these groups, comma separated: tabs, scroll, home, banner, required, dialogs (default: all; --quick alone means tabs)
#   --out     where to write (default artifacts/screenshots/<timestamp>, which is git-ignored)
#   SIZES="1100x800 720x800" scripts/screenshots.sh     other sizes (WxH, space separated); the window's minimum width is 640
#
# Each run uses a throwaway home folder, so it never reads or changes your real settings, profiles or logs (the --demo data writes
# sample log files). The pages still call the live CyanLabs API for releases and news, so they need a network to look their best.
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
[[ -d "$HOME/.dotnet" ]] && export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

QUICK=0; OUT=""; ONLY=""
while (($#)); do
  case "$1" in
    --quick) QUICK=1 ;;
    --out) OUT="${2:?--out needs a folder}"; shift ;;
    --only) ONLY="${2:?--only needs a list, e.g. tabs,tall}"; shift ;;
    -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $1 (try --help)" >&2; exit 1 ;;
  esac
  shift
done
OUT="${OUT:-artifacts/screenshots/$(date +%Y%m%d-%H%M%S)}"
read -r -a SIZE_LIST <<< "${SIZES:-1100x800 720x800}"

echo "building tools/Shots ..."
dotnet build tools/Shots -c Release -v quiet --nologo >/dev/null
DLL="$PWD/tools/Shots/bin/Release/net10.0/Shots.dll"
[[ -f "$DLL" ]] || { echo "build output not found: $DLL" >&2; exit 1; }
mkdir -p "$OUT"; OUT="$(cd "$OUT" && pwd)"

# One run of the tool: a fresh throwaway home (an accepted disclaimer, so no first-start popup, unless a variant asks for it), then render.
# usage: shot <name> <WxH> <theme: dark|light> <demo mode> [tool arguments...]   (page names go in the arguments)
#   <name> "tab": one file per page, tab-<Page>-<size>-<theme>.png.   "scroll": one file per scrolled position, scroll-<Page>-<N>-<size>-<theme>.png.
#   Anything else: one file, <name>-<size>-<theme>.png.
COUNT=0; FAILED=0
shot() {
  local name="$1" size="$2" theme="$3" mode="$4"; shift 4
  # a fixed folder name (not a random one): the Download Location and Logs pages print these paths, so shots stay identical from run to run
  local base="${TMPDIR:-/tmp}"; base="${base%/}"
  local w="${size%x*}" h="${size#*x}" iso="$base/syn3shots/home"; rm -rf "$iso"            # always ends in /syn3shots/home
  mkdir -p "$iso/.local/share/CyanLabs/Syn3Updater"
  printf '{"Lang":"en-US","DisclaimerAccepted":true,"Theme":"Default","Profile":"default","DownloadConnections":8,"UpdateChannel":"Beta","DownloadPath":"%s"}\n' "$iso/Downloads/Syn3Updater" \
    > "$iso/.local/share/CyanLabs/Syn3Updater/settings.json"
  local light=(); [[ "$theme" == "light" ]] && light=(--light)
  local tmp; tmp="$(mktemp -d)"
  if env HOME="$iso" XDG_DATA_HOME="$iso/.local/share" XDG_CONFIG_HOME="$iso/.config" XDG_CACHE_HOME="$iso/.cache" DEMO_MODE="$mode" \
       dotnet "$DLL" "$tmp" "$w" "$h" "$@" --demo ${light[@]+"${light[@]}"} >"$tmp/log.txt" 2>&1 && ! grep -q "^FAILED" "$tmp/log.txt"; then
    local n=0 f file page
    for f in "$tmp"/*.png; do
      [[ -e "$f" ]] || continue
      file="${f##*/}"; page="${file%%_*}"                       # the tool names its files <Page>_<W>x<H>[_light][_scroll<N>].png
      if [[ "$name" == tab ]]; then mv "$f" "$OUT/tab-$page-$size-$theme.png"
      elif [[ "$name" == scroll ]]; then
        # only the scrolled positions: the first screen of each page is already a tab- shot
        if [[ "$file" =~ _scroll([0-9]+)\.png$ ]]; then mv "$f" "$OUT/scroll-$page-${BASH_REMATCH[1]}-$size-$theme.png"; else continue; fi
      else mv "$f" "$OUT/$name-$size-$theme.png"; fi
      n=$((n + 1))
    done
    COUNT=$((COUNT + n)); echo "  $n  $name $size $theme"
  else
    FAILED=$((FAILED + 1)); echo "  FAILED  $name $size $theme  ($(grep -m1 "FAILED\|rror" "$tmp/log.txt" 2>/dev/null || echo "see output"))" >&2
  fi
  rm -rf "$iso" "$tmp"
}

echo "writing to $OUT"
themes=(dark light); ((QUICK)) && themes=(dark)
sizes=("${SIZE_LIST[@]}"); ((QUICK)) && sizes=("${SIZE_LIST[0]}")
((QUICK)) && [[ -z "$ONLY" ]] && ONLY="tabs"
want() { [[ -z "$ONLY" || ",$ONLY," == *",$1,"* ]]; }
big="${SIZE_LIST[0]}"

if want tabs; then
  echo "every tab:"
  for size in "${sizes[@]}"; do for theme in "${themes[@]}"; do shot tab "$size" "$theme" downgrade; done; done
fi
if want scroll; then
  echo "scrolled down (one screenful at a time):"
  for theme in "${themes[@]}"; do shot scroll "$big" "$theme" downgrade --scroll; done
fi
if want home; then
  echo "Home in each install mode:"
  for mode in autoinstall reformat downgrade; do for theme in "${themes[@]}"; do shot "home-$mode" "$big" "$theme" "$mode" Home; done; done
fi
if want banner; then
  echo "update banner:"
  for state in install progress howto notify; do for theme in "${themes[@]}"; do shot "update-banner-$state" "$big" "$theme" downgrade Home "--update=$state"; done; done
fi
if want required; then
  echo "required update:"
  for theme in "${themes[@]}"; do shot update-required "$big" "$theme" downgrade Home --forced; done
fi
if want dialogs; then
  echo "first-start dialogs:"
  for theme in "${themes[@]}"; do
    shot dialog-welcome "$big" "$theme" downgrade Home --dialog
    shot dialog-disclaimer "$big" "$theme" downgrade Home --dialog --second
  done
fi

echo "done: $COUNT screenshots in $OUT ($FAILED run(s) failed)"
((FAILED == 0))
