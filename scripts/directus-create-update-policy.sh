#!/usr/bin/env bash
# Creates the `update_policy` collection in the CyanLabs Directus backend: the minimum Syn3 Updater version per channel and OS that the app
# enforces (an installed build below it is updated at the next launch). One row per channel; an empty value means "no minimum".
#
# usage: DIRECTUS_TOKEN=<admin token> scripts/directus-create-update-policy.sh [--dry-run] [--apply]
#   --dry-run (default)  only READS the backend, and prints every change it would make
#   --apply              makes the changes
# env:   DIRECTUS_URL (default https://syn3updater.cyanlabs.net), APP_POLICY_NAME (default "Syn3Updater": the policy the app's token belongs to)
# Safe to run again: anything that already exists is left alone. It never modifies or deletes an existing collection, field, role or policy.
set -euo pipefail
MODE="dry-run"
for a in "$@"; do case "$a" in --apply) MODE=apply ;; --dry-run) MODE=dry-run ;; *) echo "unknown option $a" >&2; exit 2 ;; esac; done
: "${DIRECTUS_TOKEN:?set DIRECTUS_TOKEN to an admin token}"
URL="${DIRECTUS_URL:-https://syn3updater.cyanlabs.net}"; APP_POLICY_NAME="${APP_POLICY_NAME:-Syn3Updater}"
command -v jq >/dev/null || { echo "jq is required" >&2; exit 1; }

api() { curl -gsS -m 30 -H "Authorization: Bearer $DIRECTUS_TOKEN" -H "Content-Type: application/json" "$@"; }
code() { curl -gsS -m 30 -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $DIRECTUS_TOKEN" "$1"; }
# one change: in dry-run mode print it, in apply mode send it and fail loudly
change() {  # method path json
  echo "  $1 $URL$2"; echo "$3" | jq -c . | sed 's/^/    /'
  [[ "$MODE" == apply ]] || return 0
  local out; out="$(api -X "$1" "$URL$2" -d "$3")"
  if echo "$out" | jq -e '.errors' >/dev/null 2>&1; then echo "FAILED: $out" >&2; exit 1; fi
}

VERSION_RE='^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?(-[0-9A-Za-z.]+)?$'
versionField() {  # name, note
  jq -n --arg f "$1" --arg note "$2" --arg re "$VERSION_RE" '{
    field: $f, type: "string",
    meta: { interface: "input", note: $note, width: "half", options: { placeholder: "3.0.2" },
            validation: { _or: [ {($f): {_null: true}}, {($f): {_regex: $re}} ] },
            validation_message: "Use a version like 3.0.2, 3.0.0.1 or 3.1.0-beta.2 (or leave empty for no minimum)" },
    schema: { is_nullable: true, max_length: 64, default_value: null } }'
}

echo "Mode: $MODE   Backend: $URL"
[[ "$(code "$URL/users/me")" == 200 ]] || { echo "the token was refused by $URL" >&2; exit 1; }

# 1. the collection and its fields
if [[ "$(code "$URL/collections/update_policy")" == 200 ]]; then
  echo "collection update_policy already exists: leaving it alone"
else
  echo "create the collection:"
  body="$(jq -n --argjson all "$(versionField min_version_all 'Minimum for every OS. Builds below this are updated at next launch. Empty = no minimum.')" \
                --argjson win "$(versionField min_version_windows 'Windows override, applied on top of the all-OS minimum (the higher wins).')" \
                --argjson lin "$(versionField min_version_linux 'Linux override. Package-managed installs (deb, rpm, Arch, AUR) are told how to update instead.')" \
                --argjson mac "$(versionField min_version_macos 'macOS override.')" '{
    collection: "update_policy",
    meta: { icon: "system_update", note: "Minimum Syn3 Updater version per update channel. The app forces an update to anything below it.", singleton: false, accountability: "all" },
    schema: {},
    fields: [
      { field: "id", type: "integer", meta: { hidden: true, readonly: true, interface: "input" }, schema: { is_primary_key: true, has_auto_increment: true } },
      { field: "channel", type: "string",
        meta: { interface: "select-dropdown", required: true, width: "half", note: "The update channel this row applies to.",
                options: { choices: [ {text: "Stable", value: "Stable"}, {text: "Beta", value: "Beta"}, {text: "Nightly", value: "Nightly"} ] } },
        schema: { is_nullable: false, is_unique: true, max_length: 16 } },
      { field: "enabled", type: "boolean", meta: { interface: "boolean", width: "half", note: "Off = nothing is forced for this channel, whatever the versions below say." }, schema: { default_value: false, is_nullable: false } },
      $all, $win, $lin, $mac,
      { field: "message", type: "text", meta: { interface: "input-multiline", note: "Optional. Shown on the screen explaining why an update is required." }, schema: { is_nullable: true } }
    ] }')"
  change POST /collections "$body"
fi

# 2. let the app read it: only the app's own policy, nothing public
pid="$(api "$URL/policies?filter[name][_eq]=$APP_POLICY_NAME&fields=id" | jq -r '.data[0].id // empty')"
[[ -n "$pid" ]] || { echo "no policy named '$APP_POLICY_NAME'" >&2; exit 1; }
have="$(api "$URL/permissions?filter[policy][_eq]=$pid&filter[collection][_eq]=update_policy&filter[action][_eq]=read&fields=id" | jq -r '.data | length')"
if [[ "$have" -gt 0 ]]; then echo "policy '$APP_POLICY_NAME' can already read update_policy"
else
  echo "allow policy '$APP_POLICY_NAME' ($pid) to READ update_policy:"
  change POST /permissions "$(jq -n --arg p "$pid" '{policy: $p, collection: "update_policy", action: "read", fields: ["*"], permissions: {}, validation: {}}')"
fi

# 3. one row per channel, all switched off
for ch in Stable Beta Nightly; do
  n="$(api "$URL/items/update_policy?filter[channel][_eq]=$ch&fields=id" 2>/dev/null | jq -r '.data | length' 2>/dev/null || echo 0)"
  if [[ "$n" -gt 0 ]]; then echo "row $ch already exists"
  else echo "add the $ch row (disabled, no minimums):"; change POST /items/update_policy "$(jq -n --arg c "$ch" '{channel: $c, enabled: false}')"; fi
done
echo "done ($MODE)"
