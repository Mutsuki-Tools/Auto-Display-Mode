# SudoVDA 虛擬螢幕驅動

潛行模式用來建立虛擬螢幕的驅動（ADR-0005）。原始碼來自 [SudoMaker/SudoVDA](https://github.com/SudoMaker/SudoVDA)，由我們自行編譯，並以拋棄式自簽憑證簽章：簽完後私鑰立即刪除，所以這張憑證無法再簽其他任何東西。

## 檔案

- `x64/`：簽好的驅動套件（`SudoVDA.inf`、`SudoVDA.dll`、`sudovda.cat`）
- `AutoDisplayMode-SudoVDA.cer`：公開憑證。安裝驅動時匯入本機的「受信任的根憑證授權單位」與「受信任的發行者」，移除驅動時一併刪除
- `build-driver.ps1`：重新編譯與簽章的腳本，原始碼版本以 commit 固定
- `LICENSE.txt`：SudoVDA 與 Microsoft 範例程式碼的授權聲明

`build.bat` 建置時，把 `x64/`、公開憑證與 `LICENSE.txt` 複製到 `release/drivers/sudovda/`，程式從 exe 旁邊的這個資料夾找驅動檔。這裡的檔案才是正本，`release/` 中的複本不進 git。

## 重新編譯

需要 Visual Studio 2022（C++ 桌面開發、Spectre 緩解程式庫、Windows Driver Kit 元件）與 WDK 10.0.26100：

```powershell
powershell -ExecutionPolicy Bypass -File drivers\sudovda\build-driver.ps1
```

每次重新編譯都會換一張新憑證。已經裝了舊版的電腦，要先用舊版的程式移除驅動，再安裝新版。
