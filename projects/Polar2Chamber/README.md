# Polar2Chamber 專案

Polar2 Chamber 的 PLC 宣告檔。模擬器載入這個專案，就有和真 PLC 相同的 `GVL_Module.Chamber_Control`（6 個 module，共 12678720 bytes）。本專案的 `plc/` 全部從下列來源原樣複製，不改內容。

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

`Polar2LayoutTests` 的預期值取自這個 `.tmc`（`BitOffs`、`BitSize` 除以 8）。

## Framework 升級時重新複製

1. 在 TSMC repo 取得新版本，記下新的 commit SHA。
2. 刪除 `plc/Framework/`。
3. 從 `RobustSolutionFramework/` 用原樣複製（`cp`，不轉碼、不改換行）所有 `.TcDUT`，保留子資料夾；再複製 `GVLs/Param_Config.TcGVL`。不複製 `.TcPOU`。
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
- 重新載入 `Polar2_ModuleModeInit` workflow 會把 SVID 1 寫回 `'1'`。
- 上面兩個 workflow 由後續的 unit U4 建立；U1 只提供 PLC 宣告檔與版面測試。

`plc/` 資料夾以 `.gitattributes` 的 `-text` 規則逐位元組保存，不做換行轉換；複製後的檔案與來源的 SHA-256 相同。

`.gitignore` 的 `[Aa][Rr][Mm]/` 規則會忽略 `plc/Polar2/DUTs/External/Arm/`，所以該資料夾的檔案要用 `git add -f` 加入。
