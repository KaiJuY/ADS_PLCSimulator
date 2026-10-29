# ADS PLC Simulator

用 .NET 模擬一台 TwinCAT PLC：ADS client（HMI、EAP、測試程式）連上來看到的是和真 PLC 一樣的符號與記憶體佈局，
而 PLC 的行為由節點式 workflow 描述，不需要改程式。

```
projects/<Project>/plc/*.st|*.TcDUT|*.TcGVL|*.TcPOU   ── Layer 1：資料結構（ST 宣告語法）
projects/<Project>/workflows/*.flow.json              ── Layer 2：讀寫後的行為（節點圖）
projects/<Project>/simulation/                        ── 等角模擬畫面（anatomy kit）
```

## 執行

```powershell
cd src/AdsPlcSimulator.Host
dotnet run
```

- Editor：http://localhost:5259/editor/
- Simulation：http://localhost:5259/simulation/
- ADS：AMS port 851（`appsettings.json` → `Ads:Port`）

| `Ads:Router` | 何時用 |
|---|---|
| `Local`（預設） | 機器上有 TwinCAT（XAE/XAR/ADS Setup），註冊到它的 router。若 TwinCAT runtime 已佔用 851，改用別的 port。 |
| `Embedded` | 機器上沒有 TwinCAT，啟動內建 `AmsTcpIpRouter`（佔 TCP 48898，與 TwinCAT router 互斥）。 |

載入哪個專案由 `Simulator:ProjectPath` 決定（預設 `RobustSolutionDemo`）。要跑 Polar2Chamber：

```powershell
dotnet run --project src/AdsPlcSimulator.Host -- --Simulator:ProjectPath=../../projects/Polar2Chamber
```

Polar2Chamber 的內容與維護方式見 [projects/Polar2Chamber/README.md](projects/Polar2Chamber/README.md)。

## 架構

| 專案 | 責任 |
|---|---|
| `AdsPlcSimulator.Core/Plc` | ST 宣告解析 → 型別解析與 TwinCAT x64 記憶體佈局 → `SymbolTable` + `ProcessImage`（一塊 byte[]，就是 PLC 的資料區）。不依賴 ADS。 |
| `AdsPlcSimulator.Core/Flow` | Workflow 模型、節點目錄、編譯驗證、執行引擎（`FlowRuntime`）。 |
| `AdsPlcSimulator.Host` | `AdsSymbolicServer` 轉接層、Router、HTTP API、WebSocket 即時推播、Editor 靜態頁。 |

資料流：ADS write → `ProcessImage.Write` → 事件佇列 → 符合的 trigger 啟動一次 run → 節點寫回 `ProcessImage`
（ADS client 立即讀得到）／送 `sim.invoke` 到 Simulation 頁；每個節點的狀態與輸出值即時推到 Editor。

### 執行語意（Blueprint 式）

- **Exec pin**（三角形）決定順序。一個 exec 輸出可接多條線，依序執行；多條線可匯入同一個 exec 輸入。
- **Data pin**（圓形）在節點執行時才往上游取值；純資料節點（Read Symbol、Compare、Arithmetic、Constant）每次被讀都重新計算。
- Data 輸入沒接線時，用同名參數的值（所以「寫入 5」只要一個 Write Symbol 節點）。
- 同一個 trigger 前一次 run 還在跑時再被觸發，這次會被略過並記錄 `RunSkipped`（像忙碌中的 PLC FB 忽略新請求）。
- `On Client Write` 只對 ADS client 的寫入觸發（值沒變也觸發）；`On Change` 對任何來源的值變化觸發。

### PackML service 節點與 `[#]`

| 節點 | 用途 |
|---|---|
| `service.host` | 掃描 `root` 底下每個 module 的 PackML 交握（像一個 PLC task）；ServiceId 與 CompletionBehavior 讀自 `identityFile`。沒有 workflow 接手的 service，Execute 等 `defaultDelayMs` 後自動 Complete。 |
| `trigger.onServiceExecute` | service 進入 Execute 時啟動 run，輸出 `index` 與 `generation`；該 service 改由 workflow 決定何時結束。 |
| `action.completeService` | 結束 Execute。service 不在 `generation` 指的那次 Execute 時丟例外，這次 run 失敗。 |
| `action.abortService` | 像 PLC 程式呼叫 `RequestCommand(Abort)`；`generation` 過期或已有排隊命令時丟例外。 |
| `trigger.onStart` | workflow 載入後執行一次。 |

- Symbol 路徑可含一個 `[#]`（例：`GVL_Module.Chamber_Control[#].ServiceCtrl.Chamber.Initial`）。節點用 `index` 輸入取代 `[#]`；`trigger.onServiceExecute` 的 `index` 輸出可直接接到其他節點的 `index` 輸入。路徑沒有 `[#]` 時，`index` 被忽略。
- 同一個 `root` 同時只能有一個 workflow 的 `service.host`。重新載入該 workflow 會把所有 service 重設為 Idle。

## 擴充

- **新的 PLC 結構**：把 `.TcDUT/.TcGVL/.TcPOU` 或 `.st` 丟進 `plc/`。library 型別（沒有原始碼）要在 `.st` 補宣告，見 `plc/Framework/FrameworkLibrary.st`。
- **新的節點**：在 `Core/Flow/Nodes` 寫一個 `ExecNode` 或 `PureNode`，附上 `static NodeDefinition`，加進 `NodeCatalog.Default`。Editor 從 `/api/nodes` 讀目錄，不用改前端。
- **新的模擬動作**：在 `simulation/build.mjs` 的 `ACTIONS` 加 target/action，`node build.mjs` 重建 `index.html`。

## 已知限制

- ADS notification（`AddDeviceNotification`）尚未驗證；目前已驗證 SymbolLoader、ReadValue/WriteValue、variable handle。
- 純 IG/IO 存取（不經符號）未實作。
- FB 只模擬變數，不模擬方法／屬性；`POINTER`/`REFERENCE`/interface 只存 8 bytes 位址值。
- 不支援 `UNION`、`ARRAY[*]`、`BIT`，以及 `DATE#`/`TOD#`/`DT#` literal。

## 測試

```powershell
dotnet test
```

`Ads` 類測試需要本機 TwinCAT router；記憶體佈局測試的期望值取自 TwinCAT 4026 編譯出的 `RobustSolutionDemoProject.tmc`。

## 整合測試（stage I）

`tests/AdsPlcSimulator.Integration` 是 ADS client 主控台程式（不是 xUnit，`dotnet test` 不會執行它）。它連到執行中的 Host，跑 Polar2Chamber 的情境 S1–S12 與 S15，每個情境印一行 PASS／FAIL 與預期值、實際值。S13、S14 是 HTTP／瀏覽器步驟，不在 driver 內。

```powershell
# 終端機 1：啟動 Host（載入 Polar2Chamber）
dotnet run --project src/AdsPlcSimulator.Host -- --Simulator:ProjectPath=../../projects/Polar2Chamber

# 終端機 2：執行 driver
dotnet run --project tests/AdsPlcSimulator.Integration -- [--port 851] [--only S10,S11]
```

- 全部通過時 exit code 為 0，否則為 1；連不上 Host 時印出連線錯誤並回傳 1。
- S4 的預期值（SVID Value 為 `1`）只在剛啟動的 Host 成立；跑過 S10、S12 後要重啟 Host 才能再跑 S4。
- S15 會把 M1 停到 Stopped，要重跑其他情境請重啟 Host。
