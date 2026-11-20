using System;
using System.Collections.Generic;

namespace AutoDisplayMode.Core
{
    /// <summary>
    /// 舊版的兩個固定角色：遊玩、省電。過渡期間舊版的兩條切換條件以它們指向模式
    /// （<see cref="AppConfig.HomeModeId"/>、<see cref="AppConfig.AwayModeId"/>），之後的版本改由切換條件取代並移除。
    /// </summary>
    public enum Mode
    {
        /// <summary>遊玩（cfg 中為 home）。</summary>
        Home,

        /// <summary>省電（cfg 中為 away）。</summary>
        Away,
    }

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

        // ---- 舊版切換條件（過渡用：之後的版本改由切換條件取代並移除）----

        /// <summary>遊玩模式閒置幾分鐘後切換到省電模式。</summary>
        public int IdleMinutes { get; set; } = 10;

        /// <summary>省電模式中持續播放聲音算不算使用；遊玩模式中持續播放聲音會不會打斷閒置計時。</summary>
        public bool AudioDetect { get; set; } = true;

        /// <summary>舊版切換條件中扮演「遊玩」的模式 ID；0 表示沒有。</summary>
        public int HomeModeId { get; set; }

        /// <summary>舊版切換條件中扮演「省電」的模式 ID；0 表示沒有。</summary>
        public int AwayModeId { get; set; }

        /// <summary>模式 <paramref name="modeId"/> 在舊版切換條件中扮演的角色；都不是時為 null。</summary>
        public Mode? LegacyRoleOf(int modeId) =>
            modeId == 0 ? null : modeId == HomeModeId ? Mode.Home : modeId == AwayModeId ? Mode.Away : null;

        /// <summary>讀取時 cfg 中是否有 HomeMode／AwayMode 鍵（沒有時以 <see cref="LegacyLastMode"/> 轉移）。</summary>
        internal bool LegacyRolesSpecified { get; set; }

        /// <summary>讀取時 cfg 中的舊鍵 LastMode（遊玩/省電）；只用來轉移成 HomeMode／AwayMode，不再寫出。</summary>
        internal Mode? LegacyLastMode { get; set; }

        internal static Mode ParseMode(string s) =>
            string.Equals(s.Trim(), "away", StringComparison.OrdinalIgnoreCase) ? Mode.Away : Mode.Home;
    }
}
