using System;

namespace AutoDisplayMode.Core
{
    /// <summary>
    /// 應用程式協調者：核心對外的單一入口，也是唯一的測試接縫。
    /// 負責閒置判斷、聲音持續判斷、緩衝期、手動切換與 cfg 讀寫；
    /// 透過 Ports.cs 中的介面與 Windows 轉接層互動。
    /// </summary>
    /// <remarks>所有方法都必須在同一個（UI）執行緒上呼叫。</remarks>
    public sealed class AppCoordinator
    {
        /// <summary>啟動後的緩衝期。</summary>
        const long StartupGraceMs = 5_000;
        /// <summary>切換執行中的緩衝期上限（正常情況會在切換完成時改為 <see cref="AfterSwitchGraceMs"/>）。</summary>
        const long SwitchingGraceMs = 120_000;
        /// <summary>切換完成後的緩衝期：螢幕重新排列時游標可能被系統移動。</summary>
        const long AfterSwitchGraceMs = 10_000;
        /// <summary>無法切換（找不到 bat）時暫停自動切換的時間。</summary>
        const long NotReadyPauseMs = 60_000;
        /// <summary>手動開關自動切換後的緩衝期。</summary>
        const long ToggleGraceMs = 3_000;

        readonly IConfigStore store;
        readonly IDisplaySwitcher switcher;
        readonly IUsageSignals signals;
        readonly IClock clock;
        readonly IStartupRegistration startup;
        readonly INotifier notifier;

        AppConfig config;
        long lastUsageMs;     // 最後一次偵測到使用的時間
        long ignoreUntilMs;   // 在這個時間之前不做自動切換（切換螢幕後的緩衝）
        int audioStreak;      // 連續偵測到聲音的秒數
        uint lastInputTick;   // 遊玩模式用：Windows 最後輸入時間

        public AppCoordinator(IConfigStore store, IDisplaySwitcher switcher, IUsageSignals signals,
            IClock clock, IStartupRegistration startup, INotifier notifier)
        {
            this.store = store;
            this.switcher = switcher;
            this.signals = signals;
            this.clock = clock;
            this.startup = startup;
            this.notifier = notifier;

            config = LoadConfig();
            CurrentMode = config.LastMode;
        }

        /// <summary>狀態改變（介面需要更新）時觸發。</summary>
        public event Action? Changed;

        public Mode CurrentMode { get; private set; }

        /// <summary>螢幕切換執行中。</summary>
        public bool IsSwitching { get; private set; }

        /// <summary>目前的設定。請勿直接修改，改用協調者的方法。</summary>
        public AppConfig Config => config;

        public string ConfigLocation => store.Location;

        public bool StartupEnabled => startup.IsEnabled;

        /// <summary>目前狀態的摘要文字（通知用）。</summary>
        public string StatusSummary =>
            "目前：" + ModeName(CurrentMode) + "｜自動模式：" + OnOff(config.AutoSwitch) + "\n" +
            "閒置 " + config.IdleMinutes + " 分鐘切換省電｜聲音偵測：" + OnOff(config.AudioDetect);

        public static string ModeName(Mode mode) => mode == Mode.Home ? "遊玩模式" : "省電模式";

        static string OnOff(bool b) => b ? "開" : "關";

        // ------------------------------------------------------------
        //  啟動與每秒檢查
        // ------------------------------------------------------------

        /// <summary>程式啟動（訊息迴圈開始後）呼叫一次。</summary>
        public void Start()
        {
            long now = clock.NowMs;
            lastUsageMs = now;
            ignoreUntilMs = now + StartupGraceMs;
            lastInputTick = signals.LastInputTick;
            notifier.Notify("省電螢幕工具已啟動。\n" + StatusSummary, NotificationKind.Info);
        }

        /// <summary>每秒呼叫一次。</summary>
        public void Tick()
        {
            long now = clock.NowMs;

            // 聲音：連續超過 AudioSeconds 秒才算
            bool sound = config.AudioDetect && signals.ReadAudioPeak() > config.AudioThreshold;
            audioStreak = sound ? audioStreak + 1 : 0;
            bool usage = audioStreak > config.AudioSeconds;

            bool mouse = signals.TakeMouseUsage();
            if (CurrentMode == Mode.Away)
            {
                // 省電模式：只看實體滑鼠（低階滑鼠鉤子，可分辨遠端軟體送出的輸入）
                if (mouse) usage = true;
            }
            else
            {
                // 遊玩模式：看 Windows 最後輸入時間（不掛鉤子，不影響遊戲滑鼠延遲）
                uint t = signals.LastInputTick;
                if (t != lastInputTick)
                {
                    lastInputTick = t;
                    usage = true;
                }
            }
            if (usage) lastUsageMs = now;

            ApplyHookPolicy();

            if (!config.AutoSwitch || IsSwitching || now < ignoreUntilMs) return;

            if (CurrentMode == Mode.Away)
            {
                if (usage) SwitchTo(Mode.Home, "偵測到使用中，已自動切換成遊玩模式。");
            }
            else if (now - lastUsageMs >= config.IdleMinutes * 60_000L)
            {
                SwitchTo(Mode.Away, "閒置 " + config.IdleMinutes + " 分鐘，已自動切換成省電模式。");
            }
        }

        // 只在「自動切換 + 省電模式」時掛滑鼠鉤子，遊玩模式完全不掛
        void ApplyHookPolicy()
        {
            bool need = config.AutoSwitch && CurrentMode == Mode.Away && !IsSwitching;
            signals.ApplyHookPolicy(new MouseHookPolicy(need, ignoreUntilMs, config.MouseMoveThreshold, config.IgnoreRemoteInput));
        }

        // ------------------------------------------------------------
        //  切換模式
        // ------------------------------------------------------------

        void SwitchTo(Mode target, string? notifyText)
        {
            long now = clock.NowMs;

            if (!switcher.IsReady(config))
            {
                ignoreUntilMs = now + NotReadyPauseMs;
                ApplyHookPolicy();
                notifier.Notify("找不到 bat 檔：\n" + config.BatPath + "\n請關閉程式後修改 AutoDisplayMode.cfg 的 BatPath。",
                    NotificationKind.Warning);
                return;
            }

            CurrentMode = target;
            config.LastMode = target;
            SaveKeys("LastMode");

            IsSwitching = true;
            signals.ResetMouseTracking();
            audioStreak = 0;
            lastUsageMs = now;
            ignoreUntilMs = now + SwitchingGraceMs;
            ApplyHookPolicy();
            OnChanged();

            if (notifyText != null && config.ShowNotifications)
                notifier.Notify(notifyText, NotificationKind.Info);

            switcher.Begin(target, config);
        }

        /// <summary>轉接層在切換結束時呼叫；<paramref name="error"/> 為 null 表示成功。</summary>
        public void OnSwitchFinished(string? error)
        {
            long now = clock.NowMs;
            IsSwitching = false;
            ignoreUntilMs = now + AfterSwitchGraceMs;
            ResetUsageTracking(now);
            signals.RequestAudioRefresh();
            ApplyHookPolicy();
            OnChanged();

            if (error != null) notifier.Notify(error, NotificationKind.Error);
        }

        /// <summary>手動切換到 <paramref name="target"/>，並把自動切換設為 <paramref name="autoSwitch"/>。</summary>
        public void ManualSwitch(Mode target, bool autoSwitch)
        {
            if (IsSwitching)
            {
                notifier.Notify("正在切換中，請稍候。", NotificationKind.Info);
                return;
            }
            config.AutoSwitch = autoSwitch;
            SaveKeys("AutoMode");
            SwitchTo(target, null);
            OnChanged();
        }

        public void SetAutoSwitch(bool on)
        {
            long now = clock.NowMs;
            config.AutoSwitch = on;
            SaveKeys("AutoMode");
            ignoreUntilMs = Math.Max(ignoreUntilMs, now + ToggleGraceMs);
            ResetUsageTracking(now);
            ApplyHookPolicy();
            OnChanged();
        }

        public void SetIdleMinutes(int minutes)
        {
            config.IdleMinutes = minutes;
            SaveKeys("IdleMinutes");
            OnChanged();
        }

        public void SetAudioDetect(bool on)
        {
            config.AudioDetect = on;
            SaveKeys("AudioDetect");
            audioStreak = 0;
            OnChanged();
        }

        void ResetUsageTracking(long now)
        {
            lastUsageMs = now;
            signals.ResetMouseTracking();
            audioStreak = 0;
            lastInputTick = signals.LastInputTick;
        }

        // ------------------------------------------------------------
        //  開機啟動
        // ------------------------------------------------------------

        public void ToggleStartup()
        {
            try
            {
                startup.SetEnabled(!startup.IsEnabled);
            }
            catch (Exception ex)
            {
                notifier.Notify("無法修改開機啟動設定：" + ex.Message, NotificationKind.Error);
            }
            OnChanged();
        }

        // ------------------------------------------------------------
        //  設定檔
        // ------------------------------------------------------------

        /// <summary>重新讀取 cfg。目前實際的螢幕狀態以程式記錄為準，不因重新載入而切換。</summary>
        public void ReloadConfig()
        {
            config = LoadConfig();
            config.LastMode = CurrentMode;

            ResetUsageTracking(clock.NowMs);
            signals.RequestAudioRefresh();
            ApplyHookPolicy();
            OnChanged();

            string msg = "已重新載入設定檔。\n" + StatusSummary;
            if (switcher.IsReady(config))
            {
                notifier.Notify(msg, NotificationKind.Info);
            }
            else
            {
                notifier.Notify(msg + "\n注意：找不到 bat 檔，請檢查 BatPath。", NotificationKind.Warning);
            }
        }

        /// <summary>使用者要以文字編輯器開啟 cfg 前呼叫：cfg 不存在就先以目前設定建立。</summary>
        public void EnsureConfigFileExists()
        {
            if (TryRead(out string? text) && text == null) SaveAll(config);
        }

        AppConfig LoadConfig()
        {
            if (!TryRead(out string? text)) return new AppConfig();   // 讀取失敗就用預設值
            if (text == null)
            {
                var defaults = new AppConfig();
                SaveAll(defaults);
                return defaults;
            }
            return ConfigFile.Parse(text);
        }

        bool TryRead(out string? text)
        {
            try
            {
                text = store.ReadText();
                return true;
            }
            catch
            {
                text = null;
                return false;
            }
        }

        void SaveAll(AppConfig c) => TryWrite(ConfigFile.Format(c));

        // 只更新指定的項目，cfg 其他內容保持不變
        void SaveKeys(params string[] keys)
        {
            if (!TryRead(out string? text)) return;
            if (text == null) SaveAll(config);
            else TryWrite(ConfigFile.UpdateKeys(text, config, keys));
        }

        void TryWrite(string text)
        {
            try
            {
                store.WriteText(text);
            }
            catch
            {
                // 資料夾沒有寫入權限等情況，忽略
            }
        }

        void OnChanged() => Changed?.Invoke();
    }
}
