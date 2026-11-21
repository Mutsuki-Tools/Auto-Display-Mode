# 02: 虛擬螢幕原型（SudoVDA 編譯、簽章、安裝與實測）

**Type:** prototype

**What to build:** 用最少的程式碼驗證 spec「待原型驗證的假設」。先從原始碼編譯 SudoVDA，用拋棄式自簽憑證簽章，簽完刪除私鑰，在這台電腦上安裝（不開測試模式）。再寫一個拋棄式的小測試程式（放 scratch，不進產品），以 IOCTL 建立、移除虛擬螢幕並送心跳，逐項實測。結果寫進本票的 Answer，若推翻 spec 的假設，就提出票 03～05 要怎麼改。

**Blocked by:** 無

**Status:** ready-for-human（需要安裝 WDK、系統管理員權限，以及實際用 Chrome 遠端桌面連線）

- [x] 記錄編譯環境（Visual Studio／WDK 版本）與步驟，做成可重複的腳本或說明，供日後換驅動版本時使用
- [x] 簽好的驅動檔與公開憑證放進 repo（位置自訂，例如 `drivers/sudovda/`），附上 SudoVDA 的授權聲明；私鑰已刪除
- [ ] 一般 Windows 11 上能安裝（匯入憑證、建立裝置節點、安裝 INF），也能完整移除（含憑證）；測試工具的安裝與移除只用 Windows 內建的 API（SetupAPI、`X509Store`），先驗證票 05 的可攜做法
- [ ] 一般使用者權限就能新增、移除虛擬螢幕，並指定解析度與更新率；記錄實際可選的解析度清單
- [ ] 心跳停止（測試程式被工作管理員結束）後，虛擬螢幕在幾秒內被移除；記錄實際秒數
- [ ] 先建立虛擬螢幕再關閉所有螢幕，在只剩虛擬螢幕時用 Chrome 遠端桌面連線：看得到畫面、可以操作；記錄「調整桌面大小以符合」的行為
- [ ] Chrome 遠端桌面送來的滑鼠、鍵盤輸入在低階鉤子中帶有注入旗標（`LLMHF_INJECTED`／`LLKHF_INJECTED`）
- [ ] 所有螢幕關閉、虛擬螢幕被移除後（心跳停止），Windows 怎麼還原實體螢幕：是否恢復成移除前最後一次只有實體螢幕的配置，還是黑畫面
- [ ] 現有程式的偵測（`WindowsMonitorSystem.Detect`）如何呈現虛擬螢幕，記錄辨識它的方法（例如裝置路徑、EDID 廠牌型號），供票 03 排除它
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
