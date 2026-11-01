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
| Expected test count | 1223 on 2026-11-01 (net9.0; #9) |
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
| Driver | `tests/AdsPlcSimulator.Integration` (ADS client, scenarios S1–S12, S15, S16 of `.adf/scenarios/1.md`, S17, S18 of `.adf/proposals/13.md` §4, S19, S20 of `.adf/scenarios/15.md`, S21–S25 of `.adf/scenarios/16.md`, S26–S29 of `.adf/scenarios/17.md` in `Scenarios.Batch2.cs`, S30–S33 of `.adf/scenarios/18.md` in `Scenarios.Batch3.cs`, S34–S37 of `.adf/scenarios/19.md` in `Scenarios.Batch4.cs`); HTTP API for workflow diagnostics; Editor screenshot with headless Edge: `msedge.exe --headless=new --screenshot=<png> "http://localhost:5259/editor/?workflow=<name>"`, then read the PNG; Editor behavior comparison over CDP: `.adf/evidence/11/run11.sh <repo> <label> <outdir>` starts a fresh stopped Host, and `cdp11.mjs` drives headless Edge through 9 steps (Stopped, Running, open, New, quick add, wire, Ctrl+S, S10 live decoration, console errors) and writes one JSON per run; compare the JSON of two checkouts field by field and the Stopped PNG pixels with PIL `ImageChops.difference().getbbox()` (PNG bytes differ when pixels match) (#11); `.adf/evidence/20/run-deps.sh` with `cdp-deps.mjs` adds 3 steps (symbol suggestions from the variable box and a node Symbol param, wheel zoom, node drag) (#20); `.adf/evidence/21/run-acyclic.sh` with `cdp-acyclic.mjs` also records the live value texts and a variable read and is the one to use now (#21) |
| Real client | AdsClientForPolarII in `tests/AdsClientForPolarII/` (untracked binaries, do not commit). It reads `Console.ReadLine` and quits when stdin is redirected, so drive it with the ConPTY harness `.adf/evidence/3/conpty`: `conpty.exe <script> <transcript> <client exe> --port 25860 --container-path GVL_Module.Chamber_Control`. Script lines: `expect <regex>`, `send <text>`, `sleep <ms>`. Ready scripts in the same folder: `client-s3.txt` (read-only items), `client-s5.txt` (Start), `client-s7.txt` (Stop, ReturnToIdle, Abort/Clear/Reset, SVID), `client-s8.txt` (Module Stop/Reset). See `.adf/scenarios/3.md`. |
| Real-PLC comparison | `.adf/evidence/3/measure` (`measure.exe <port>`): the ADS access matrix of #3. Run on 854 and 25860 and compare line by line. It writes only `ServiceCtrl.Shutter.*.RequestId` and restores it. |
| Start | `dotnet run --project src/AdsPlcSimulator.Host --no-launch-profile --urls http://localhost:5259 -- --Simulator:ProjectPath=../../projects/Polar2Chamber --Ads:Port=25860` (the CLI path still works: it runs the project at startup and does not write state.json; without it the Host starts Stopped and `POST /api/project/run {"name":"Polar2Chamber"}` / `POST /api/project/stop` run and stop a project at runtime, #6); the Host accepts service commands once it prints `Now listening` (or once `POST /api/project/run` returns 200: the session loads the workflows before the AMS port is registered, #8), then `dotnet run --project tests/AdsPlcSimulator.Integration -- --port 25860`. Start a fresh project before each driver run (restart the Host, or Stop then Run): S6 leaves services Stopped and S2 needs the module in Execute. |
| Real hardware | AMS 854 (local TwinCAT runtime, `Plc30 App`, Polar2 `GVL_Module`): reads for measurement, and writes to the Ctrl area only (never `PackMLIn`, which holds command bits). Restore and compare every written byte. |
| Address and port | HTTP `http://localhost:5259`, simulator ADS port 25860 on the local router |
| Resting state | Host stopped; no workflow run in progress |
| What Allen authorized | `dotnet build`, `dotnet test`, `dotnet run` of the Host; AMS 854 reads and Ctrl-area writes for measurement (2026-10-30) |

## Paths to skip

- `src/AdsPlcSimulator.Host/wwwroot/**` (JavaScript, not covered by the .NET scan)
- `projects/**` (sample PLC project data and the generated simulation page)

## Project traps
- 2026-11-01: Editor modules with top-level side effects (`canvas.js`, `variable.js`) are loaded only by an import; after moving exports out of such a module, keep a bare `import './x.js'` in `editor.js`. load-graph and console errors do not catch a module that is never loaded (the canvas simply stops reacting); the reachability loop in `.adf/proposals/20.md` §6.2 and the CDP comparison do (#20).
- 2026-11-01: The Editor gives a new node the id of the last type segment plus a number (`trigger.onStart` → `onStart1`, `flow.delay` → `delay1`). The Editor module graph has no import cycles since #21; after changing Editor imports, run `node .adf/evidence/21/cycles.mjs src/AdsPlcSimulator.Host/wwwroot/editor` in stage F (must print `cycles: 0`), plus ESLint (`.adf/evidence/20/eslint.config.mjs`) and `.adf/evidence/11/load-graph.mjs`. Headless Edge screenshots can differ by a few anti-aliased pixels (RGB ±1) between runs of the same code; on a small pixel diff, run that side again before calling it a regression (#21). Back up and restore `%LOCALAPPDATA%\AdsPlcSimulator\state.json` around a browser run; Run writes `lastProject`.
- 2026-10-30: Polar2Chamber sets `MaxConfigParamPerModule` to 500 (real PLC: 300), so `Chamber_Control` is 12966720 bytes and offsets differ from 854; use symbol names, not fixed offsets. Run publishes `config/module-config.json` before the workflows start (64 SVIDs, 438 ECIDs for slot 1); an invalid config fails the Run (#7).
- 2026-10-29: The local TwinCAT runtime answers on AMS 851 and 852 (another PLC). Run the simulator on 25860 for stage I; never send commands to 851/852.
- 2026-10-30: Default `Ads:Port` is 25860 since #5. Still pass `--Ads:Port=25860` explicitly when starting the Host. The gate script's Stryker `since` filter can test 0 mutants (it reports PASS anyway); check the mutant count in stryker.log, and pass a branch name, not a short SHA, as the base. The CRAP baseline is from bda9b8a; PlcProgramBuilder type complexity is 208 since #1.
- 2026-10-30: Deleting a handle-based ADS notification makes the .NET `AdsClient` release that handle too, and a raw `0xF006` release bypasses the client's handle cache. Use a fresh handle per case in ADS measurements and tests, or the result describes a released handle.
- 2026-10-31: A FALSE and a TRUE written to a command bit within one 10 ms scan give no rising edge; the simulator (and the real PLC) ignore the command. `PlcSession.Send` waits 30 ms after FALSE since #16. Use the same gap in any new client code.
- 2026-11-01: The gate's type-complexity check compares with the stale baseline (bda9b8a). Any change to `SimulatorEndpoints.cs` fails it (baseline `MapSimulatorApi` 14, now 1; type 15 → 32 from new endpoints) although no branch was added (#9). Check the diff, log the decision, or refresh the baseline with `/adf:scan`.
- 2026-11-01: `/api` refuses non-GET/HEAD requests with a foreign `Origin` (403, #9). curl and scripts send no `Origin` and pass; a browser test page on another port is refused.
- 2026-11-01: To undo a hand-applied mutant, revert only the mutated line. `git checkout <file>` also reverts uncommitted edits in that file (#19 lost 2 edits this way).
- 2026-11-01: The gate script's Stryker reports "unable to calculate a mutation score" and still prints GATE 3 PASSED. Run `~/.adf/tools/dotnet-stryker.exe` from `tests/AdsPlcSimulator.Tests` with `-p <project> -m <glob> -r json` and filter survivors to the changed lines.
- 2026-10-31: Do not run `dotnet test` in a worktree while Stryker runs there; it replaces the assemblies and gives a false low score (Core 35% in #16). Stryker is at `~/.adf/tools/dotnet-stryker.exe`, not on PATH.
- 2026-10-30: Windows PowerShell 5.1 `Add-Content`/`Set-Content` write the ANSI code page (Big5), and `String.Replace` can pick the char overload. Append to `.adf/log/*.tsv` from bash (`printf ... >>`) and edit UTF-8 text with Python or the Edit tool.
- 2026-10-30: `adf-gate.ps1` needs `powershell -ExecutionPolicy Bypass -File ...` here.
- 2026-10-30: The router accepts notification sends to a closed AMS port without an error, so a crashed client's subscriptions stay in the simulator until the Host stops.
- 2026-10-29: `PUT /api/workflows/{name}` rewrites the file (adds `"groups": []`, drops the final newline). Restore workflow files with `git checkout` after API tests.

- 2026-10-28: A running Host (VS or `dotnet run`) locks `bin\...\AdsPlcSimulator.Host.exe`; the build fails with MSB3027. Stop the Host before a scan.
- 2026-10-28: Visual Studio Browser Link injects before the first `</body>`; inline scripts must escape `</body>` and `</html>`.
