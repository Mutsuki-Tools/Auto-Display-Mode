# 領域文件

說明各 engineering skill 在探索程式碼時，應如何使用本專案的領域文件。

## 探索之前，先讀這些

- 專案根目錄的 **`GLOSSARY.md`**，或
- 若根目錄有 **`GLOSSARY-MAP.md`**：它會指向每個情境（context）各自的 `GLOSSARY.md`，閱讀與主題相關的那幾份。
- **`docs/adr/`**：閱讀與你即將處理的區域相關的 ADR。多情境專案中，也要查看 `src/<context>/docs/adr/` 裡屬於該情境的決策。

上述檔案若不存在，**直接繼續，不必提起**。不要指出它們不存在，也不要建議預先建立。`/domain-modeling` skill（經由 `/grill-with-docs` 與 `/improve-codebase-architecture` 觸發）會在名詞或決策真正確定時才順帶建立。

## 檔案結構

單一情境專案（大多數專案）：

```
/
├── GLOSSARY.md
├── docs/adr/
│   ├── 0001-event-sourced-orders.md
│   └── 0002-postgres-for-write-model.md
└── src/
```

多情境專案（根目錄有 `GLOSSARY-MAP.md`）：

```
/
├── GLOSSARY-MAP.md
├── docs/adr/                          ← 全系統層級的決策
└── src/
    ├── ordering/
    │   ├── GLOSSARY.md
    │   └── docs/adr/                  ← 該情境專屬的決策
    └── billing/
        ├── GLOSSARY.md
        └── docs/adr/
```

## 使用詞彙表的用語

當你的產出提到某個領域概念（issue 標題、重構提案、假設、測試名稱）時，使用 `GLOSSARY.md` 中定義的名詞。不要改用詞彙表明確避免的同義詞。

若需要的概念還不在詞彙表中，這是一個訊號：要嘛你在創造專案沒在用的說法（請重新考慮），要嘛確實有缺口（記下來交給 `/domain-modeling`）。

## 標示與 ADR 的衝突

若你的產出與既有 ADR 相牴觸，要明確指出，而不是默默推翻：

> _與 ADR-0007（event-sourced orders）相牴觸，但值得重新討論，因為……_
