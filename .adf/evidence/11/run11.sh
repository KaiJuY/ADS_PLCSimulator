#!/bin/bash
# Usage: run11.sh <repo-dir> <label> <outdir>. One fresh, stopped Host per run; cdp11.mjs drives the Editor.
repo=$1; label=$2; out=$3
S=$(cd "$(dirname "$0")" && pwd)
probe="$repo/projects/Polar2Chamber/workflows/Split11Probe.flow.json"
mkdir -p "$out"
rm -f "$probe"
cd "$repo" || exit 1
[ -z "$(git status --short projects)" ] || { echo "projects not clean"; exit 1; }
dotnet build src/AdsPlcSimulator.Host -v q 2>&1 | grep " 個錯誤"
dotnet build tests/AdsPlcSimulator.Integration -v q 2>&1 | grep " 個錯誤"
dotnet run --project src/AdsPlcSimulator.Host --no-build --no-launch-profile --urls http://localhost:5259 -- --Ads:Port=25860 > "$out/$label-host.log" 2>&1 &
until grep -q "Now listening" "$out/$label-host.log"; do sleep 0.3; done
curl -s -o /dev/null -X POST http://localhost:5259/api/project/stop
node "$S/cdp11.mjs" "$repo" "$out/$label.json" "$out/$label"
powershell -c "Get-Process | ? { \$_.ProcessName -like 'AdsPlcSimulator.Host*' } | Stop-Process -Force"
powershell -c "Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | ? { \$_.CommandLine -like '*edge11-*' } | % { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue }"
wait 2>/dev/null
rm -f "$probe"
git status --short projects
