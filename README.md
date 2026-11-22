# Auto Display Mode

常駐在 Windows 工作列的小工具：在多組螢幕配置（模式）之間手動或自動切換。例如離開電腦 5 分鐘就只留一台螢幕，一動滑鼠就全部打開。

## 功能

- **模式**：記錄每台螢幕的開關、位置、解析度、更新率與方向，用類似 Windows 顯示設定的畫布拖拉排列。
- **切換條件**：「在 A 模式時閒置 N 分鐘／偵測到使用 → 切到 B 模式」，使用來源可選鍵盤滑鼠與聲音。
- **潛行模式**：關閉所有實體螢幕，只留一個虛擬螢幕給遠端連線軟體（例如 Chrome 遠端桌面）使用。程式閃退、被強制結束或斷電時，都會自動切回實體螢幕。
- 以 EDID 的廠牌、型號與序號辨識螢幕，換接孔也認得。
- 免安裝，單一資料夾即可執行，設定存在旁邊的 `AutoDisplayMode.cfg`。

## 使用

需要 Windows 10／11（.NET Framework 4.8 已內建）。

使用說明請看 [docs/release/README.md](docs/release/README.md)，建置後也會附在 `release\README.md`。

## 從原始碼建置

需要 [.NET SDK](https://dotnet.microsoft.com/download)。在專案根目錄執行：

```bat
build.bat
```

它會建置並執行測試，再把程式、驅動與使用說明輸出到 `release\`。`release\` 整個刪掉也能重新產生；設定檔則是程式第一次執行時才建立。

## 專案結構

| 路徑 | 內容 |
|---|---|
| `src/AutoDisplayMode.Core/` | 核心邏輯（模式、切換引擎、cfg），不依賴 Windows API，全部以測試驅動 |
| `src/AutoDisplayMode/` | WinForms 介面與 Windows 轉接層（顯示設定 API、鍵盤滑鼠鉤子、驅動） |
| `tests/` | xUnit 測試 |
| `drivers/sudovda/` | 潛行模式用的虛擬螢幕驅動（簽好的檔案與重新編譯的腳本） |
| `docs/adr/` | 架構決策紀錄 |
| `GLOSSARY.md` | 用語定義 |

## 虛擬螢幕驅動

潛行模式使用 [SudoVDA](https://github.com/SudoMaker/SudoVDA)（以 Microsoft Indirect Display Driver 範例為基礎）。本專案從原始碼自行編譯，並以一張拋棄式的自簽憑證簽章，簽完後私鑰已刪除（[ADR-0005](docs/adr/0005-bundle-self-signed-sudovda.md)）。

在程式中按「安裝驅動」時，會把這張憑證匯入本機的受信任根憑證，按「移除驅動」時一併刪除。不想信任這裡的憑證的話，可以用 [`drivers/sudovda/build-driver.ps1`](drivers/sudovda/README.md) 自己編譯、簽一份。

## 授權

本專案以 [MIT License](LICENSE) 授權。內附的 SudoVDA 驅動另有授權聲明，見 [`drivers/sudovda/LICENSE.txt`](drivers/sudovda/LICENSE.txt)。
