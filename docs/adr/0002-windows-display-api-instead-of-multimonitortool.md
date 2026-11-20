# 直接呼叫 Windows 顯示設定 API，不再使用 MultiMonitorTool

舊版透過 bat 呼叫 NirSoft MultiMonitorTool 切換螢幕，螢幕數值寫死在 bat 中。新版需要在設定視窗偵測目前接上的螢幕、列出其支援的解析度與更新率，並套用每個模式的螢幕設定，因此改為程式直接呼叫 Windows 顯示設定 API 完成偵測與套用，移除 bat 與 MultiMonitorTool 這個第三方依賴；舊檔案只保留在 git 歷史中。
