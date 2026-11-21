using System.Collections.Generic;

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

        // ---- 切換條件 ----

        /// <summary>所有切換條件（依表格順序：同一來源模式的條件由上往下檢查，第一條成立的就執行）。</summary>
        public List<SwitchCondition> SwitchConditions { get; } = new();

        /// <summary>
        /// 存檔前的檢查：一般設定在範圍內、目前模式（<paramref name="currentModeId"/>，以協調者記錄的為準）存在、
        /// 每條切換條件都有效。有效時回傳 null，否則回傳原因。
        /// </summary>
        public string? Validate(int currentModeId)
        {
            if (ValidateGeneral() is string general) return general + "。";
            if (FindMode(currentModeId) == null) return "不能刪除目前模式。";
            for (int i = 0; i < SwitchConditions.Count; i++)
            {
                if (SwitchConditions[i].Validate(this) is string reason)
                    return "第 " + (i + 1) + " 條切換條件無效：" + reason + "。";
            }
            return null;
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
