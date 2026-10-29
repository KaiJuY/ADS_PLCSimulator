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
