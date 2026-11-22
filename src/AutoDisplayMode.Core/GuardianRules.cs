namespace AutoDisplayMode.Core
{
    /// <summary>守護程式每次檢查主程式（約每 0.25 秒）的判斷結果。</summary>
    public enum GuardianVerdict
    {
        /// <summary>主程式正常：繼續監看。</summary>
        KeepWatching,

        /// <summary>主程式不再需要這個守護程式（經由切換離開潛行模式、正常結束，或已改由新的守護程式守護）：結束，不套用。</summary>
        Quit,

        /// <summary>主程式不是正常結束（閃退、被結束）：接手。</summary>
        MainExited,

        /// <summary>主程式的心跳逾時（卡住、被除錯器暫停）：接手。</summary>
        MainHung,
    }

    /// <summary>守護程式的判斷規則（ADR-0006 第 2 層）。</summary>
    public static class GuardianRules
    {
        /// <summary>
        /// 心跳多久沒更新就判斷主程式卡住。開關螢幕時主程式的 UI 執行緒會卡住 2.5～2.8 秒，再加上每秒一次的間隔；
        /// 主程式的虛擬螢幕心跳在 UI 執行緒卡住 5 秒後停止，驅動再約 3 秒移除虛擬螢幕，守護程式在那之前接手。
        /// </summary>
        public const long HungMs = 6_000;

        /// <summary>
        /// 主程式正常結束前會先結束守護（<see cref="IRecoveryGuard.Disarm"/>），所以呼叫端要先讀 <paramref name="mainExited"/>
        /// 再讀 <paramref name="armed"/>：看到主程式已結束時，正常結束的話一定也看得到守護已結束。
        /// </summary>
        /// <param name="armed">主程式仍要這個守護程式守護。</param>
        /// <param name="mainExited">主程式已經結束。</param>
        /// <param name="heartbeatAgeMs">距主程式最後一次心跳的毫秒數。</param>
        public static GuardianVerdict Decide(bool armed, bool mainExited, long heartbeatAgeMs) =>
            !armed ? GuardianVerdict.Quit
            : mainExited ? GuardianVerdict.MainExited
            : heartbeatAgeMs > HungMs ? GuardianVerdict.MainHung
            : GuardianVerdict.KeepWatching;
    }
}
