#!/bin/bash
# Usage: run-sidebar.sh <repo-dir> <label> <outdir>. One fresh Host running Polar2Chamber; cdp-sidebar.mjs drives the Editor.
repo=$1; label=$2; out=$3
S=$(cd "$(dirname "$0")" && pwd)
probe="$repo/projects/Polar2Chamber/workflows/Split26Probe.flow.json"
statejson="$LOCALAPPDATA/AdsPlcSimulator/state.json"
mkdir -p "$out"
rm -f "$probe"
cd "$repo" || exit 1
[ -z "$(git status --short projects)" ] || { echo "projects not clean"; exit 1; }
[ -f "$statejson" ] && cp "$statejson" "$out/state.json.bak"
dotnet build src/AdsPlcSimulator.Host -v q 2>&1 | grep " 個錯誤"
dotnet run --project src/AdsPlcSimulator.Host --no-build --no-launch-profile --urls http://localhost:5259 -- --Ads:Port=25860 > "$out/$label-host.log" 2>&1 &
until grep -q "Now listening" "$out/$label-host.log"; do grep -q "Hosting failed" "$out/$label-host.log" && { echo "host failed to start"; exit 1; }; sleep 0.3; done
curl -s -o /dev/null -X POST http://localhost:5259/api/project/stop
curl -s -X POST -H "Content-Type: application/json" -d '{"name":"Polar2Chamber"}' http://localhost:5259/api/project/run; echo
node "$S/cdp-sidebar.mjs" "$repo" "$out/$label.json" "$out/$label"
powershell -c "Get-Process | ? { \$_.ProcessName -like 'AdsPlcSimulator.Host*' } | Stop-Process -Force"
powershell -c "Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | ? { \$_.CommandLine -like '*edge26-*' } | % { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue }"
wait 2>/dev/null
rm -f "$probe"
[ -f "$out/state.json.bak" ] && cp "$out/state.json.bak" "$statejson" && rm "$out/state.json.bak"
git status --short projects
