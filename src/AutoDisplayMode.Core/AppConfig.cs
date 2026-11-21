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

        /// <summary>目前模式的 ID。</summary>
        public int CurrentModeId { get; set; }

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

        /// <summary>新模式的 ID：目前最大的 ID + 1（沒有模式時為 1）。</summary>
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
        /// 複製模式 <paramref name="modeId"/>：新的 ID、名稱與未使用的顏色，其餘設定與螢幕設定相同（清單各自獨立）。
        /// 切換條件不複製。回傳新模式。
        /// </summary>
        public ModeConfig DuplicateMode(int modeId)
        {
            ModeConfig source = FindMode(modeId) ?? throw new ArgumentException("模式不存在", nameof(modeId));
            ModeConfig copy = source.CopyAs(NextModeId(), UniqueModeName(source.Name.Trim() + " 的複本"));
            copy.Color = ModeConfig.NoColor;
            Modes.Add(copy);
            ModeConfig.AssignMissingColors(Modes);
            return copy;
        }

        /// <summary>不與現有模式重複的名稱：<paramref name="baseName"/>、「baseName 2」、「baseName 3」…</summary>
        public string UniqueModeName(string baseName)
        {
            string name = baseName;
            for (int n = 2; ValidateModeName(0, name) != null; n++) name = baseName + " " + n;
            return name;
        }

        /// <summary>來源模式或目標模式是 <paramref name="modeId"/> 的切換條件（刪除該模式時會一併刪除）。</summary>
        public List<SwitchCondition> ConditionsReferencing(int modeId) =>
            SwitchConditions.FindAll(s => s.FromModeId == modeId || s.ToModeId == modeId);

        /// <summary>
        /// 模式 <paramref name="modeId"/> 是否可以刪除：最後一個模式與目前模式（<paramref name="currentModeId"/>）不可刪除。
        /// 可以刪除時回傳 null，否則回傳原因。
        /// </summary>
        public string? ModeRemovalBlocker(int modeId, int currentModeId)
        {
            if (Modes.Count <= 1) return LastModeText;
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
        const string CurrentModeText = "不能刪除目前模式";

        /// <summary>刪除模式，並一併刪除來源模式或目標模式是它的切換條件；回傳被刪除的切換條件。</summary>
        public List<SwitchCondition> RemoveMode(int modeId)
        {
            List<SwitchCondition> removed = ConditionsReferencing(modeId);
            Modes.RemoveAll(m => m.Id == modeId);
            SwitchConditions.RemoveAll(removed.Contains);
            return removed;
        }

        // ---- 切換條件 ----

        /// <summary>所有切換條件（依表格順序：同一來源模式的條件由上往下檢查，第一條成立的就執行）。</summary>
        public List<SwitchCondition> SwitchConditions { get; } = new();

        /// <summary>是否可以新增切換條件：至少要有兩個模式。可以新增時回傳 null，否則回傳原因。</summary>
        public string? SwitchConditionAddBlocker() =>
            Modes.Count < 2 ? "至少要有兩個模式才能新增切換條件（來源模式與目標模式不能相同）" : null;

        /// <summary>
        /// 在最後新增一條來源模式為 <paramref name="fromModeId"/>（不存在時用第一個模式）、目標模式為另一個模式的切換條件，
        /// 條件為預設的閒置 10 分鐘（滑鼠或鍵盤）。回傳新的切換條件。先以 <see cref="SwitchConditionAddBlocker"/> 確認可以新增。
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
            if (FindMode(currentModeId) == null) return CurrentModeText + "。";
            return InvalidSwitchConditions().FirstOrDefault();
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
