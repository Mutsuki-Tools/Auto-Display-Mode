# Issue 追蹤：本地 Markdown

本專案的 issue 與 spec 以 Markdown 檔存放在 `.scratch/`。

## 慣例

- 一個功能一個資料夾：`.scratch/<feature-slug>/`
- Spec 是 `.scratch/<feature-slug>/spec.md`
- 實作 issue 一張票一個檔案，放在 `.scratch/<feature-slug>/issues/<NN>-<slug>.md`，從 `01` 開始編號；不要把所有票合併成單一檔案
- Triage 狀態以檔案開頭附近的 `Status:` 行記錄（角色字串見 `triage-labels.md`）
- 留言與討論紀錄附加在檔案底部的 `## Comments` 標題下

## 當 skill 說「發布到 issue tracker」

在 `.scratch/<feature-slug>/` 下建立新檔案（資料夾不存在就先建立）。

## 當 skill 說「取得相關的票」

讀取指定路徑的檔案。使用者通常會直接提供路徑或 issue 編號。

## Wayfinding 操作

供 `/wayfinder` 使用。**Map** 是一個檔案，每張票各有一個 **child** 檔案。

- **Map**：`.scratch/<effort>/map.md`（內容包含 Notes / Decisions-so-far / Fog）。
- **Child ticket**：`.scratch/<effort>/issues/NN-<slug>.md`，從 `01` 開始編號，內文寫問題。`Type:` 行記錄票的類型（`research`/`prototype`/`grilling`/`task`）；`Status:` 行記錄 `claimed`/`resolved`。
- **Blocking**：檔案開頭附近寫 `Blocked by: NN, NN`。所列的每個檔案都是 `resolved` 時，該票即解除封鎖。
- **Frontier**：掃描 `.scratch/<effort>/issues/`，找出尚未完成、未被封鎖、未被認領的檔案；編號最小者優先。
- **Claim**：開始任何工作之前，先設為 `Status: claimed` 並存檔。
- **Resolve**：在 `## Answer` 標題下附上答案，設為 `Status: resolved`，再於 `map.md` 的 Decisions-so-far 附加一則脈絡指引（重點摘要 + 連結）。
