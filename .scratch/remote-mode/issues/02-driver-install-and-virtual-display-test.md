# 02: 驅動安裝與移除（成品）＋虛擬螢幕實測

**Type:** task + prototype

**What to build:** 兩件事。

1. **驅動安裝與移除（原票 05 併入）：** 在程式的設定視窗中安裝與移除 SudoVDA 驅動（使用者故事 31、32），做進成品。驅動檔**放在 exe 旁邊**的 `drivers/sudovda/`，不嵌進 exe（使用者決定）：換驅動版本或憑證時只要替換檔案。安裝與移除放在一個獨立的介面後面；萬一這個做法實測失敗，只要替換那一個實作。
2. **虛擬螢幕實測：** 驗證 spec「待原型驗證的假設」。精靈 `prototype/wizard.sh` 帶使用者操作：安裝、移除、重新安裝都**直接操作程式的介面**；建立虛擬螢幕、只留虛擬螢幕、強制結束等實測，用拋棄式的 VddProbe 進行。結果寫進本票的 Answer；若推翻 spec 的假設，就提出票 03、04 要怎麼改。

**Blocked by:** 01（`build.bat` 改輸出到 `release/`，並把驅動檔複製到 `release/drivers/sudovda/`）

**Status:** ready-for-agent（實作）；實測需要使用者本人跑精靈（UAC、Chrome 遠端桌面）

### 驅動安裝與移除

- [x] 程式從「exe 所在資料夾\drivers\sudovda\」找驅動檔（`x64\SudoVDA.inf`、`.dll`、`.cat`）與公開憑證；找不到時顯示應該放在哪裡
- [x] 一般設定分頁顯示驅動狀態（未安裝、已安裝、找不到驅動檔、無法使用），並有「安裝驅動」「移除驅動」兩個按鈕
- [ ] 「安裝驅動」：程式以系統管理員身分重新啟動自己（例如 `--install-driver`，跳出 UAC），匯入憑證到「受信任的根憑證」與「受信任的發行者」，建立裝置節點，安裝 INF；完成後狀態更新
- [ ] 「移除驅動」：同樣提權，移除裝置與驅動套件，並刪除安裝時匯入的憑證
- [x] 只用 Windows 內建的 API（SetupAPI、`UpdateDriverForPlugAndPlayDevices`、`DiUninstallDevice`、`SetupUninstallOEMInf`、`X509Store`），不依賴 WDK、devcon、nefcon；做法可參考 VddProbe 原本的實作（見 Comments）
- [x] 安裝與移除放在獨立的介面後面，核心或介面層不直接呼叫 SetupAPI
- [ ] 使用者取消 UAC，或安裝、移除失敗時，顯示原因，狀態維持不變
- [x] 一般設定分頁的說明補上驅動的用途、安裝與移除，以及為什麼需要系統管理員權限
- [ ] 把 `release/` 資料夾（exe＋drivers）複製到別的位置執行，也能正確找到驅動檔
- [x] VddProbe 拿掉 `install`／`uninstall` 指令，避免有兩套做法；精靈第 2、8、9 階段改成請使用者操作程式的介面（同時當作 UI 的手動測試），精靈不再自動安裝

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
  - 原票 05 刪除；「驅動不可用時，潛行模式旁顯示警告與安裝按鈕」移到票 03。
- 放在 exe 旁邊，對載入時間與記憶體幾乎沒有影響：內嵌資源要讀取時才會載入，驅動也只有約 90 KB。真正的好處是：
  - 換驅動或憑證時，只要替換檔案。
  - 建置不必處理內嵌與解壓。
  - 看資料夾就知道裝的是哪一版。
- 代價：搬到別台電腦時，要複製整個 `release/` 資料夾，不再只有單一 exe。
- 可參考的實作：VddProbe 在 commit `ef23ecc` 中的 `Install`／`Uninstall`。

### 2026-11-21 精靈改成操作程式的介面

- 精靈的第 2、8、9 階段不再自動安裝或移除，改成請使用者在設定視窗的「一般設定」按「安裝驅動」或「移除驅動」，再由 VddProbe `status` 檢查結果。
- 精靈從 `release/drivers/sudovda/` 找驅動檔，並確認 `release/AutoDisplayMode.exe` 存在。
- 精靈也記錄使用者回報的 UI 結果：`UI_INSTALL_OK`／`UI_UNINSTALL_OK`／`UI_REINSTALL_OK` 與對應的 `*_NOTE`。
- VddProbe 已拿掉 `install`／`uninstall`。
- 要使用者跑精靈時，給完整的指令（在 PowerShell 貼上，要用 Git Bash 執行，不能用 WSL 的 `bash`）：

```powershell
& "C:\Program Files\Git\bin\bash.exe" "C:/Users/Jimmy/Desktop/工具/Auto Display Mode/.scratch/remote-mode/prototype/wizard.sh"
```

### 2026-11-21 驅動安裝與移除實作完成（待精靈實測）

#### 介面與實作

- 核心 `Ports.cs` 新增 `IVirtualDisplayDriverInstaller`：
  - `PackageFolder`：驅動檔應該放的資料夾。
  - `GetStatus()`：回傳 `DriverStatus`，內容是狀態（`NotInstalled`／`Installed`／`FilesMissing`／`Unavailable`）、補充說明、`CanInstall`、`CanUninstall`。
  - `InstallAsync()`／`UninstallAsync()`：回傳 `DriverOperationResult`（是否成功、訊息）。
  - 介面層只透過這個介面操作，不直接呼叫 SetupAPI。萬一實測失敗，只要替換 Windows 轉接層的兩個檔案。
- `Windows/SudoVdaDriverInstaller.cs`：實作這個介面，負責提權與子程序。
  - 以 `runas` 重新啟動本程式，參數是 `--install-driver` 或 `--uninstall-driver`，加上 `--result=<暫存檔>`。子程序把結果寫進暫存檔（`OK`／`REBOOT`／`FAIL` 加訊息），父程序在背景等它結束，不卡住 UI。
  - UAC 按「否」（錯誤 1223）時回報「已取消：沒有取得系統管理員權限，驅動沒有變更。」
  - 安裝成功後，最多等 10 秒讓裝置啟動、出現裝置介面。等不到時仍算成功，但會提醒「驅動已安裝，但還無法使用」並附上原因。
- `Program.Main` 一開始先呼叫 `SudoVdaDriverInstaller.TryRunElevatedCommand(args)`，在拿 mutex 之前處理參數。子程序不建立 TrayApp 與協調者，也不碰 cfg；它會檢查自己是不是系統管理員、是不是 64 位元程序。exe 是 ILONLY（沒有偏好 32 位元），在 64 位元 Windows 上以 64 位元執行。
- `Windows/SudoVdaSetup.cs`：偵測、安裝與移除，只用 Windows 內建的 API。
  - 驅動檔從「exe 所在資料夾\drivers\sudovda\」找：`x64\SudoVDA.inf`、`x64\SudoVDA.dll`、`x64\sudovda.cat`、`AutoDisplayMode-SudoVDA.cer`。路徑來自 `Application.ExecutablePath`，所以 `release/` 搬到別處也找得到。
  - 「本程式的」驅動以憑證主旨開頭 `CN=AutoDisplayMode SudoVDA` 辨識。憑證直接比對主旨；驅動套件看驅動存放區中目錄檔的簽章者；裝置看它驅動機碼的 `InfPath` 是不是本程式的套件。
    - 好處一：新版程式也能移除舊版的驅動與憑證（每次編譯都換憑證，但主旨開頭不變）。
    - 好處二：其他軟體（例如 Apollo）裝的 SudoVDA，硬體 ID 雖然相同，也不會被當成本程式的驅動。這時狀態是「無法使用」，不能安裝，也不會被移除。
  - 狀態的判斷順序：
    1. 有其他軟體的 SudoVDA 裝置：無法使用。
    2. 有本程式的裝置，而且驅動已啟動、沒有問題代碼、有裝置介面：已安裝。
    3. 有本程式的裝置，但不符合上一條：無法使用，附上裝置管理員的問題代碼說明（例如 14 需要重新開機、52 無法驗證簽章）。
    4. 沒有裝置，而且缺檔案：找不到驅動檔，附上應該放的位置與缺少的檔案。
    5. 其他情況：未安裝。
    - 電腦上還留有本程式的裝置、驅動套件或憑證時，「移除驅動」可以按（未安裝時會提示可以清除）。
  - 安裝：匯入憑證到「受信任的根憑證」與「受信任的發行者」（已有就不重複）；沒有本程式的裝置時，建立根列舉的裝置節點；最後執行 `UpdateDriverForPlugAndPlayDevices`（`INSTALLFLAG_FORCE`）。
    - 失敗時復原這次做的事：新建的裝置節點（`DIF_REMOVE`）、新加入存放區的驅動套件、新匯入的憑證，所以狀態維持不變。
    - 錯誤訊息附上錯誤代碼，安裝驅動失敗時另外指出 `C:\Windows\INF\setupapi.dev.log`。
  - 移除：依序移除本程式的裝置（`DiUninstallDevice`）、本程式的驅動套件（`SetupUninstallOEMInf`，`SUOI_FORCEDELETE`）、所有版本的憑證。每一步都會執行，最後一起回報失敗的步驟。
  - 讀取狀態不需要系統管理員權限。在這台電腦上約 360 毫秒，主要花在掃描 `C:\Windows\INF\oem*.inf`（204 個），所以一律在背景執行緒讀取。

#### 設定視窗

- 一般設定分頁最後新增「虛擬螢幕驅動」一節：
  - 「狀態：」顯示已安裝、未安裝、找不到驅動檔或無法使用；後兩者是紅字，下方有灰色的補充說明。
  - 「安裝驅動」「移除驅動」兩個按鈕依 `CanInstall`／`CanUninstall` 啟用。
  - 最下面的提示寫明需要系統管理員權限（UAC），而且按下後立即執行、不必按「套用」。
- 開啟設定視窗、每次切換到這個分頁、套用之後，都會在背景重新讀取狀態。
- 按鈕按下後：狀態顯示「安裝中…」或「移除中…」，提示請在 UAC 按「是」，兩個按鈕停用；完成後重新讀取狀態。失敗時跳出「無法安裝驅動：…」或「無法移除驅動：…」；成功但有提醒時（例如需要重新開機）跳出提示。
- 這兩個動作不呼叫 `MarkEdited`，跟副本的套用無關：不會出現「有尚未套用的變更」，按「取消」也不會復原。設定視窗在等待中被關閉時，結果直接丟掉，不碰已釋放的控制項。
- 說明（F1）新增【虛擬螢幕驅動】一段：驅動的用途、驅動檔的位置與搬移方式、安裝與移除各做了什麼、為什麼需要系統管理員權限、各狀態的意思。
- 按鈕名稱與狀態文字（「安裝驅動」「移除驅動」「已安裝」「未安裝」）跟精靈的提示一致，精靈不必修改。

#### 其他

- VddProbe `status` 新增 `PACKAGES=`，列出驅動存放區中提到 SudoVDA 的套件。精靈第 8 階段多記錄 `UNINSTALL_CERT_PUBLISHER` 與 `UNINSTALL_PACKAGES`，用來確認移除後憑證與驅動套件都清乾淨了。
- `drivers/sudovda/README.md` 補上安裝與移除的做法，以及「不要改掉憑證主旨開頭」；`build-driver.ps1` 產生憑證的地方也加上同樣的註解。

#### 檢查

- 建置 0 警告、0 錯誤；測試 230 個全部通過。核心只新增介面與資料型別，沒有新的邏輯，所以沒有新增測試。
- 以拋棄式小程式（假的轉接層、真正的 cfg 內容，150%）開啟真正的設定視窗並截圖：
  - 用真正的實作讀這台電腦的狀態：「未安裝」，只有「安裝驅動」可以按。
  - 驅動資料夾指向不存在的位置：紅字「找不到驅動檔」，列出應該放的位置與缺少的 4 個檔案，兩個按鈕都停用。
  - 假的「無法使用」：紅字與問題說明，兩個按鈕都可以按。
  - 各種狀態下都沒有出現「有尚未套用的變更」。
- 安裝前的基準（VddProbe `status`）：`DEVICE_COUNT=0`、`INTERFACE=(none)`，兩個憑證存放區與 `PACKAGES` 都是 `(none)`。
- 沒有實際執行提權的子程序（會變更這台電腦的驅動），交給精靈實測。

#### 實測前的準備

1. 從工作列關閉正在執行的舊版程式，執行 `build.bat`：最後印出 `Build OK`。實作時舊版程式正在執行，所以 `release/AutoDisplayMode.exe` 還是舊版（複製失敗；建置與測試都有跑）。
2. 啟動新版的 `release/AutoDisplayMode.exe`，在設定視窗的一般設定分頁確認看得到「虛擬螢幕驅動」一節，狀態是「未安裝」。
3. 跑精靈（指令見上一則）。精靈之外，另外手動測試：
   - 按「安裝驅動」，UAC 按「否」：跳出「無法安裝驅動：已取消…」，狀態仍是「未安裝」。
   - 把 `release/` 整個資料夾複製到別的位置（例如桌面），從那裡執行：一般設定分頁的狀態跟原本一樣。這時如果原本的程式還開著，新複本會被單一執行個體擋下，所以要先關掉原本的。
