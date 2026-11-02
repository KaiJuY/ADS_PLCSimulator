#!/bin/bash
# Stage I of #27: full Integration run, then vacuum-probe, each on a fresh Run of Polar2Chamber (simulator port 25860 only).
# Usage: run-i27.sh <repo> <outdir>
repo=$1; out=$2
HERE=$(cd "$(dirname "$0")" && pwd)
SJ="$LOCALAPPDATA/AdsPlcSimulator/state.json"
mkdir -p "$out"
cd "$repo" || exit 1
[ -z "$(git status --short projects)" ] || { echo "projects not clean"; exit 1; }
cp "$SJ" "$out/state.json.bak"
dotnet build AdsPlcSimulator.sln -v q 2>&1 | grep " 個錯誤"
dotnet run --project src/AdsPlcSimulator.Host --no-build --no-launch-profile --urls http://localhost:5259 -- --Ads:Port=25860 > "$out/host.log" 2>&1 &
until grep -q "Now listening" "$out/host.log"; do grep -q "Hosting failed" "$out/host.log" && { echo "host failed to start"; exit 1; }; sleep 0.3; done
run() { curl -s -o /dev/null -X POST http://localhost:5259/api/project/stop; curl -s -X POST -H 'Content-Type: application/json' -d '{"name":"Polar2Chamber"}' http://localhost:5259/api/project/run; echo; }
run
dotnet run --project tests/AdsPlcSimulator.Integration --no-build -- --port 25860 > "$out/integration.log" 2>&1
echo "integration exit=$?" | tee -a "$out/summary.txt"
run
dotnet run --project "$HERE/vacuum-probe" -- --port 25860 > "$out/vacuum-probe.log" 2>&1
echo "vacuum-probe exit=$?" | tee -a "$out/summary.txt"
curl -s -o /dev/null -X POST http://localhost:5259/api/project/stop
powershell -c "Get-Process | ? { \$_.ProcessName -like 'AdsPlcSimulator.Host*' } | Stop-Process -Force"
wait 2>/dev/null
cp "$out/state.json.bak" "$SJ" && rm "$out/state.json.bak"
git status --short projects
