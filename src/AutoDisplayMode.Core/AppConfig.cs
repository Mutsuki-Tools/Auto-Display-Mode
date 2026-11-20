using System;
using System.Collections.Generic;

namespace AutoDisplayMode.Core
{
    /// <summary>舊版的兩個固定模式：遊玩、省電。此階段切換仍以它們透過 bat 進行，之後的版本移除。</summary>
    public enum Mode
    {
        /// <summary>遊玩模式（cfg 中為 home）。</summary>
        Home,

        /// <summary>省電模式（cfg 中為 away）。</summary>
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
        public ModeConfig? CurrentModeConfig => Modes.Find(m => m.Id == CurrentModeId);

        // ---- 舊版切換（過渡用：此階段切換仍透過 bat，之後的版本移除）----

        public string BatPath { get; set; } = @"C:\multimonitortool-x64\monitormode.bat";
        public string HomeArgs { get; set; } = "home";
        public string AwayArgs { get; set; } = "away";
        public int IdleMinutes { get; set; } = 10;
        public bool AudioDetect { get; set; } = true;
        public Mode LastMode { get; set; } = Mode.Home;

        /// <summary>切換到 <paramref name="mode"/> 時傳給 bat 的參數。</summary>
        public string ArgsFor(Mode mode) => mode == Mode.Home ? HomeArgs : AwayArgs;

        internal static string ModeKey(Mode mode) => mode == Mode.Home ? "home" : "away";

        internal static Mode ParseMode(string s) =>
            string.Equals(s.Trim(), "away", StringComparison.OrdinalIgnoreCase) ? Mode.Away : Mode.Home;
    }
}
