# 02: 驅動安裝與移除（成品）＋虛擬螢幕實測

**Type:** task + prototype

**What to build:** 兩件事。

1. **驅動安裝與移除（原票 05 併入）：** 在程式的設定視窗中安裝與移除 SudoVDA 驅動（使用者故事 31、32），做進成品。驅動檔**放在 exe 旁邊**的 `drivers/sudovda/`，不嵌進 exe（使用者決定）：換驅動版本或憑證時只要替換檔案。安裝與移除放在一個獨立的介面後面；萬一這個做法實測失敗，只要替換那一個實作。
2. **虛擬螢幕實測：** 驗證 spec「待原型驗證的假設」。精靈 `prototype/wizard.sh` 帶使用者操作：安裝、移除、重新安裝都**直接操作程式的介面**；建立虛擬螢幕、只留虛擬螢幕、強制結束等實測，用拋棄式的 VddProbe 進行。結果寫進本票的 Answer；若推翻 spec 的假設，就提出票 03、04 要怎麼改。

**Blocked by:** 01（`build.bat` 改輸出到 `release/`，並把驅動檔複製到 `release/drivers/sudovda/`）

**Status:** ready-for-agent（實作）；實測需要使用者本人跑精靈（UAC、Chrome 遠端桌面）

### 驅動安裝與移除

- [ ] 程式從「exe 所在資料夾\drivers\sudovda\」找驅動檔（`x64\SudoVDA.inf`、`.dll`、`.cat`）與公開憑證；找不到時顯示應該放在哪裡
- [ ] 一般設定分頁顯示驅動狀態（未安裝、已安裝、找不到驅動檔、無法使用），並有「安裝驅動」「移除驅動」兩個按鈕
- [ ] 「安裝驅動」：程式以系統管理員身分重新啟動自己（例如 `--install-driver`，跳出 UAC），匯入憑證到「受信任的根憑證」與「受信任的發行者」，建立裝置節點，安裝 INF；完成後狀態更新
- [ ] 「移除驅動」：同樣提權，移除裝置與驅動套件，並刪除安裝時匯入的憑證
- [ ] 只用 Windows 內建的 API（SetupAPI、`UpdateDriverForPlugAndPlayDevices`、`DiUninstallDevice`、`SetupUninstallOEMInf`、`X509Store`），不依賴 WDK、devcon、nefcon；做法可參考 VddProbe 原本的實作（見 Comments）
- [ ] 安裝與移除放在獨立的介面後面，核心或介面層不直接呼叫 SetupAPI
- [ ] 使用者取消 UAC，或安裝、移除失敗時，顯示原因，狀態維持不變
- [ ] 一般設定分頁的說明補上驅動的用途、安裝與移除，以及為什麼需要系統管理員權限
- [ ] 把 `release/` 資料夾（exe＋drivers）複製到別的位置執行，也能正確找到驅動檔
- [ ] VddProbe 拿掉 `install`／`uninstall` 指令，避免有兩套做法；精靈第 2、8、9 階段改成操作程式的介面

### 驅動編譯

- [x] 記錄編譯環境（Visual Studio／WDK 版本）與步驟，做成可重複的腳本或說明，供日後換驅動版本時使用
- [x] 簽好的驅動檔與公開憑證放進 repo（`drivers/sudovda/`），附上 SudoVDA 的授權聲明；私鑰已刪除

### 實測（精靈）

- [ ] 一般 Windows 11 上，用程式的介面能安裝（不開測試模式），也能完整移除（含憑證）
- [ ] 一般使用者權限就能新增、移除虛擬螢幕，並指定解析度與更新率；記錄實際可選的解析度清單
- [ ] 心跳停止（測試程式被工作管理員結束）後，虛擬螢幕在幾秒內被移除；記錄實際秒數
- [ ] 先建立虛擬螢幕再關閉所有螢幕，在只剩虛擬螢幕時用 Chrome 遠端桌面連線：看得到畫面、可以操作；記錄「調整桌面大小以符合」的行為
- [ ] Chrome 遠端桌面送來的滑鼠、鍵盤輸入在低階鉤子中帶有注入旗標（`LLMHF_INJECTED`／`LLKHF_INJECTED`）
- [ ] 所有螢幕關閉、虛擬螢幕被移除後（心跳停止），Windows 怎麼還原實體螢幕：是否恢復成移除前最後一次只有實體螢幕的配置，還是黑畫面
- [ ] 現有程式的偵測（`WindowsMonitorSystem.Detect`）如何呈現虛擬螢幕，記錄辨識它的方法，供票 03 排除它（已初步確認可用介面卡的硬體 ID 辨識，見 Comments）
- [ ] 睡眠喚醒後虛擬螢幕是否還在
- [ ] Meta Virtual Monitor、Wacom MovinkPad 等既有虛擬顯示不受影響

## Answer

## Comments

### 2026-11-21 編譯完成，實測交給精靈

#### 編譯環境

- Visual Studio 2022 Community 17.14：另外加裝「Spectre 緩解程式庫」與「Windows Driver Kit」兩個元件。
- WDK 10.0.26100.6584（winget `Microsoft.WindowsWDK.10.0.26100`）。
- SudoVDA 固定在 commit `a4b09fa`（2025-04-15）。

#### 產出

- `drivers/sudovda/`：
  - `build-driver.ps1`：取得原始碼、編譯、執行 `infverif`、以拋棄式憑證簽章，最後刪除私鑰。
  - 簽好的 `x64/`、公開憑證 `AutoDisplayMode-SudoVDA.cer`、README、授權聲明。
- 這個 WDK 版本內建的 INF 驗證會去載入不存在的 `x86\InfVerif.dll` 而報錯。腳本以 `SkipPackageVerification=true` 關掉它，改成直接執行 `infverif.exe /u`；結果為 VALID，只有一個常見的 WUDFRd 警告。

#### 測試工具與精靈

- `.scratch/remote-mode/prototype/VddProbe/`：拋棄式測試工具。
  - 安裝與移除只用 Windows 內建的 API：SetupAPI、`UpdateDriverForPlugAndPlayDevices`、`DiUninstallDevice`、`SetupUninstallOEMInf`、`X509Store`。
  - 其他功能：建立虛擬螢幕並送心跳、只留虛擬螢幕（solo）、監看虛擬螢幕何時消失、記錄鍵盤滑鼠鉤子收到的輸入有沒有注入旗標。
- `.scratch/remote-mode/prototype/wizard.sh`：帶使用者跑完需要本人操作的 9 個階段。結果寫進 `.scratch/remote-mode/prototype-results.env`，紀錄檔放在 `prototype/logs/`。
- 已先確認：`VddProbe detect` 能透過介面卡的硬體 ID 分辨各個虛擬顯示，例如 `Root\Parsec\VDA`、`root\MetaVirtualScreenDriver`。票 03 可以用 `Root\SudoMaker\SudoVDA` 排除我們的虛擬螢幕。

### 2026-11-21 改變範圍：原票 05 併入

- 使用者決定：
  - 安裝與移除直接做進成品，精靈改成操作程式的介面。
  - 驅動檔放在 exe 旁邊，不嵌進 exe。
  - 原票 05 刪除；「驅動不可用時，遠端模式旁顯示警告與安裝按鈕」移到票 03。
- 放在 exe 旁邊，對載入時間與記憶體幾乎沒有影響：內嵌資源要讀取時才會載入，驅動也只有約 90 KB。真正的好處是：
  - 換驅動或憑證時，只要替換檔案。
  - 建置不必處理內嵌與解壓。
  - 看資料夾就知道裝的是哪一版。
- 代價：搬到別台電腦時，要複製整個 `release/` 資料夾，不再只有單一 exe。
- 可參考的實作：VddProbe 在 commit `ef23ecc` 中的 `Install`／`Uninstall`。
