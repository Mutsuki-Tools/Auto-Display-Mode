---
name: handoff-prompts-short
description: 給下一張票的交接指令要精簡，不重複票的 Comments 已寫的內容；cfg 不必再獨立 commit
metadata:
  node_type: memory
  type: feedback
  originSessionId: 8bfb576a-ac8c-41c7-9c4e-b5d0e4293bf2
  modified: 2026-11-21T05:21:03.162Z
---

寫「下一張票的啟動指令＋交接事項」時要精簡：已經寫在票底部 Comments 的內容（資料流、高 DPI 的坑、設計決定）只要叫對方去讀，不要再貼一次；只列票裡沒寫、且跟這張票直接相關的事項。

**Why:** 使用者（2026-11-21，票 07 交接時）覺得交代越來越長。
**How to apply:** 交接事項控制在十幾行內；慣例只留仍有效的。

另外，使用者說「手動實測時程式改寫的 cfg 以獨立的 commit 提交」這條慣例不再需要，不要再放進交接或照做。
