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

### 設定檔 `config/service-behaviors.json`

這是模擬器自己的擴充，不是真 PLC 的格式；沒有 `config/module-config.json` 時不能用（SVID、ECID 的位置由它決定），Run 會失敗。service 進入 Execute 的那次 scan 照這個檔案檢查 Module Mode（SVID 1）與參數，不通過就在該 service 的 `ErrorList` 加一筆並 Abort（MainErrorId 2 = Mode 不允許，1 = 參數超出範圍或 TIME 為 0）。只在從 Starting 進入 Execute 時檢查；Execute 中 Mode 改變不中斷，Hold／Suspend 後回到 Execute 也不重查。被 `trigger.onServiceExecute` 接管的 service 只檢查 Mode。

Polar2 的規則：

- Mode 0（Disable）：只有 `Chamber.SetMode` 能執行。這是例外，記在 #10 的規格待確認清單。
- Mode 1（Manual）：`Chamber.SingleProcess`、`Chamber.EMGS` 不能執行。
- Mode 2（DryRun）、3（Auto）：全部允許。
- `Chamber.ArmAlignTest`、`Chamber.ArmTest`：`ArmIndex` 1..4、`NozzleIndex` 1..3；`Chamber.FlowCalibration`：`ArmIndex` 1..4、`ArmNozzleIndex` 1..3。
- `Chamber.ArmAlignTest`、`Chamber.FlowCalibration` 以 `TestTime` 為執行時間（為 0 時 MainErrorId 1）；`Chamber.ArmTest` 沒有 TestTime，用 `service.host` 的 `defaultDelayMs`。
- `Holder.Rotate.MoveVelocityCW` 進入 Execute 時把 SVID 53 寫成 ECID 318，`MoveVelocityCCW` 寫成負值；任何離開 Execute（Complete、Stop、Abort、Hold、Suspend）時寫回 0；Unhold、Unsuspend 回到 Execute 時再寫一次。
- `Holder.Rotate.MoveModulo`：`Position`（LREAL）須在 0..360（含兩端），超出時 MainErrorId 1、`SourceErrorId` = `Position` 的整數部分（負值為 `unchecked` 的 UDINT；NaN、∞ 為 4294967295）、訊息 `Position out of range (0~360).`。進入 Execute 時 SVID 53 寫成 ECID 318，離開 Execute 寫回 0（5000 ms 後 Complete）。
- `OutsideRing.Rotate.MoveVelocityCW` 進入 Execute 時把 SVID 58 寫成 ECID 361，`MoveVelocityCCW` 寫成負值；離開 Execute 時寫回 0（同上，任何離開都寫）。
- `Cup[1..4].Lift.TopPos`、`MiddlePos`、`DownPos`：在 Complete 之前（5000 ms 到時）把該 Cup 的 Master、Slave 高度 SVID 寫成對應的 ECID（Top／Down／Middle Pos）。Cup1 是 SVID 45／46、ECID 276／277／278，Cup2 是 47／48、288／289／290，Cup3 是 49／50、300／301／302，Cup4 是 51／52、312／313／314。
- `Cup[1..4].Lift.IncrementalJog`：在 Complete 之前，Master、Slave 高度 SVID 各自加上 `Position`（mm，可為負）；不檢查上下限。Execute 中被 Stop 或 Abort 時高度不變。
- DryRun（Mode 2）不影響以上各項（沒有標 `dryRunZero`）。
- 第 3 批（#18，62 項，共 86 項）：
  - `Arm[1..4].Lift.TopPos`、`DownPos`：進入 Execute 時 `Armn Lift Speed` 寫成 ECID `Armn Lift Speed (Normal)`，Complete 之前（5000 ms 到時）`Armn Lift Position` 寫成 ECID `Lift Top Pos`／`Down Pos`，離開 Execute 時速度寫回 0。
  - `Arm[1..4].Rotate.NegLimitPos`、`PosLimitPos`、`NozzlePortPos`、`Noz1CenterPos`、`Noz2CenterPos`、`Noz3CenterPos`：同上，速度是 `Armn Rotate Speed (Normal)`，位置是 `Armn Rotate Position` 寫成對應的 `Rotate Neg Limit Pos`、`Pos Limit Pos`、`Nozzle Port Pos`、`Noz1/2/3 Center Pos`（各 Arm 只有 PLC 綁定的 service 在表中：Arm1、Arm2 各 17 項，Arm3 11 項，Arm4 9 項）。
  - `Arm[1..4].Rotate.IncrementalJog`：速度同上；Complete 之前 `Armn Rotate Position` 加上 `Position`（deg，可為負）。
  - `Arm[1..4].Rotate.JogPositive`、`JogNegative`：進入 Execute 時 `Armn Rotate Speed` 寫成 ECID `Armn Rotate Speed (Jog)`（Negative 為負值），離開 Execute 寫回 0；ExecuteUntilExternalStop，沒有位置變化。
  - `Arm[n].Nozzle[k].ChemicalSupply`、`Arm[n].NozzlePort.Nozzle[k].Supply`、`FixedNozzle[1..4].ChemicalSupply`：進入 Execute 時對應的 Flow/Pressure SVID 寫成 `ServiceParam` 的 `Setpoint`，離開 Execute 寫回 0。DryRun（Mode 2）時寫 0（`dryRunZero`），因為 DryRun 不出化學藥劑。
  - `Exhaust.Fan[1..4].SupplyFlowPID`：進入 Execute 時 `ExhaustFann Pressure` 寫成 ECID `ExhaustFann Target Pressure`，離開 Execute 寫回 0。排氣不出化學藥劑，DryRun 不歸零。
  - 不在表中（沒有 SVID 可寫，或留待後批）：`Arm[n].Rotate.MoveRecipe`、`Arm[n].NozzlePort.DrainSelect*`、`Exhaust.Damper.*`、`SideVent[n].*`。FFU（SVID 63、64）在 PLC 沒有 service，記在 #10。
- 第 4 批（#19，2 項，共 88 項）：`Chamber.SingleProcess`、`Chamber.CupChamberClean` 用 `process` 段模擬步驟執行（`process` 與 `duration` 不能並存）。
  - 檢查（進入 Execute 時，Mode 之後；MainErrorId 1）：`ProcessStepCount` 須在 1..50（`SourceErrorId` = 該值，訊息 `ProcessStepCount out of range (1~50).`）；再從第 1 步到第 `ProcessStepCount` 步，依序檢查 `ArmIndex` 1..4、`RecipeIndex` 1..`RecipeCount`、`ArmNozzleIndex`（Arm1、Arm2 為 1..3，Arm3 為 1..2，Arm4 為 1..1）、`ProcessTime` 大於 0、`RotateDirection` 1..2、`RotateSpeed` 大於 0、`CupDrainIndex` 1..3、`CycleControl` 0..2、`CycleCount` 大於 0（只在 `CycleControl` 為 Start 時）；`Chamber.CupChamberClean` 另檢查 `CleanSettings[i].TargetCupIndex` 1..4 與 `CleanType` 1..2。第一個不通過的回報：`SourceErrorId` = 該欄位值（NaN、∞ 為 4294967295），訊息如 `ProcessSteps[3].ArmIndex out of range (1~4).`、`ProcessSteps[2].ProcessTime must be greater than 0.`。循環配對：End 前沒有 Start 時 `SourceErrorId` = 該步編號，訊息 `ProcessSteps[2].CycleControl End without Start.`；Start 沒有 End 時 `SourceErrorId` = 最內層未關閉的 Start 的步編號，訊息 `… Start without End.`。
  - 執行：進入 Execute 時複製步驟參數（Execute 中改參數不影響這次），從第 1 步開始；每步經過 `ProcessTime`（最短 1 ms）換下一步；Complete 的時間是所有步驟（含循環重複）的時間總和，不看 `defaultDelayMs`。`CycleControl` 為 Start（1）的步到 End（2）的步（含兩端）共執行 `CycleCount` 次（含第一次），可以巢狀。
  - 每步寫入的 SVID：53 = `RotateSpeed`（`RotateDirection` 為 CCW 時取負，四捨五入到整數）；Arm 的 Nozzle 流量 SVID 依 `ArmIndex`、`ArmNozzleIndex` 查表（Arm1 為 9／10／11，Arm2 為 22／23／24，Arm3 為 34／35，Arm4 為 43）= `ArmNozzleSetpoint`；54..57 = `FixedNozzleSetpoint[1..4]`。DryRun（Mode 2）時流量歸零（`dryRunZero`），轉速照寫。換步、結束、離開 Execute（Complete、Stop、Abort、Hold、Suspend）時這些 SVID 寫回 0；Unhold、Unsuspend 回到 Execute 時目前這步從頭開始（重新計時、重寫 SVID）。
  - `Chamber.CupChamberClean` 另外：`CleanType` 為 1（FixedAtTop）時，`TargetCupIndex` 那個 Cup 的 Master、Slave 高度 SVID（45／46、47／48、49／50、51／52）寫成 Top Pos 的 ECID（276、288、300、312）；這兩個 SVID 不歸 0，值保留到下一次寫入或下一次 Run。`CleanType` 為 2（MoveUpDown）時不寫高度，SVID 保持原值（沒有 ramp）。
  - 被 `trigger.onServiceExecute` 接管的 service 不執行步驟。`Chamber.EMGS`、`Chamber.MoveRecipe`、`Chamber.DoNozzleClean`、`Chamber.DoNozzlePreflush` 不在表中，維持預設行為。
  - 這些 SVID（53、Arm 的 Nozzle 流量、54..57）與其他 service 共用，沒有擁有者檢查（同上）。
- 同一個 SVID 由多個 service 寫入（例 Arm1 的 `Arm1 Rotate Speed` 由 9 個 Rotate service 寫入）時沒有擁有者檢查：兩個 service 同時在 Execute 時，先離開的寫 0。模擬器不阻擋同一軸的 service 同時執行。

`ranges` 可以用整數、REAL、LREAL 欄位，`min`、`max` 可為小數（REAL 欄位的界線以 REAL 數值比較，兩端皆含）。寫入值多一種 `addParam`：寫入「目前 SVID 值 + 該 ServiceParam 欄位」（`negate` 只對欄位值生效）；目前值不是數字或不是有限值時以 0 計，欄位值是 NaN、∞ 時加 0。高度 SVID 只存在 process image，Run 時重設為 `"0.0"`。

注意：

- Mode 改成 0 之後只有 `Chamber.SetMode` 能跑，手動測試完要用 SetMode 把 Mode 寫回。
- Run 之後到 `Polar2_ModuleModeInit` 寫 `'1'` 之前，SVID 1 是初始值 `"0"`，Mode 是 Disable；`Polar2_ModuleModeInit` 載入失敗時一直如此。
- `duration`、`beforeComplete` 只對 NaturalCompletion 的 service 有作用；寫在 ExecuteUntilExternalStop 的 service 上不會報錯，但沒有效果（Host 在 Run 時不讀 identity file，無法發現）。
- 重新載入 `Polar2_ServiceHost` 時，Execute 中的 service 不經過離開 Execute，`onLeave` 不會寫，速度、流量、壓力 SVID（例 SVID 53、Arm 的 Rotate Speed、Nozzle Flow/Pressure、ExhaustFan Pressure）可能停在 Execute 時的值。

`sim`（#23）：每個 service 可以加一個獨立的 `sim` 清單，service 執行時經 `/live` 送動畫指令給 Simulation 頁。`sim` 與 `onEnter`、`beforeComplete`、`onLeave` 互不影響；省略時為空清單，明寫 `"sim": null` 載入失敗。

```json
"Arm[1].Rotate.Noz1CenterPos": {
  "onEnter": [...], "beforeComplete": [...], "onLeave": [...],
  "sim": [
    { "when": "enter", "target": "Arm1.Rotate", "action": "Noz1Center", "value": { "ecid": 23 } },
    { "when": "beforeComplete", "target": "Arm1.Rotate", "action": "Noz1Center", "value": { "ecid": 23 } },
    { "when": "leave", "target": "Arm1.Rotate", "action": "Hold" }
  ]
}
```

| 欄位 | 說明 |
|---|---|
| `when` | 送出時機（不分大小寫）：`enter`（進入 Execute；Unhold、Unsuspend 回到 Execute 時再送一次）、`beforeComplete`（自然完成前，`durationMs` 為 0）、`leave`（離開 Execute，包含完成、Hold、Abort）。 |
| `target` | 頁面上的零件名稱，不可空白。 |
| `action` | 動作名稱，不可空白。 |
| `value` | 選填，數值標籤的來源，恰好一個：`constant`（有限數）、`param`（`ServiceParam.<service>` 下的欄位）、`ecid`（ECID 編號）；可加 `negate`；不可用 `addParam`。 |
| `dryRunAction` | 選填。進入 Execute 時 Mode 是 DryRun（Mode 2）就改送這個 action，`value` 送 null。 |

行為與限制：

- `enter` 的 `durationMs` 是 service 的完整長度：NaturalCompletion 的 service 為 `duration` 欄位或 `defaultDelayMs`，其他（ExecuteUntilExternalStop、有 `process` 的 service）為 null。
- 被拒絕的 entry（Mode 不允許、參數超出範圍）與被 workflow 接手的 service 不送。
- Hold 只會廣播，Host 不存進 `simState`（存了會蓋掉最後的定位名稱）。Hold 中途重新整理頁面時，零件顯示的是目標位置。
- 指令以 `target` 為單位，不以 service 為單位；同一個 target 上兩個 service 重疊時，先結束的那個送出的 `Hold`、`Stop` 會影響後開始的那個。
- 載入時驗證：`when` 不認得、`target`、`action` 空白、`value` 來源不是恰好一個、用 `addParam`、`dryRunAction` 空白、有未知欄位，或 ECID、`param` 欄位不存在，Run 失敗，訊息帶檔名與位置。

目前加了 `sim` 的 67 個 service 與 target：

| target | action | service | 數值標籤（單位） |
|---|---|---|---|
| `Holder` | `CW`、`CCW`、`Stop` | `Holder.Rotate.MoveVelocityCW`（enter `CW`）、`MoveVelocityCCW`（enter `CCW`）、`MoveModulo`（enter `CW`）；leave `Stop` | ECID 318（RPM） |
| `OutsideRing` | `CW`、`CCW`、`Stop` | `OutsideRing.Rotate.MoveVelocityCW`、`MoveVelocityCCW`；leave `Stop` | ECID 361（RPM） |
| `Cup{n}.Master`、`Cup{n}.Slave`（n = 1–4） | `Top`、`Middle`、`Down`、`Hold` | `Cup[n].Lift.TopPos`、`MiddlePos`、`DownPos`：enter、beforeComplete 送定位名稱，leave `Hold` | 該 service `beforeComplete` 寫入的 ECID（mm） |
| `Arm{n}.Lift` | `Top`、`Down`、`Hold` | `Arm[n].Lift.TopPos`、`DownPos`：同上 | 同上（mm） |
| `Arm{n}.Rotate` | `NegLimit`、`PosLimit`、`NozzlePort`、`Noz1Center`、`Noz2Center`、`Noz3Center`、`Hold` | `Arm[n].Rotate.<名稱>Pos`（Arm1、Arm2 各 6 個，Arm3 4 個，Arm4 3 個）：同上 | 同上（deg） |
| `Arm{n}.Nozzle{k}` | `On`、`Off` | `Arm[n].Nozzle[k].ChemicalSupply`：enter `On`（`dryRunAction`: `Off`），leave `Off` | `Setpoint`（ml/s） |
| `Arm{n}.Port{k}` | `On`、`Off` | `Arm[n].NozzlePort.Nozzle[k].Supply`（Arm1、Arm2）：同上 | `Setpoint`（ml/s） |
| `FixedNozzle{n}` | `On`、`Off` | `FixedNozzle[n].ChemicalSupply`：同上 | `Setpoint`（ml/s） |
| `ExhaustFan{n}` | `On`、`Off` | `Exhaust.Fan[n].SupplyFlowPID`：enter `On`，leave `Off` | 該 service `onEnter` 寫入的 ECID（Pa） |

不加 `sim` 的有 Chamber 的 5 個複合 service、所有 Jog、IncrementalJog、`Arm[n].NozzlePort.DrainSelect*` 與其他沒列出的 service。

## Simulation 頁

Run 之後，開啟 `http://localhost:5259/simulation/`（Host 的 HTTP 位址）可以看到 Polar2 的 isometric 機構動畫：Holder（含夾爪與真空吸盤）、OutsideRing（含升降）、4 支 Arm、4 層 Cup 與 Port 杯、牆上的 4 個 Fixed Nozzle、Shutter（門）、箱體的 4 個 SideVent 與風扇、Damper，依 `sim` 指令動作。頁面只顯示，不能操作；Stop 專案後顯示「Polar2 未執行」。

頁面放在 `simulation/`：`build.mjs` 產生 `index.html`，`kit/iso-kit.mjs` 是繪圖工具，`Chamber.png` 是配置依據的手繪圖。改了 `parts.mjs`、`runtime.js` 或 `build.mjs` 之後，在 `projects/Polar2Chamber/simulation/` 執行：

```
node build.mjs
```

會重新產生 `index.html`（要一起 commit，Host 直接提供這個檔案）。`build.mjs` 在寫出前會檢查 `config/service-behaviors.json` 每個 service 的每筆 `sim`（含 `dryRunAction`）：`target` 與 `action` 必須在 `parts.mjs` 的 `TARGETS` 裡（不分大小寫）。有不符時每筆輸出一行 `sim mismatch: <service> <target> <action>`，exit code 為 1，不寫 `index.html`。環境變數 `POLAR2_SIM_CONFIG` 有值時改讀該路徑的檔案。

### 外形與新增的 target（#24）

箱體在平台下方，4 面各有一個百葉 SideVent 與一個風扇（面 1 為 +x，2 為 +y，3 為 −x，4 為 −y）。箱體底下是 Damper：翻板在 AEX（酸排）與 SEX（溶劑排）兩支管之間。外罩牆、箱體牆與平台都是半透明，所以背面的 SideVent 3、4 與箱體內的風扇看得到。Shutter 是 +y 面的門，Fixed Nozzle 在外罩牆內側，4 支 Arm 的停放點都有 Port 杯。

| target | action | 說明 |
|---|---|---|
| `Holder` | `CW`、`CCW`、`Stop`、`Home`、`Hold` | `Home`（OPR）以最短路徑轉到最近的 360° 倍數；`Hold` 停在當下角度 |
| `Holder.Gripper` | `Open`、`Close`、`Hold` | 6 個夾爪頭轉向或轉離晶圓邊緣 |
| `OutsideRing.Lift` | `Top`、`Down`、`Hold` | 升降量只是示意（8 mm），不依 ECID |
| `Shutter` | `Open`、`Close`、`Hold` | 門上下移動 |
| `SideVent{n}`（n = 1–4） | `Open`、`Close`、`Hold` | 百葉開度 0 或 75° |
| `Damper` | `AEX`、`SEX`、`Hold` | 翻板位置 |
| `Cup{n}.Drain`、`Arm{n}.PortDrain`（n = 1、2） | `Select1`、`Select2`、`Select3` | 只顯示標籤 `Drain K`，不畫管路；沒有 `leave`，標籤保留到下一個 `SelectK` |
| `Arm{n}.Rotate`、`Cup{n}.Master`、`Cup{n}.Slave` | 既有，加 `JogPositive`、`JogNegative`、`IncrementalJog`、`StepDone` | 見下 |
| `ExhaustFan{n}` | `On`、`Off` | 風扇轉動；每個 SideVent 的風扇都以每秒 360° 轉 |

### 排氣流線與甩液

- 排氣流線：`ExhaustFan{n}` 是 On，而且 `SideVent{n}` 開度超過一半時，SideVent n 外側顯示 3 條流動的藍色虛線。
- 甩液：Holder 在轉（`CW`、`CCW`），而且任一 `Arm{n}.Nozzle{m}` 或 `FixedNozzle{n}` 是 On 時，晶圓邊緣向外畫液滴線段（Port 不算，DryRun 的送液類 `dryRunAction` 是 `Off`，所以不會甩液）。液滴數看 Holder 的 RPM（`value`）：低於 300 為 8 個、300 到 1000 或沒有數值為 16 個、高於 1000 為 32 個。液滴飛到第一層升起的 Cup（`Top` 附近）的內壁為止；沒有 Cup 升起時畫到 Cup4 外壁。液滴位置由時間算出，不用亂數。

### Jog 與重新整理

- `JogPositive`、`JogNegative`：從目前值等速移動，Arm 每秒 10°（範圍 −48° 到 35°），Cup 每秒 6 mm（範圍 Down 到 Top）；到邊界停住，直到收到 `Hold`。標籤顯示 `value`，單位為 RPM 或 mm/s（Jog 速度的 ECID 單位）。
- `IncrementalJog`：依 `value` 的正負走一步（Arm 5°、Cup 4 mm），`value` 為 0 不動。完成前的 `StepDone` 讓畫面對齊這一步的目標，不依賴頁面與 Host 的計時剛好相同。Hold 再 Unhold 時沿用同一個目標，不會多走一步。
- 頁面不知道 Jog 停在哪裡，所以收到 `Hold`（上一個 action 是 Jog 類）時，把凍結後的值存進瀏覽器的 `localStorage`（key 為 `polar2-sim:<專案名稱>:<target>`）。重新整理後的 `simState` 重播 Jog 類 action 時，頁面不移動，直接讀這個值；沒有存值就留在初始位置。target 收到 Jog 類以外的 action（例如 `Top`、`NozzlePort`）時刪除存值。

### 已知限制

- 換瀏覽器或清除資料後，Jog 之後的位置回到初始位置；Jog 進行中重新整理，頁面顯示上次存的值而不動。
- 瀏覽器存的 Jog 位置在 Host 重啟後仍在，所以重啟後在 Jog 進行中或 Jog 之後重新整理，頁面可能顯示上一次執行的位置，直到 target 收到定位類 action 為止（重新整理與新的一次執行都是先收到 `project` 再收到 `simState`，頁面分不出兩者）。
- `/live` 佇列滿時 Host 丟最舊的訊息，遺失的 `Hold` 會讓存值停在更早的位置。
- IncrementalJog 被 Stop 或 Abort（沒有 `StepDone`）後，下一次同方向的 IncrementalJog 先走完上次的目標；Unhold 後剩下的距離以完整的 `durationMs` 走完，畫面速度變慢。
- 任一風扇 On 時頁面每幀重畫（DryRun 也一樣）；頁面不可見時不畫。
- Hold 或 Abort 後重新整理，定位類零件顯示目標位置。

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
