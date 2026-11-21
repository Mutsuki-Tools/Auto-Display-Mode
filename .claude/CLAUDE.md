# Auto Display Mode

## 語言

- 與使用者對話一律使用**台灣繁體中文**（台灣慣用詞彙，例如「程式」「檔案」「資料夾」「設定」，而非「程序」「文件」「文件夾」「配置」）。
- 所有新撰寫或更新的文件（README、spec、issue、ADR、GLOSSARY、`docs/` 下的檔案等）都以台灣繁體中文撰寫。程式碼識別字、指令與檔名維持原樣。

## 記憶

- 跨對話要記住的事（使用者偏好、回饋、專案背景）一律存在本專案的 `.claude/memory/`：一則一個檔案（沿用 name／description／metadata.type 的 frontmatter），並在 `.claude/memory/MEMORY.md` 加一行 `@檔名` 匯入。**不要**寫到 `~/.claude/projects/.../memory/`。
- 這個資料夾有進 git，新增或修改記憶後隨工作一起提交。
- 目前的記憶：@memory/MEMORY.md

## Agent skills

### Issue tracker

Issue 以本地 Markdown 檔記錄在 `.scratch/<feature>/`。詳見 `docs/agents/issue-tracker.md`。

### Triage labels

使用預設的五個標籤（needs-triage、needs-info、ready-for-agent、ready-for-human、wontfix）。詳見 `docs/agents/triage-labels.md`。

### Domain docs

單一情境（single-context）：專案根目錄一份 `GLOSSARY.md` 與 `docs/adr/`。詳見 `docs/agents/domain.md`。
