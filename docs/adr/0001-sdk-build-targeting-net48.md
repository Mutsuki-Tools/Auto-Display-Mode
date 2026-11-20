# 用 .NET SDK 編譯，但目標維持 .NET Framework 4.8

擴充成多模式、設定視窗與可拖拉的配置畫布後，程式規模大增，Windows 內建 csc.exe 只支援 C# 5、也難以建立測試專案。因此改用 .NET SDK（`dotnet build`）以最新 C# 語法編譯，但目標框架維持 .NET Framework 4.8 + WinForms：Win10/11 內建此執行環境，產出的 exe 不需另裝 runtime、檔案也小。

## Considered Options

- 維持內建 csc + C# 5：免安裝即可編譯，但大型 UI 程式寫起來吃力，且無法建立測試。
- 改用 .NET 9 + WinForms：高 DPI 支援較好，但目標電腦需安裝 .NET 9 Desktop Runtime，或打包成 60MB 以上的自封裝檔。
