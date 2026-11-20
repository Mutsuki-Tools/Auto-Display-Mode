using System;

namespace AutoDisplayMode.Core
{
    /// <summary>模式。此階段只有固定的兩個：遊玩、省電。</summary>
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
        public string BatPath { get; set; } = @"C:\multimonitortool-x64\monitormode.bat";
        public string HomeArgs { get; set; } = "home";
        public string AwayArgs { get; set; } = "away";

        /// <summary>自動切換。</summary>
        public bool AutoSwitch { get; set; } = true;
        public int IdleMinutes { get; set; } = 10;
        public Mode LastMode { get; set; } = Mode.Home;

        public bool AudioDetect { get; set; } = true;
        public int AudioSeconds { get; set; } = 3;
        public double AudioThreshold { get; set; } = 0.005;
        public int MouseMoveThreshold { get; set; } = 10;
        public bool IgnoreRemoteInput { get; set; } = true;
        public bool ShowNotifications { get; set; } = true;

        /// <summary>切換到 <paramref name="mode"/> 時傳給 bat 的參數。</summary>
        public string ArgsFor(Mode mode) => mode == Mode.Home ? HomeArgs : AwayArgs;

        internal static string ModeKey(Mode mode) => mode == Mode.Home ? "home" : "away";

        internal static Mode ParseMode(string s) =>
            string.Equals(s.Trim(), "away", StringComparison.OrdinalIgnoreCase) ? Mode.Away : Mode.Home;
    }
}
