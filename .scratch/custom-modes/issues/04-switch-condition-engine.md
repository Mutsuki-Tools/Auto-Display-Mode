# 04: 切換條件引擎

**What to build:** 自動切換改由 cfg 中的切換條件表驅動，取代舊的兩條固定規則。每條切換條件為「來源模式 + 條件 → 目標模式」，條件為「閒置 N 分鐘/小時」或「偵測到使用」，使用來源可複選「滑鼠或鍵盤」「聲音」。閒置：所選來源全部都沒有使用持續 N；「滑鼠或鍵盤」以 Windows 最後輸入時間判斷，不需鉤子。偵測到使用：所選來源任一出現使用即成立；含「滑鼠或鍵盤」時需要滑鼠與鍵盤鉤子（移動門檻、遠端注入過濾），只在目前模式是某條此類條件的來源模式時才安裝。聲音需持續達「聲音持續秒數」且超過音量門檻才算一次使用。只評估來源模式等於目前模式的條件，依表格順序第一條成立者執行；來源與目標不可相同。切換後所有計時歸零，切換期間不偵測，完成後 10 秒緩衝。自動切換時依設定跳出通知。

**Blocked by:** 03

**Status:** ready-for-agent

- [x] 在 cfg 手寫切換條件（例如遊玩閒置 5 分鐘 → 省電、省電偵測到使用 → 遊玩）並重新載入後，自動切換如預期運作
- [x] 舊的固定兩條規則已移除
- [x] 遊玩模式只設閒置條件時，不安裝任何滑鼠或鍵盤鉤子
- [x] 測試涵蓋：閒置分鐘/小時、多來源「全部沒有」語意、偵測到使用多來源「任一」語意、聲音持續秒數過濾、條件順序優先、串連切換、切換後計時歸零與緩衝期、鉤子需求的開關時機、遠端輸入過濾設定
- [x] 手動實測：遠端連線操作不會觸發「偵測到使用」；通知音不會打斷閒置計時

## Comments

### 2026-11-21 實作完成（待手動實測）

- 核心新增 `SwitchCondition.cs`：切換條件（`FromModeId`、`Kind`＝`Idle`／`UsageDetected`、`IdleAmount`＋`IdleUnit`＝`Minutes`／`Hours`、`Sources`＝`MouseKeyboard`｜`Audio` 可複選、`ToModeId`），以及驗證（`Validate`，回傳無效原因；票 07 的存檔驗證可沿用）與描述文字（`Describe`）。`AppConfig.SwitchConditions` 依表格順序存放。
- 協調者每秒：聲音峰值超過門檻且連續超過「聲音持續秒數」才算一次聲音使用；Windows 最後輸入時間改變算一次滑鼠或鍵盤使用（閒置用，不需鉤子）；鉤子回報的使用只給「偵測到使用」。接著只看來源模式等於目前模式的切換條件，依表格順序第一條成立的就切換。閒置＝所選來源的「最後使用時間」取最大值後持續 N；偵測到使用＝所選來源任一在這一秒出現使用。切換成功後所有閒置計時（滑鼠或鍵盤、聲音）歸零並有 10 秒緩衝；失敗與拒絕暫停 1 分鐘、閒置計時不歸零（沿用票 03）。
- 鉤子：只有「自動切換開＋沒有在切換＋目前模式是某條『偵測到使用＋滑鼠或鍵盤』切換條件的來源模式」時才安裝。port 改為 `IUsageSignals.TakeHookUsage`／`ResetHookTracking`／`ApplyHookPolicy(InputHookPolicy)`，一個需求同時控制低階滑鼠鉤子與低階鍵盤鉤子。Windows 轉接層把 `MouseHook` 改為通用的 `LowLevelHook`，新增鍵盤鉤子：只算按下（`WM_KEYDOWN`／`WM_SYSKEYDOWN`），忽略遠端輸入時濾掉 `LLKHF_INJECTED`／`LLKHF_LOWER_IL_INJECTED` 與遠端桌面工作階段（與滑鼠相同）。兩個鉤子一起每約 10 分鐘重新安裝。
- 「偵測到使用＋滑鼠或鍵盤」只看鉤子，不看 Windows 最後輸入時間，因為後者分不出遠端軟體送出的輸入；閒置仍看 Windows 最後輸入時間，所以遠端操作會打斷閒置計時（與舊版相同）。
- cfg 格式：每條切換條件一段 `[Switch.N]`，鍵為 `From`、`Condition`（`Idle`／`UsageDetected`）、`IdleTime`、`IdleUnit`（`Minutes`／`Hours`）、`MouseKeyboard`、`Audio`（1/0）、`To`；放在一般設定之後、模式之前，附中文註解與範例。讀取時依 N 由小到大排序（重複的 N 只認第一個），寫出時重新編號為 1、2、3…。「偵測到使用」不寫 `IdleTime`／`IdleUnit`。沒寫的鍵用預設值（閒置 10 分鐘、只選滑鼠或鍵盤）。
- **無效的切換條件**（來源或目標模式不存在、來源與目標相同、未選任何使用來源、閒置時間不是正整數、`Condition` 無法辨識）讀取時略過，並在啟動或重新載入的通知中逐條說明原因。這時不立即改寫 cfg，讓使用者修正後重新載入；但下次存檔時會消失。規格只寫了「來源與目標不可相同」，其餘規則對齊票 07 的存檔驗證。
- 啟動與重新載入的通知改為列出從目前模式出發的切換條件（沒有時顯示「沒有從「X」出發的切換條件」）。說明視窗列出全部切換條件與規則。

#### 移除過渡用的舊版切換條件

- 已移除：`AppConfig` 的 `Mode` 列舉、`IdleMinutes`、`AudioDetect`、`HomeModeId`、`AwayModeId`、`LegacyRoleOf`、`LegacyLastMode`、`LegacyRolesSpecified`、`HasObsoleteKeys`；`ConfigFile.ResolveLegacyRoles`；協調者的 `LegacySwitchActive`、`LegacySwitchSummary`、`SetIdleMinutes`、`SetAudioDetect`；工作列的「閒置切換的時間」「自動切換偵測聲音」選單與 `LegacySwitchText`。這兩項現在改在 cfg 的切換條件中設定（設定視窗留給票 07）。`AutoSwitchTests` 併入新的 `SwitchConditionTests`。
- `ConfigFile.Parse` 改為回傳 `ParseResult`（設定、是否需要立即改寫、讀取說明），取代 `AppConfig` 上的 `HasObsoleteKeys` 旗標。

#### 轉移決定：新格式 cfg 中的 `[LegacySwitch]`

- 規格只說「舊格式（無分段）cfg」捨棄 `IdleMinutes`／`AudioDetect`，沒寫票 03 新增的 `[LegacySwitch]` 怎麼處理。決定轉成兩條等效的切換條件，維持原本的行為：
  - 「HomeMode」閒置 `IdleMinutes` 分鐘（滑鼠或鍵盤、聲音）→「AwayMode」
  - 「AwayMode」偵測到使用（滑鼠或鍵盤、聲音）→「HomeMode」
  - `AudioDetect=0` 時兩條都不選聲音。`IdleMinutes` 小於 1 或沒寫時用 10（舊版的預設）。
- `HomeMode`／`AwayMode` 沒有指向兩個不同且存在的模式時（例如只設定其中一個），舊版的兩條規則本來就不作用，因此不轉換，只移除該分段並在通知中說明。
- 讀到 `[LegacySwitch]` 時，啟動（或重新載入）當下就改寫 cfg，理由同票 03 的修正：避免轉換結果只存在記憶體中。通知會顯示「已將舊版切換條件轉換為 N 條切換條件」。
- 該分段中票 02 留下的 `BatPath`、`HomeArgs`、`AwayArgs`、`LastMode` 一併捨棄。`LastMode` 不再轉成角色：票 02 的 cfg 只有一個模式，舊版的兩條規則本來就不作用。
- 舊格式（無分段）cfg 依規格捨棄 `IdleMinutes`、`AudioDetect`、`LastMode`，轉換後沒有切換條件。
- 使用者目前的 cfg（`HomeMode=1`、`AwayMode=2`、`IdleMinutes=5`、`AudioDetect=1`）會轉成：`[Switch.1]`「預設」閒置 5 分鐘（滑鼠或鍵盤、聲音）→「省電」；`[Switch.2]`「省電」偵測到使用（滑鼠或鍵盤、聲音）→「預設」。

#### 行為改變

- **「省電」中按鍵盤也會切回「預設」**：舊版的「省電」只看實體滑鼠（滑鼠鉤子），不看鍵盤。現在「偵測到使用＋滑鼠或鍵盤」同時安裝滑鼠與鍵盤鉤子，實體按鍵也算使用；遠端注入的按鍵在「忽略遠端輸入」開啟時仍會被濾掉。
- 聲音峰值每秒都會讀取（舊版在 `AudioDetect=0` 時不讀）。有沒有用到取決於切換條件是否選了聲音，對行為沒有影響。

#### 審查後的修正

- `Condition=` 寫了無法辨識的值時，原本會默默當成閒置，現在判為無效並說明原因。
- `CaptureTests` 的 cfg 範本移除殘留的 `[LegacySwitch]`。介面文字與測試名稱統一用「滑鼠或鍵盤」。
- 只屬判斷題、沒有改的：`Kind` 的判斷分散在多處、`IdleAmount`＋`IdleUnit` 可以包成一個型別、通知文字可以移到 `SwitchCondition` 上。等票 07 需要編輯切換條件時，再視情況整理。

102 個測試全部通過。

#### 手動實測步驟

1. 先從工作列關閉正在執行的舊版程式，再執行 `build.bat`，然後啟動新的 exe。啟動通知應包含「已將舊版切換條件轉換為 2 條切換條件」；cfg 應立即改寫：`[LegacySwitch]` 消失，出現 `[Switch.1]`（`From=1`、`Condition=Idle`、`IdleTime=5`、`IdleUnit=Minutes`、`MouseKeyboard=1`、`Audio=1`、`To=2`）與 `[Switch.2]`（`From=2`、`Condition=UsageDetected`、`MouseKeyboard=1`、`Audio=1`、`To=1`）。程式轉換出的 cfg 以獨立的 commit 提交。
2. 右鍵選單不再有「閒置切換的時間」「自動切換偵測聲音」；「說明」列出這兩條切換條件。
3. 手寫切換條件：從選單開啟 cfg，把 `[Switch.1]` 的 `IdleTime` 改成 `1` 並存檔，選「重新載入cfg檔」。通知應列出「「預設」閒置 1 分鐘（滑鼠或鍵盤、聲音）→「省電」」。在「預設」不碰滑鼠鍵盤、不播放聲音，1 分鐘後應自動切到「省電」，並跳出「閒置 1 分鐘，已自動切換到「省電」。」。
4. 通知音不打斷閒置計時：回到「預設」後，閒置期間觸發幾次短暫的系統通知音（例如在其他程式觸發提示音，每次不到 3 秒），仍應在閒置滿 1 分鐘時切到「省電」。另外持續播放影片或音樂時，不應切換。
5. 在「省電」動一下實體滑鼠，應切回「預設」；再閒置切到「省電」後，改按一下鍵盤（新行為），也應切回「預設」。
6. 遠端連線：處於「省電」時，用遠端連線軟體（例如 Parsec、AnyDesk、Chrome 遠端桌面）操作滑鼠與鍵盤，不應切回「預設」。接著把 cfg 的 `IgnoreRemoteInput` 改成 `0` 並重新載入，再從遠端操作，應切回「預設」。測完改回 `1`。
7. 把 `[Switch.1]` 的 `IdleTime` 改回 `5` 並重新載入。

### 2026-11-21 手動實測（全部通過）

- 手動實測步驟 1～7 全部通過：啟動時 `[LegacySwitch]` 立即轉換為 `[Switch.1]`、`[Switch.2]` 並改寫 cfg；選單已無閒置時間與聲音偵測項目；手寫修改切換條件並重新載入後，自動切換依新的條件運作；通知音不會打斷閒置計時；「省電」中動滑鼠或按鍵盤都會切回「預設」；忽略遠端輸入開啟時，遠端操作不會觸發「偵測到使用」，關閉後則會觸發。
- 程式轉換出的 cfg 已獨立提交。步驟 7 是手動把 `IdleTime` 改回 5，程式產生的註解還停在「1 分鐘」，提交時已一併更正（下次程式存檔也會自動重新產生）。
- 全部驗收項目已勾選。
