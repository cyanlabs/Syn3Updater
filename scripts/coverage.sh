#!/usr/bin/env bash
# Line/branch coverage per class from the cobertura report. usage: scripts/coverage.sh
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
rm -rf artifacts/coverage
dotnet test tests/Syn3Updater.Tests --collect:"XPlat Code Coverage" --results-directory artifacts/coverage -v q >/dev/null
python3 - "$(ls artifacts/coverage/*/coverage.cobertura.xml)" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
rows = []
for cls in root.iter('class'):
    name = cls.get('name')
    if any(x in name for x in ('<', 'ApiSecret', 'AvaloniaXamlLoader', '.Program')) or '.Views.' in name:  # generated / XAML code-behind
        continue
    lines = [l for l in cls.iter('line')]
    if not lines: continue
    hit = sum(1 for l in lines if int(l.get('hits')) > 0)
    rows.append((name, hit, len(lines)))
# merge nested/compiler classes into their parent type
merged = {}
for n, h, t in rows:
    k = n.split('/')[0].split('+')[0]
    a = merged.get(k, (0, 0)); merged[k] = (a[0] + h, a[1] + t)
tot_h = sum(h for h, _ in merged.values()); tot_t = sum(t for _, t in merged.values())
print(f"{'class':62} {'lines':>9} {'cov':>6}")
for k, (h, t) in sorted(merged.items(), key=lambda kv: kv[1][0] / kv[1][1]):
    print(f"{k.replace('Cyanlabs.Syn3Updater.', ''):62} {h:>4}/{t:<4} {100 * h / t:5.0f}%")
print(f"\nTOTAL {tot_h}/{tot_t} = {100 * tot_h / tot_t:.1f}%")
PY
