# 01: 改用 .NET SDK 建置，拆出核心與 Windows 轉接層（行為不變）

**What to build:** 先重構好地基，讓後續修改變簡單，使用者看到的行為完全不變。專案改為 SDK 風格、目標 .NET Framework 4.8 + WinForms、使用最新 C# 語法（ADR-0001），並新增 xUnit 測試專案（同為 .NET Framework 4.8）。把現有的切換邏輯（閒置判斷、聲音持續判斷、緩衝期、手動切換、cfg 讀寫）搬進不引用 Windows API 的核心，透過介面（螢幕切換執行、使用訊號、時鐘、cfg 儲存、開機啟動、通知）與 Windows 轉接層互動；此階段螢幕切換仍由轉接層呼叫 bat 完成。建置腳本改為呼叫 `dotnet build`／`dotnet test`。核心對外提供單一的協調者入口，作為規格書所定的唯一測試接縫。

**Blocked by:** None (can start immediately)

**Status:** ready-for-agent

- [ ] 以 `dotnet build` 建置出的 exe 在 Win10/11 不需另裝 runtime 即可執行
- [ ] 建置腳本可一鍵建置並執行測試
- [ ] 核心不引用任何 Windows API、WinForms 或登錄檔
- [ ] 測試透過協調者入口與假物件，鎖住現有行為：閒置達指定分鐘切到省電模式、省電模式中偵測到使用切回遊玩模式、聲音需持續達門檻秒數才算使用、切換期間與切換後緩衝期不觸發、自動切換關閉時不動作
- [ ] 手動實測：工作列選單、自動切換、開機啟動、說明、重新載入 cfg 的行為與重構前一致
