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
| Expected test count | 388 on 2026-10-30 (net9.0) |
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
| Real client | AdsClientForPolarII in `tests/AdsClientForPolarII/` (untracked binaries, do not commit). It reads `Console.ReadLine` and quits when stdin is redirected, so drive it with the ConPTY harness `.adf/evidence/3/conpty`: `conpty.exe <script> <transcript> <client exe> --port 25860 --container-path GVL_Module.Chamber_Control`. Script lines: `expect <regex>`, `send <text>`, `sleep <ms>`. Ready scripts in the same folder: `client-s3.txt` (read-only items), `client-s5.txt` (Start), `client-s7.txt` (Stop, ReturnToIdle, Abort/Clear/Reset, SVID), `client-s8.txt` (Module Stop/Reset). See `.adf/scenarios/3.md`. |
| Real-PLC comparison | `.adf/evidence/3/measure` (`measure.exe <port>`): the ADS access matrix of #3. Run on 854 and 25860 and compare line by line. It writes only `ServiceCtrl.Shutter.*.RequestId` and restores it. |
| Start | `dotnet run --project src/AdsPlcSimulator.Host --no-launch-profile --urls http://localhost:5259 -- --Simulator:ProjectPath=../../projects/Polar2Chamber --Ads:Port=25860`; wait about 3 s after `Now listening`, then `dotnet run --project tests/AdsPlcSimulator.Integration -- --port 25860`. Restart the Host before each driver run (S2 needs the module in Execute). |
| Real hardware | AMS 854 (local TwinCAT runtime, `Plc30 App`, Polar2 `GVL_Module`): reads for measurement, and writes to the Ctrl area only (never `PackMLIn`, which holds command bits). Restore and compare every written byte. |
| Address and port | HTTP `http://localhost:5259`, simulator ADS port 25860 on the local router |
| Resting state | Host stopped; no workflow run in progress |
| What Allen authorized | `dotnet build`, `dotnet test`, `dotnet run` of the Host; AMS 854 reads and Ctrl-area writes for measurement (2026-10-30) |

## Paths to skip

- `src/AdsPlcSimulator.Host/wwwroot/**` (JavaScript, not covered by the .NET scan)
- `projects/**` (sample PLC project data and the generated simulation page)

## Project traps
- 2026-10-29: The local TwinCAT runtime answers on AMS 851 and 852 (another PLC). Run the simulator on 25860 for stage I; never send commands to 851/852.
- 2026-10-30: Default `Ads:Port` is 25860 since #5. Still pass `--Ads:Port=25860` explicitly when starting the Host. The gate script's Stryker `since` filter can test 0 mutants (it reports PASS anyway); check the mutant count in stryker.log, and pass a branch name, not a short SHA, as the base. The CRAP baseline is from bda9b8a; PlcProgramBuilder type complexity is 208 since #1.
- 2026-10-30: Deleting a handle-based ADS notification makes the .NET `AdsClient` release that handle too, and a raw `0xF006` release bypasses the client's handle cache. Use a fresh handle per case in ADS measurements and tests, or the result describes a released handle.
- 2026-10-30: Starting the integration driver right at `Now listening` makes S6/S7 fail (also on main). Wait about 3 s.
- 2026-10-30: Windows PowerShell 5.1 `Add-Content`/`Set-Content` write the ANSI code page (Big5), and `String.Replace` can pick the char overload. Append to `.adf/log/*.tsv` from bash (`printf ... >>`) and edit UTF-8 text with Python or the Edit tool.
- 2026-10-30: `adf-gate.ps1` needs `powershell -ExecutionPolicy Bypass -File ...` here.
- 2026-10-30: The router accepts notification sends to a closed AMS port without an error, so a crashed client's subscriptions stay in the simulator until the Host stops.
- 2026-10-29: `PUT /api/workflows/{name}` rewrites the file (adds `"groups": []`, drops the final newline). Restore workflow files with `git checkout` after API tests.

- 2026-10-28: A running Host (VS or `dotnet run`) locks `bin\...\AdsPlcSimulator.Host.exe`; the build fails with MSB3027. Stop the Host before a scan.
- 2026-10-28: Visual Studio Browser Link injects before the first `</body>`; inline scripts must escape `</body>` and `</html>`.
