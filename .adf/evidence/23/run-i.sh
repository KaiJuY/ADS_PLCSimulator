#!/bin/bash
# Stage I of #23, phase A: record /live while the Integration scenarios run, one fresh project Run per scenario.
# Usage: run-i.sh <repo> <outdir> <scenario...>
repo=$1; out=$2; shift 2
HERE=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$out"
cd "$repo" || exit 1
SJ="$LOCALAPPDATA/AdsPlcSimulator/state.json"
cp "$SJ" "$out/state.json.bak"
dotnet run --project src/AdsPlcSimulator.Host --no-build --no-launch-profile --urls http://localhost:5259 -- --Ads:Port=25860 > "$out/host.log" 2>&1 &
until grep -q "Now listening" "$out/host.log"; do sleep 0.3; done
node "$HERE/live-record.mjs" "$out/live.jsonl" &
REC=$!
sleep 1
for s in "$@"; do
  curl -s -o /dev/null -X POST http://localhost:5259/api/project/stop
  curl -s -o /dev/null -X POST -H 'Content-Type: application/json' -d '{"name":"Polar2Chamber"}' http://localhost:5259/api/project/run
  printf '{"marker":"%s","ms":%s}\n' "$s" "$(date +%s%3N)" >> "$out/markers.jsonl"
  dotnet run --project tests/AdsPlcSimulator.Integration --no-build -- --port 25860 --only "$s" > "$out/$s.log" 2>&1
  echo "$s exit=$?" >> "$out/scenarios.txt"
done
sleep 1
kill $REC 2>/dev/null
curl -s -o /dev/null -X POST http://localhost:5259/api/project/stop
powershell -c "Get-Process | ? { \$_.ProcessName -like 'AdsPlcSimulator.Host*' } | Stop-Process -Force"
wait 2>/dev/null
cp "$out/state.json.bak" "$SJ"
git status --short projects
