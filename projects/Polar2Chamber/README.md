# Polar2Chamber 專案

Polar2 Chamber 的 PLC 宣告檔。模擬器載入這個專案，就有和真 PLC 相同的 `GVL_Module.Chamber_Control`（6 個 module，共 12966720 bytes）。本專案的 `plc/` 從下列來源原樣複製，只有一處不同：`Param_Config.TcGVL` 的 `MaxConfigParamPerModule` 由 300 改為 500（規格書的 ECID 有 438 筆，300 放不下）。真 PLC 的 `Chamber_Control` 是 12678720 bytes，所以模擬器的 offset 與真 PLC 不同（用 symbol 名稱存取的 client 不受影響，用固定 offset 的工具會讀錯位置）。

## 來源

| 目的資料夾 | 來源 repo | 來源子路徑 | Commit SHA |
|---|---|---|---|
| `plc/Polar2/` | `ScienTech-Polar2` | `ScienTech_Polar2/DUTs/External/**`（77 個 `.TcDUT`，保留子資料夾）、`ScienTech_Polar2/GVLs/GVL_Module.TcGVL` | `4808094f6ffc4db0dabb656ad255e1cba20a9134` |
| `plc/Framework/` | `TSMC-RobustSolution` | `00_SourseCode/robust_solution_simple_module/robust_solution_simple_module/RobustSolutionFramework/` 底下全部 `.TcDUT`（104 個，在 `DUTs/`、`POUs/`、`Configuration/DUTs/`，保留子資料夾）、`GVLs/Param_Config.TcGVL`。不複製 `.TcPOU` | `82e7eb1db8d6413b76f295aa8538539c9a6453c9` |

下面兩個檔案不在版本控制內，所以用 SHA-256 與修改時間記錄。兩者都在 TSMC repo 的 `00_SourseCode/robust_solution_simple_module/robust_solution_simple_module/Polar_II_ChamberDemo/` 底下。

| 檔案 | 在本 repo | SHA-256 | 修改時間 |
|---|---|---|---|
| `POUs/F_InitializePolarIIChamberServiceStatus.TcPOU` | `plc/ServiceIdentity/`（已複製；TSMC repo 中是 untracked） | `6f290791e8af800a9e15f3f0e234ca87f12ec85fd65f1ca5209a41e1d8222a5e` | 2026-10-24 13:35 +0800 |
| `Polar_II_ChamberDemo.tmc` | 不複製（TSMC 的 `.gitignore:20` 忽略 `*.tmc`） | `a07033683a3ee2772ce81485ba26211012d0a21aad045d765b282e428784cf49` | 2026-10-24 11:31 +0800 |

`Polar2LayoutTests` 的預期值取自這個 `.tmc`（`BitOffs`、`BitSize` 除以 8），再依 `MaxConfigParamPerModule` 300 → 500 調整。`plc/Framework/GVLs/Param_Config.TcGVL` 這一行與來源 commit 不同。調整後的位移（byte，offset / size）：

| symbol | 300（`.tmc`） | 500（本專案） |
|---|---|---|
| `Chamber_Control` | 0 / 12678720 | 0 / 12966720 |
| `Chamber_Control[1]` | 0 / 2113120 | 0 / 2161120 |
| `[1].ModuleConfigParamList` | 58672 / 45600 | 58672 / 76000 |
| `[1].ServiceCtrl` | 104272 / 3300 | 134672 / 3300 |
| `[1].ServiceParam` | 107576 / 77680 | 137976 / 95280 |
| `[1].ServiceStatus` | 185256 / 1927860 | 233256 / 1927860 |

`ServiceParam` 變大是因為 `T_SetConfigParamList` 也用這個上限。

### 規格書

`spec/Polar 二代機 測試 Chamber 規格_20261007.xlsx` 是原檔的副本，原樣複製，不改內容。Service、SVID、ALID、DVID、ECID 的內容以它為準。

| 原檔位置 | SHA-256 | 修改時間 |
|---|---|---|
| `C:\Users\AllenY\Desktop\WorkSpace\Scientech\Polar 二代機 測試 Chamber 規格_20261007.xlsx` | `14bcc55783b6e07bc94f04c123420ee2677eefba68471cad0aa4c09cab7faf79` | 2026-10-29 08:21 +0800 |

工作表：`Service`（70 列）、`Object Quantity`、`Recipe Map`、`ModuleConfigParamList_ECID`（198 列）、`ModuleVariableList_SVID`（22 列）、`ModuleAlarmList_ALID`（68 列）、`ModuleDataList_DVID`（2 列）、`Material Context`、`Nozzle_Cup_Drainer Table`、`Verification Data`。規格書更新時，重新複製並更新上表。

### 設定檔 `config/module-config.json`

SVID（`ModuleVariableList_SVID`）與 ECID（`ModuleConfigParamList_ECID`）的內容由腳本從規格書產生，格式與真 PLC 的 framework 設定檔相同（`schemaVersion` 4）。目前是 slot 1 的 Polar2：64 個 SVID、438 個 ECID。ID 暫定，依輸出順序從 1 編號；規格書加入或刪除列時會位移。

```
python projects/Polar2Chamber/spec/spec-to-config.py "projects/Polar2Chamber/spec/Polar 二代機 測試 Chamber 規格_20261007.xlsx" projects/Polar2Chamber/config/module-config.json
```

腳本只用 Python 3 標準函式庫。遇到不支援的型別、非 ASCII 名稱、名稱縮寫後仍超過 30 byte、重複名稱時，印出原因並以 exit code 1 結束，不寫輸出檔。名稱超過 30 byte 時依固定順序縮寫（例如 `Position` → `Pos`），並在該筆加 `comment` 記錄原名稱。輸出檔為 UTF-8、LF。

ECID 可以手動加 `lowerLimit`、`upperLimit`（JSON 數字），規則照 framework 的 `FB_ModuleConfigurationManager`：

- 兩個要一起寫，只寫一個會讓 Run 失敗。
- 只有 `configParamList` 可以寫；`variableList`（SVID）寫了會讓 Run 失敗。
- 只有 DINT 與 REAL 可以寫；BOOL 與 STRING 寫了會讓 Run 失敗。
- 必須 `lowerLimit <= upperLimit`，且在型別範圍內：DINT 為 -2147483648 到 2147483647，REAL 為 ±3.402823466E38。
- 數值型 ECID（DINT、REAL）發布時 `HasLimits` 為 TRUE；沒寫上下限就發布型別完整範圍。BOOL 與 STRING 發布 `HasLimits` 為 FALSE。

目前腳本產生的設定檔沒有上下限。用腳本重新產生 `config/module-config.json` 會覆蓋整個檔案，手動加的 `lowerLimit`、`upperLimit` 會消失；重新產生後要再加一次。

## Framework 升級時重新複製

1. 在 TSMC repo 取得新版本，記下新的 commit SHA。
2. 刪除 `plc/Framework/`。
3. 從 `RobustSolutionFramework/` 用原樣複製（`cp`，不轉碼、不改換行）所有 `.TcDUT`，保留子資料夾；再複製 `GVLs/Param_Config.TcGVL`，並重新套用 `MaxConfigParamPerModule := 500`。不複製 `.TcPOU`。
4. 如果 Polar2 的 DUT 也有變，用同樣方式重新複製 `plc/Polar2/`。
5. 重新取得 `.tmc` 與 `F_InitializePolarIIChamberServiceStatus.TcPOU`，更新上表的 SHA-256 與修改時間，並更新 `plc/ServiceIdentity/` 的檔案。
6. 更新本檔的 commit SHA，執行 `dotnet test`。`Polar2LayoutTests` 失敗表示版面和新 `.tmc` 不同，要更新測試的預期值。
7. 用手動比對下列 4 個 function block 在新舊版本的差異，因為模擬器以 C# 重現它們的交握行為：`FB_PackMLBase`、`FB_ServiceCoordinator`、`FB_ModuleBase`、`FB_ServiceBase`。

## 啟動 Host

在 repo 根目錄執行：

```
dotnet run --project src/AdsPlcSimulator.Host -- --Simulator:ProjectPath=../../projects/Polar2Chamber
```

## 注意事項

- Editor 的符號路徑選單不會列出含 `[#]` 的路徑，要在 Editor 手動輸入。
- 重新載入 `Polar2_ServiceHost` workflow 會把所有 service 重設為 Idle，所有 module 重設為 Execute。
- 重新載入 `Polar2_ModuleModeInit` workflow 會把 slot 1 的 SVID 1（`Module Mode`）寫回 `'1'`。
- 重新載入有 `trigger.onServiceExecute` 的 workflow 時，若它的 service 正在 Execute，這次 Execute 會退回預設行為：NaturalCompletion 在 `defaultDelayMs` 後完成，ExecuteUntilExternalStop 等到 Stop 或 Abort。

## Alarm

`Polar2_ServiceHost`（`service.host`）照 framework 的 `FB_ModuleAlarmManager` 與 `FB_ModuleBase.M_UpdateAlarmState` 每 10 ms 處理每個 module 的 alarm，發布到 `ModuleAlarmList`，並處理 `ModuleCtrl.AlarmAckRequest`，回應寫在 `ModuleStatus.Alarm`。`ModuleAlarmList` 的順序是 service、configured、hook alarm。

alarm 有三個來源：

- service alarm：`ServiceStatus.<service>.ErrorList` 的項目，由節點 `action.addServiceError` 加入（同 `M_AddError`：重複的 MainErrorId 與第 11 筆會被拒絕，節點的輸出 `added`、`reason` 說明原因）。service 經 Clearing 時 `ErrorList` 清空，alarm 在同一次 scan 變成 inactive。
- configured alarm：設定檔 `alarmList`（見下）。
- hook alarm：節點 `action.setModuleAlarm`（同 `M_AddAlarm`），`active` TRUE 時出現，FALSE 時變成 inactive。設定檔已有同一個 MainErrorId 時，hook alarm 不出現。

Ack：`ModuleCtrl.AlarmAckRequest` 的 `bChangeRequest` 上升緣處理一次；`Command` 1 = AcknowledgeOne（以 ServiceId 與 MainErrorId 找，ServiceId 0 找 configured 與 hook alarm），2 = AcknowledgeAll。inactive 且已 Ack 的 alarm 從 List 移除。

節點的 `message`、`path` 只能在 Editor 的參數欄位輸入，不能接線。字串超過 `ST_ExtAlarm` 宣告的長度（Message 80、Path 255、DateTime 20）時截斷。

### 設定檔 `alarmList`

`config/module-config.json` 的每個 `modules[]` 元素可以有 `alarmList`（最多 30 筆），`source` 相對於該 module：

```
"alarmList": [ {
    "source": "ModuleVariableList[3].Value",
    "dataType": "REAL",
    "condition": { "operator": "gt", "value": 80.0, "tolerance": 0 },
    "mainErrorId": 101, "sourceErrorId": 0,
    "message": "FFU pressure out of range.", "severity": "Alarm" } ]
```

`operator` 為 `gt`、`lt`、`ge`、`le`、`eq`（`eq` 用 `tolerance`）；`severity` 為 `Alarm` 或 `Warning`。`mainErrorId` 必須是 1 到 4294901759。

Run 前若 `AlarmAckRequest.bChangeRequest` 還是 TRUE，新的 host 會把它當作上升緣處理一次（等同 PLC 重開機），所以 Run 前先把 Ctrl 清掉。

## Heartbeat 與 Service Alive

`Polar2_ServiceHost` 照 framework 實作兩個用途不同的機制。兩者的 timeout 都在 Run 時從 `plc/Framework/GVLs/Param_Config.TcGVL` 讀。

- **Module heartbeat**（`FB_ModuleBase.M_CheckHeartbeat`）：client 定時更新 module 的 `HeartbeatIndex`；超過 `ModuleHeartbeat_Timeout`（預設 T#10S）沒變，`ModuleStatus.IsControlSourceAlive` 為 FALSE。只設旗標，不停機、不產生 alarm。重新 Run 時旗標為 TRUE，計時重來。module 缺少 `HeartbeatIndex` 或 `IsControlSourceAlive` 時，這個 module 不做 heartbeat。
- **Service Alive**（`FB_ServiceBase.M_EvaluateAliveTimeout`）：只對 `ExecuteUntilExternalStop` 的 service。client 在 Execute 期間持續更新 `ServiceCtrl.<service>.AliveIndex` 表示「仍要繼續執行」；`AliveTimeout` 在進入 Execute 時讀一次（限制在 `ServiceAliveTimeoutMin` 到 `ServiceAliveTimeoutMax` 之間，預設 100 ms 到 10 s），超過這段時間 `AliveIndex` 沒變，同一次 scan 產生 alarm 940（`SourceErrorId` 是限制後的毫秒數）並 Abort。`AliveTimeout` 為 0 時不監控。離開 Execute 就不再監控；module 不在 Execute 時（例如 Hold），`AliveIndex` 與 `AliveTimeout` 讀作 0。workflow 接管的 service 同樣監控。

相容性：`AliveTimeout` 大於 0 卻不送 `AliveIndex` 的 client，其 `ExecuteUntilExternalStop` service 會被 Abort，與真 PLC 相同。

限制：

- timeout 只支援 `T#` 字面值；`T#5S * 2` 這類運算式會被當成「不是 TIME」而用預設值。值為 0 或負數也用預設值。
- `ServiceAliveTimeoutMin` 大於 `ServiceAliveTimeoutMax`（缺少的一方以預設值比較）時 Run 失敗，訊息指出兩個值。
- 不模擬 composite service 與 coordinator 未就緒時的 `AliveIndex` 歸零。

## Workflow

- `Polar2_ServiceHost`：執行 `GVL_Module.Chamber_Control` 底下所有 module 的 PackML 交握，ServiceId 與 CompletionBehavior 來自 `plc/ServiceIdentity/` 的 identity 檔。
- `Polar2_ModuleModeInit`：啟動時把 slot 1 的 SVID 1（`Module Mode`）寫成 `'1'`。slot 1 的 identity（`ModuleId=1`、`Enabled=TRUE`、`ModuleType=Polar2`）與 SVID、ECID 內容由 `config/module-config.json` 在 Run 時發布；其餘 slot 保持 0、FALSE、空字串。
- `Polar2_Chamber_SetMode`：Chamber.SetMode 進入 Execute 時，`TargetMode` 在 0 到 3 時先完成 service，再把它寫入 SVID 1（Complete 前被 Abort 時 SVID 不變）。超出範圍時 SVID 不變，改用 `action.addServiceError` 把 MainErrorId 1、SourceErrorId = `TargetMode`、訊息 `TargetMode out of range (0~3).` 加入 `ErrorList`，再用 `action.abortService` 中止 service。MainErrorId 1 是暫定值：規格書 ALID 沒有 SetMode 的項目。
- `Polar2_Chamber_Initial`：Chamber.Initial 進入 Execute 後等待 5000 ms 再完成；依 `SwitchModeAfterFinish` 把 SVID 1 寫成 `'2'`（1）或 `'3'`（2），其他值保持原值。

`plc/` 資料夾以 `.gitattributes` 的 `-text` 規則逐位元組保存，不做換行轉換；複製後的檔案與來源的 SHA-256 相同。

`.gitignore` 的 `[Aa][Rr][Mm]/` 規則會忽略名為 Arm 的資料夾；緊接其後的例外 `!projects/**/plc/**/Arm/` 讓 `plc/` 底下的 Arm 資料夾照常被追蹤，重新複製後用一般的 `git add` 即可。
