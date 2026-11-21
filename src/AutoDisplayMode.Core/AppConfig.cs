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

        public int MouseMoveThreshold { get; set; } = 10;
        public double AudioThreshold { get; set; } = 0.005;
        public int AudioSeconds { get; set; } = 3;
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
    }
}
