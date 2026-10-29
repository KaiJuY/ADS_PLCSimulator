# ADF project profile: AdsPlcSimulator

This file holds the facts that apply only to this project. ADF rules apply first. This file can add rules. It cannot remove ADF rules.

The scripts read `.adf/profile.json`. Keep both files in step.

## Build and test

| Item | Value |
|---|---|
| Solution | `AdsPlcSimulator.sln` |
| Target frameworks | net9.0 (all three projects) |
| Main framework (development gates) | net9.0 |
| Release frameworks | net9.0 |
| Expected test count | 328 on 2026-10-29 (net9.0) |
| Full suite time | about 1 minute with coverage (net9.0) |

## Known load-dependent tests

- `AdsPlcSimulator.Tests.Ads.*` (`AdsServerTests`) needs a working local TwinCAT ADS router. Recognize it by `ClientPortNotOpen`; it is an environment failure, not a code failure.

## Controlled terms

| Use | Do not use |
|---|---|
| workflow | flow, script |
| process image | memory, variable store |
| client write | HMI write, external write |

## Integration (stage I)

| Item | Value |
|---|---|
| Driver | `tests/AdsPlcSimulator.Integration` (ADS client, scenarios S1–S12, S15 of `.adf/scenarios/1.md`); HTTP API for workflow diagnostics; Editor screenshot with headless Edge: `msedge.exe --headless=new --screenshot=<png> "http://localhost:5259/editor/?workflow=<name>"`, then read the PNG |
| Start | `dotnet run --project src/AdsPlcSimulator.Host --no-launch-profile --urls http://localhost:5259 -- --Simulator:ProjectPath=../../projects/Polar2Chamber --Ads:Port=25860`, then `dotnet run --project tests/AdsPlcSimulator.Integration -- --port 25860` |
| Real hardware | no (simulator) |
| Address and port | HTTP `http://localhost:5259`, ADS port 851 on the local router |
| Resting state | Host stopped; no workflow run in progress |
| What Allen authorized | `dotnet build`, `dotnet test`, `dotnet run` of the Host |

## Paths to skip

- `src/AdsPlcSimulator.Host/wwwroot/**` (JavaScript, not covered by the .NET scan)
- `projects/**` (sample PLC project data and the generated simulation page)

## Project traps
- 2026-10-29: The local TwinCAT runtime answers on AMS 851 and 852 (another PLC). Run the simulator on 25860 for stage I; never send commands to 851/852.
- 2026-10-29: `PUT /api/workflows/{name}` rewrites the file (adds `"groups": []`, drops the final newline). Restore workflow files with `git checkout` after API tests.

- 2026-10-28: A running Host (VS or `dotnet run`) locks `bin\...\AdsPlcSimulator.Host.exe`; the build fails with MSB3027. Stop the Host before a scan.
- 2026-10-28: Visual Studio Browser Link injects before the first `</body>`; inline scripts must escape `</body>` and `</html>`.
