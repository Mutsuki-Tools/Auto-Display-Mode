using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoDisplayMode.Core
{
    /// <summary>設定（cfg 的內容）。</summary>
    public sealed class AppConfig
    {
        // ---- 一般設定 ----

        /// <summary>自動切換。</summary>
        public bool AutoSwitch { get; set; } = true;

        /// <summary>目前模式的模式編號。</summary>
        public int CurrentModeId { get; set; }

        /// <summary>
        /// 復原模式的模式編號：最近一次所在的一般模式。離開潛行模式若不是經由切換（例如程式啟動時、虛擬螢幕意外消失），
        /// 一律回到復原模式。不存在或不是一般模式時改用順序第一個一般模式（見 <see cref="RestoreTarget"/>）。
        /// </summary>
        public int RestoreModeId { get; set; }

        // 一般設定的範圍：設定視窗的欄位與讀取 cfg 時的正規化共用
        public const int MinMouseMoveThreshold = 1, MaxMouseMoveThreshold = 1000;
        public const double MinAudioThreshold = 0, MaxAudioThreshold = 1, DefaultAudioThreshold = 0.005;
        public const int MinAudioSeconds = 1, MaxAudioSeconds = 3600, DefaultAudioSeconds = 3;

        /// <summary>滑鼠移動門檻：滑鼠要移動超過幾個像素才算使用。</summary>
        public int MouseMoveThreshold { get; set; } = 10;

        /// <summary>聲音音量門檻：聲音峰值（0~1）超過多少才算有聲音。</summary>
        public double AudioThreshold { get; set; } = DefaultAudioThreshold;

        /// <summary>聲音持續秒數：聲音要連續播放幾秒才算一次使用。</summary>
        public int AudioSeconds { get; set; } = DefaultAudioSeconds;
        public bool IgnoreRemoteInput { get; set; } = true;
        public bool ShowNotifications { get; set; } = true;

        /// <summary>開機登入時自動開啟。</summary>
        public bool StartWithWindows { get; set; }

        // ---- 模式 ----

        /// <summary>所有模式（依 cfg 中的順序）。</summary>
        public List<ModeConfig> Modes { get; } = new();

        /// <summary>目前模式；沒有任何模式時為 null。</summary>
        public ModeConfig? CurrentModeConfig => FindMode(CurrentModeId);

        public ModeConfig? FindMode(int id) => Modes.Find(m => m.Id == id);

        /// <summary>順序第一個一般模式；沒有一般模式時為 null。</summary>
        public ModeConfig? FirstNormalMode => Modes.Find(m => !m.IsRemote);

        /// <summary>復原模式：<see cref="RestoreModeId"/> 指向的一般模式，不存在時改用順序第一個一般模式；再沒有一般模式時為 null。</summary>
        public ModeConfig? RestoreTarget() =>
            FindMode(RestoreModeId) is ModeConfig m && !m.IsRemote ? m : FirstNormalMode;

        /// <summary>新模式的編號：目前最大的編號 + 1（沒有模式時為 1）。</summary>
        public int NextModeId() => Modes.Count == 0 ? 1 : Modes.Max(m => m.Id) + 1;

        /// <summary>新增一個沒有螢幕設定的模式（名稱不重複、配一個未使用的顏色），回傳新模式。</summary>
        public ModeConfig AddMode(string baseName = "新模式")
        {
            var mode = new ModeConfig(NextModeId(), UniqueModeName(baseName));
            Modes.Add(mode);
            ModeConfig.AssignMissingColors(Modes);
            return mode;
        }

        /// <summary>
        /// 新增一個潛行模式（名稱不重複、配一個未使用的顏色、虛擬螢幕 1920×1080@60），
        /// 並在切換條件清單最後加上它的內建切換條件（目標為順序第一個一般模式）。回傳新模式。
        /// </summary>
        public ModeConfig AddRemoteMode(string baseName = "潛行模式")
        {
            ModeConfig target = FirstNormalMode ?? throw new InvalidOperationException(LastNormalModeText);
            var mode = new ModeConfig(NextModeId(), UniqueModeName(baseName), ModeKind.Remote);
            Modes.Add(mode);
            ModeConfig.AssignMissingColors(Modes);
            SwitchConditions.Add(SwitchCondition.BuiltInFor(mode.Id, target.Id));
            return mode;
        }

        /// <summary>
        /// 複製模式 <paramref name="modeId"/>：新的編號、名稱與未使用的顏色，其餘設定與螢幕設定相同（清單各自獨立），種類也相同。
        /// 切換條件不複製；但複製潛行模式時，新的潛行模式在清單最後加上自己的內建切換條件（目標與原本的相同）。回傳新模式。
        /// </summary>
        public ModeConfig DuplicateMode(int modeId)
        {
            ModeConfig source = FindMode(modeId) ?? throw new ArgumentException("模式不存在", nameof(modeId));
            ModeConfig copy = source.CopyAs(NextModeId(), UniqueModeName(source.Name.Trim() + " 的複本"));
            copy.Color = ModeConfig.NoColor;
            Modes.Add(copy);
            ModeConfig.AssignMissingColors(Modes);
            if (copy.IsRemote)
            {
                int target = BuiltInConditionOf(source.Id) is SwitchCondition s && FindMode(s.ToModeId) is { IsRemote: false }
                    ? s.ToModeId
                    : FirstNormalMode?.Id ?? 0;
                SwitchConditions.Add(SwitchCondition.BuiltInFor(copy.Id, target));
            }
            return copy;
        }

        /// <summary>潛行模式 <paramref name="remoteModeId"/> 的內建切換條件；沒有時為 null。</summary>
        public SwitchCondition? BuiltInConditionOf(int remoteModeId) =>
            SwitchConditions.Find(s => s.BuiltIn && s.FromModeId == remoteModeId);

        /// <summary>不與現有模式重複的名稱：<paramref name="baseName"/>、「baseName 2」、「baseName 3」…</summary>
        public string UniqueModeName(string baseName)
        {
            string name = baseName;
            for (int n = 2; ValidateModeName(0, name) != null; n++) name = baseName + " " + n;
            return name;
        }

        /// <summary>
        /// 刪除模式 <paramref name="modeId"/> 時會一併刪除的切換條件：來源模式是它的，以及目標模式是它的（內建切換條件除外：
        /// 內建切換條件改指其他一般模式，見 <see cref="BuiltInConditionsRetargetedBy"/>）。
        /// </summary>
        public List<SwitchCondition> ConditionsReferencing(int modeId) =>
            SwitchConditions.FindAll(s => s.FromModeId == modeId || (s.ToModeId == modeId && !s.BuiltIn));

        /// <summary>刪除模式 <paramref name="modeId"/> 時，目標會改成順序第一個一般模式的內建切換條件。</summary>
        public List<SwitchCondition> BuiltInConditionsRetargetedBy(int modeId) =>
            SwitchConditions.FindAll(s => s.BuiltIn && s.ToModeId == modeId && s.FromModeId != modeId);

        /// <summary>
        /// 模式 <paramref name="modeId"/> 是否可以刪除：最後一個模式、最後一個一般模式與目前模式（<paramref name="currentModeId"/>）不可刪除。
        /// 可以刪除時回傳 null，否則回傳原因。
        /// </summary>
        public string? ModeRemovalBlocker(int modeId, int currentModeId)
        {
            if (Modes.Count <= 1) return LastModeText;
            if (FindMode(modeId) is { IsRemote: false } && Modes.Count(m => !m.IsRemote) <= 1) return LastNormalModeText;
            if (modeId == currentModeId) return CurrentModeText;
            return null;
        }

        /// <summary>
        /// 檢查模式 <paramref name="modeId"/> 改名為 <paramref name="name"/> 是否有效：不可空白，也不可與其他模式重複
        /// （去掉前後空白、不分大小寫比較）。有效時回傳 null，否則回傳原因。
        /// </summary>
        public string? ValidateModeName(int modeId, string name)
        {
            string trimmed = name.Trim();
            if (trimmed.Length == 0) return "模式名稱不可空白";
            ModeConfig? other = Modes.Find(m => m.Id != modeId && string.Equals(m.Name.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
            return other == null ? null : "已經有名為「" + other.Name.Trim() + "」的模式";
        }

        const string LastModeText = "至少要保留一個模式";
        const string LastNormalModeText = "至少要保留一個一般模式（內建切換條件與復原模式要有地方可回）";
        const string CurrentModeText = "不能刪除目前模式";

        /// <summary>
        /// 刪除模式，並一併刪除 <see cref="ConditionsReferencing"/> 的切換條件（包括它自己的內建切換條件）；
        /// 目標是它的內建切換條件改成順序第一個一般模式。回傳被刪除的切換條件。
        /// </summary>
        public List<SwitchCondition> RemoveMode(int modeId)
        {
            List<SwitchCondition> removed = ConditionsReferencing(modeId);
            Modes.RemoveAll(m => m.Id == modeId);
            SwitchConditions.RemoveAll(removed.Contains);
            int fallback = FirstNormalMode?.Id ?? 0;
            for (int i = 0; i < SwitchConditions.Count; i++)
                if (SwitchConditions[i].BuiltIn && SwitchConditions[i].ToModeId == modeId)
                    SwitchConditions[i] = SwitchConditions[i] with { ToModeId = fallback };
            return removed;
        }

        /// <summary>
        /// 把第 <paramref name="fromIndex"/> 個模式移到第 <paramref name="toIndex"/> 個位置（皆從 0 起算），改變模式的順序
        /// （工作列選單與 cfg 的順序）。編號在套用時才依新順序重新編排。
        /// </summary>
        public void MoveMode(int fromIndex, int toIndex)
        {
            ModeConfig m = Modes[fromIndex];
            Modes.RemoveAt(fromIndex);
            Modes.Insert(toIndex, m);
        }

        /// <summary>
        /// 依清單順序把模式重新編號為 1、2、3…（cfg 的 [Mode.N] 跟著順序），切換條件的來源與目標模式、目前模式跟著改。
        /// </summary>
        internal void RenumberModes()
        {
            var map = new Dictionary<int, int>();
            for (int i = 0; i < Modes.Count; i++) map[Modes[i].Id] = i + 1;
            int New(int id) => map.TryGetValue(id, out int n) ? n : id;

            foreach (ModeConfig m in Modes) m.Id = New(m.Id);
            for (int i = 0; i < SwitchConditions.Count; i++)
            {
                SwitchCondition s = SwitchConditions[i];
                SwitchConditions[i] = s with { FromModeId = New(s.FromModeId), ToModeId = New(s.ToModeId) };
            }
            CurrentModeId = New(CurrentModeId);
            RestoreModeId = New(RestoreModeId);
        }

        // ---- 切換條件 ----

        /// <summary>所有切換條件（依表格順序：同一來源模式的條件由上往下檢查，第一條成立的就執行）。</summary>
        public List<SwitchCondition> SwitchConditions { get; } = new();

        /// <summary>第 <paramref name="index"/> 條切換條件是否可以刪除：內建切換條件不能刪除。可以刪除時回傳 null，否則回傳原因。</summary>
        public string? SwitchConditionRemovalBlocker(int index) =>
            SwitchConditions[index].BuiltIn
                ? "內建切換條件不能刪除：它讓你回到電腦前一動鍵盤滑鼠就離開潛行模式。刪除潛行模式時會一起刪除"
                : null;

        /// <summary>
        /// 驗證並修正內建切換條件（讀取 cfg 時）：每個潛行模式剛好一條、來源模式是自己、條件與強制固定、目標是一般模式。
        /// 來源不是潛行模式或重複的刪除，鎖定的欄位改回固定值，目標失效時改成順序第一個一般模式，缺少的加在清單最後。
        /// 回傳修正的說明（每件事一句）；沒有一般模式時不修正（由呼叫端先補上一般模式）。
        /// </summary>
        public List<string> RepairBuiltInConditions()
        {
            var notes = new List<string>();
            ModeConfig? fallback = FirstNormalMode;
            if (fallback == null) return notes;
            string Name(int id) => "「" + (FindMode(id)?.Name ?? "模式 " + id) + "」";

            var seen = new HashSet<int>();
            for (int i = 0; i < SwitchConditions.Count; i++)
            {
                SwitchCondition s = SwitchConditions[i];
                if (!s.BuiltIn) continue;
                bool remote = FindMode(s.FromModeId) is { IsRemote: true };
                if (!remote || !seen.Add(s.FromModeId))
                {
                    notes.Add(remote
                        ? "已刪除" + Name(s.FromModeId) + "重複的內建切換條件。"
                        : "已刪除來源模式不是潛行模式的內建切換條件（來源模式 " + s.FromModeId + "）。");
                    SwitchConditions.RemoveAt(i--);
                    continue;
                }
                if (!s.HasBuiltInFields)
                {
                    notes.Add("已將" + Name(s.FromModeId) + "的內建切換條件改回固定的條件（偵測到使用、鍵盤滑鼠、強制）。");
                    s = SwitchCondition.BuiltInFor(s.FromModeId, s.ToModeId);
                }
                if (FindMode(s.ToModeId) is not { IsRemote: false })
                {
                    notes.Add(Name(s.FromModeId) + "的內建切換條件的目標模式不存在或不是一般模式，已改成" + Name(fallback.Id) + "。");
                    s = s with { ToModeId = fallback.Id };
                }
                SwitchConditions[i] = s;
            }
            foreach (ModeConfig m in Modes.Where(m => m.IsRemote && !seen.Contains(m.Id)).ToList())
            {
                SwitchConditions.Add(SwitchCondition.BuiltInFor(m.Id, fallback.Id));
                notes.Add("已為" + Name(m.Id) + "補上內建切換條件（目標為" + Name(fallback.Id) + "）。");
            }
            return notes;
        }

        /// <summary>是否可以新增切換條件：至少要有兩個模式。可以新增時回傳 null，否則回傳原因。</summary>
        public string? SwitchConditionAddBlocker() =>
            Modes.Count < 2 ? "至少要有兩個模式才能新增切換條件（來源模式與目標模式不能相同）" : null;

        /// <summary>
        /// 在最後新增一條來源模式為 <paramref name="fromModeId"/>（不存在時用第一個模式）、目標模式為另一個模式的切換條件，
        /// 條件為預設的閒置 10 分鐘（鍵盤滑鼠）。回傳新的切換條件。先以 <see cref="SwitchConditionAddBlocker"/> 確認可以新增。
        /// </summary>
        public SwitchCondition AddSwitchCondition(int fromModeId)
        {
            if (SwitchConditionAddBlocker() is string reason) throw new InvalidOperationException(reason);
            int from = FindMode(fromModeId)?.Id ?? Modes[0].Id;
            var s = new SwitchCondition { FromModeId = from, ToModeId = Modes.First(m => m.Id != from).Id };
            SwitchConditions.Add(s);
            return s;
        }

        /// <summary>把第 <paramref name="fromIndex"/> 條切換條件移到第 <paramref name="toIndex"/> 個位置（皆從 0 起算），改變評估順序。</summary>
        public void MoveSwitchCondition(int fromIndex, int toIndex)
        {
            SwitchCondition s = SwitchConditions[fromIndex];
            SwitchConditions.RemoveAt(fromIndex);
            SwitchConditions.Insert(toIndex, s);
        }

        /// <summary>
        /// 存檔前的檢查：一般設定在範圍內、目前模式（<paramref name="currentModeId"/>，以協調者記錄的為準）存在、
        /// 每條切換條件都有效。有效時回傳 null，否則回傳原因。
        /// </summary>
        public string? Validate(int currentModeId)
        {
            if (ValidateGeneral() is string general) return general + "。";
            if (Modes.Count == 0) return LastModeText + "。";
            foreach (ModeConfig m in Modes)
            {
                if (ValidateModeName(m.Id, m.Name) is string name) return name + "。";
            }
            if (FirstNormalMode == null) return LastNormalModeText + "。";
            if (FindMode(currentModeId) == null) return CurrentModeText + "。";
            foreach (ModeConfig m in Modes)
            {
                if (MonitorLayout.Check(m).Errors.FirstOrDefault() is string layout)
                    return "「" + m.Name.Trim() + "」的螢幕配置有誤：" + layout + "。";
            }
            if (InvalidSwitchConditions().FirstOrDefault() is string condition) return condition;
            foreach (ModeConfig m in Modes.Where(m => m.IsRemote))
            {
                if (SwitchConditions.Count(s => s.BuiltIn && s.FromModeId == m.Id) != 1)
                    return "潛行模式「" + m.Name.Trim() + "」必須剛好有一條內建切換條件。";
            }
            return null;
        }

        /// <summary>每條無效的切換條件與原因，例如「第 2 條切換條件無效：來源模式與目標模式相同。」（依表格順序）。</summary>
        public IEnumerable<string> InvalidSwitchConditions()
        {
            for (int i = 0; i < SwitchConditions.Count; i++)
            {
                if (SwitchConditions[i].Validate(this) is string reason)
                    yield return "第 " + (i + 1) + " 條切換條件無效：" + reason + "。";
            }
        }

        /// <summary>檢查一般設定是否在範圍內；有效時回傳 null，否則回傳原因。</summary>
        public string? ValidateGeneral()
        {
            if (MouseMoveThreshold < MinMouseMoveThreshold || MouseMoveThreshold > MaxMouseMoveThreshold)
                return "滑鼠移動門檻必須介於 " + MinMouseMoveThreshold + "～" + MaxMouseMoveThreshold + " 像素";
            if (!(AudioThreshold >= MinAudioThreshold && AudioThreshold <= MaxAudioThreshold))   // 也擋下 NaN
                return "聲音音量門檻必須介於 " + MinAudioThreshold + "～" + MaxAudioThreshold;
            if (AudioSeconds < MinAudioSeconds || AudioSeconds > MaxAudioSeconds)
                return "聲音持續秒數必須介於 " + MinAudioSeconds + "～" + MaxAudioSeconds + " 秒";
            return null;
        }

        /// <summary>深複製：修改複本（包含模式與螢幕設定清單）不會影響原本的設定。設定視窗以複本暫存修改。</summary>
        public AppConfig Clone()
        {
            var c = new AppConfig
            {
                AutoSwitch = AutoSwitch,
                CurrentModeId = CurrentModeId,
                RestoreModeId = RestoreModeId,
                MouseMoveThreshold = MouseMoveThreshold,
                AudioThreshold = AudioThreshold,
                AudioSeconds = AudioSeconds,
                IgnoreRemoteInput = IgnoreRemoteInput,
                ShowNotifications = ShowNotifications,
                StartWithWindows = StartWithWindows,
            };
            foreach (ModeConfig m in Modes) c.Modes.Add(m.Clone());
            c.SwitchConditions.AddRange(SwitchConditions);   // record，不可變
            return c;
        }
    }
}
