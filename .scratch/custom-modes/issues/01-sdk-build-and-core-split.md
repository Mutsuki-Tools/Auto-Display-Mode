# 01: 改用 .NET SDK 建置，拆出核心與 Windows 轉接層（行為不變）

**What to build:** 先重構好地基，讓後續修改變簡單，使用者看到的行為完全不變。專案改為 SDK 風格、目標 .NET Framework 4.8 + WinForms、使用最新 C# 語法（ADR-0001），並新增 xUnit 測試專案（同為 .NET Framework 4.8）。把現有的切換邏輯（閒置判斷、聲音持續判斷、緩衝期、手動切換、cfg 讀寫）搬進不引用 Windows API 的核心，透過介面（螢幕切換執行、使用訊號、時鐘、cfg 儲存、開機啟動、通知）與 Windows 轉接層互動；此階段螢幕切換仍由轉接層呼叫 bat 完成。建置腳本改為呼叫 `dotnet build`／`dotnet test`。核心對外提供單一的協調者入口，作為規格書所定的唯一測試接縫。

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [x] 以 `dotnet build` 建置出的 exe 在 Win10/11 不需另裝 runtime 即可執行
- [x] 建置腳本可一鍵建置並執行測試
- [x] 核心不引用任何 Windows API、WinForms 或登錄檔
- [x] 測試透過協調者入口與假物件，鎖住現有行為：閒置達指定分鐘切到省電模式、省電模式中偵測到使用切回遊玩模式、聲音需持續達門檻秒數才算使用、切換期間與切換後緩衝期不觸發、自動切換關閉時不動作
- [x] 手動實測：工作列選單、自動切換、開機啟動、說明、重新載入 cfg 的行為與重構前一致

## Comments

### 2026-11-20 實作完成（待手動實測）

- 專案結構：`AutoDisplayMode.sln`；`src/AutoDisplayMode.Core`（核心，不引用 WinForms）、`src/AutoDisplayMode`（WinForms 介面層 + `Windows/` 轉接層）、`tests/AutoDisplayMode.Core.Tests`（xUnit，net48）。
- 核心原始碼直接編進 exe，產出維持單一 `AutoDisplayMode.exe`（只依賴 .NET Framework 4.8）。`build.bat` 依序執行 `dotnet build`、`dotnet test`，成功後把 exe 複製到專案根目錄（與重構前相同位置）。
- 協調者入口：`AppCoordinator`（`Start`、`Tick`、`ManualSwitch`、`SetAutoSwitch`、`SetIdleMinutes`、`SetAudioDetect`、`ToggleStartup`、`ReloadConfig`、`EnsureConfigFileExists`、`OnSwitchFinished`）。介面定義在 `Ports.cs`。
- 41 個測試全部通過；已在暫存資料夾試跑 exe，確認可啟動並建立預設 cfg。
- 尚未完成：最後一項手動實測（工作列選單、自動切換、開機啟動、說明、重新載入 cfg）需要在實際環境確認。
- 給後續票的備註：cfg 鍵名字串、`Mode` 兩值列舉、UI 中的「自動模式」字樣都刻意維持原樣（行為不變），預計在 02～04 替換。

### 2026-11-20 手動實測通過

- 依手動實測 SOP 逐項確認：工作列選單、手動與自動切換、聲音偵測、重新載入 cfg、開機啟動、說明，行為與重構前一致。
