using System;
using System.Collections.Generic;
using System.Linq;

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
        /// <summary>首次啟動時以目前螢幕狀態建立的模式名稱。</summary>
        const string DefaultModeName = "預設";

        readonly IConfigStore store;
        readonly IDisplaySwitcher switcher;
        readonly IMonitorSystem monitors;
        readonly IUsageSignals signals;
        readonly IClock clock;
        readonly IStartupRegistration startup;
        readonly INotifier notifier;

        AppConfig config;
        long lastUsageMs;     // 最後一次偵測到使用的時間
        long ignoreUntilMs;   // 在這個時間之前不做自動切換（切換螢幕後的緩衝）
        int audioStreak;      // 連續偵測到聲音的秒數
        uint lastInputTick;   // 遊玩模式用：Windows 最後輸入時間
        bool writesBlocked;   // cfg 讀取或舊版備份失敗：不寫入 cfg，避免覆蓋原本的內容
        string loadNote = ""; // 載入 cfg 時發生的事（轉移、備份失敗等），附加在下一則通知中

        public AppCoordinator(IConfigStore store, IDisplaySwitcher switcher, IMonitorSystem monitors, IUsageSignals signals,
            IClock clock, IStartupRegistration startup, INotifier notifier)
        {
            this.store = store;
            this.switcher = switcher;
            this.monitors = monitors;
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
            notifier.Notify("省電螢幕工具已啟動。\n" + TakeLoadNote() + StatusSummary, NotificationKind.Info);
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
            Save();

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
            Save();
            SwitchTo(target, null);
            OnChanged();
        }

        public void SetAutoSwitch(bool on)
        {
            long now = clock.NowMs;
            config.AutoSwitch = on;
            Save();
            ignoreUntilMs = Math.Max(ignoreUntilMs, now + ToggleGraceMs);
            ResetUsageTracking(now);
            ApplyHookPolicy();
            OnChanged();
        }

        public void SetIdleMinutes(int minutes)
        {
            config.IdleMinutes = minutes;
            Save();
            OnChanged();
        }

        public void SetAudioDetect(bool on)
        {
            config.AudioDetect = on;
            Save();
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
            config.StartWithWindows = startup.IsEnabled;
            Save();
            OnChanged();
        }

        // ------------------------------------------------------------
        //  擷取
        // ------------------------------------------------------------

        /// <summary>目前模式的名稱。</summary>
        public string CurrentModeName => config.CurrentModeConfig?.Name ?? "";

        /// <summary>把目前實際的螢幕狀態擷取進目前模式並存檔（確認視窗由介面層負責）。</summary>
        public void CaptureToCurrentMode()
        {
            if (IsSwitching)
            {
                notifier.Notify("正在切換中，請稍候。", NotificationKind.Info);
                return;
            }

            IReadOnlyList<DetectedMonitor> detected;
            try
            {
                detected = monitors.Detect();
            }
            catch (Exception ex)
            {
                notifier.Notify("無法偵測螢幕：" + ex.Message, NotificationKind.Error);
                return;
            }

            ModeConfig mode = config.CurrentModeConfig ?? AddDefaultMode(config, Array.Empty<DetectedMonitor>());
            List<MonitorSettings> captured = Capture.Run(detected, mode.Monitors);
            mode.Monitors.Clear();
            mode.Monitors.AddRange(captured);
            config.CurrentModeId = mode.Id;
            ResetUsageTracking(clock.NowMs);

            if (Save()) notifier.Notify("已將目前螢幕狀態存入「" + mode.Name + "」。", NotificationKind.Info);
            else notifier.Notify("無法寫入設定檔：\n" + store.Location, NotificationKind.Error);
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

            string msg = "已重新載入設定檔。\n" + TakeLoadNote() + StatusSummary;
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
            if (TryRead(out string? text) && text == null) Save();
        }

        /// <summary>
        /// 讀取 cfg：
        /// 不存在（或空白）時以目前螢幕狀態建立「預設」模式並寫出；
        /// 舊格式時先備份為 .bak、轉移仍有意義的值、建立「預設」模式並寫出；
        /// 讀取失敗時使用預設值，且在下次成功讀取前不寫入 cfg（避免覆蓋讀不到的內容）。
        /// </summary>
        AppConfig LoadConfig()
        {
            writesBlocked = false;
            AppConfig c;

            if (!TryRead(out string? text))
            {
                writesBlocked = true;
                c = new AppConfig();
                AddDefaultMode(c, DetectForNewMode());
                return c;
            }

            if (text == null || text.Trim().Length == 0)
            {
                c = new AppConfig { StartWithWindows = startup.IsEnabled };
                AddDefaultMode(c, DetectForNewMode());
                Save(c);
                return c;
            }

            if (ConfigFile.IsLegacy(text))
            {
                try
                {
                    store.CreateBackup();
                    loadNote += "已將舊版設定檔轉換為新格式，原檔備份為 .bak。\n";
                }
                catch (Exception ex)
                {
                    // 沒有備份就不覆蓋舊檔：本次以轉移後的設定執行，但不寫入 cfg
                    writesBlocked = true;
                    loadNote += "無法備份舊版設定檔（" + ex.Message + "），本次不會改寫設定檔。\n";
                }
                c = ConfigFile.MigrateLegacy(text);
                c.StartWithWindows = startup.IsEnabled;   // 舊版的開機啟動狀態只存在登錄檔
                AddDefaultMode(c, DetectForNewMode());
                Save(c);
                return c;
            }

            c = ConfigFile.Parse(text);
            if (c.Modes.Count == 0)
            {
                AddDefaultMode(c, DetectForNewMode());
                Save(c);
            }
            return c;
        }

        IReadOnlyList<DetectedMonitor> DetectForNewMode()
        {
            try
            {
                return monitors.Detect();
            }
            catch (Exception ex)
            {
                loadNote += "無法偵測螢幕（" + ex.Message + "），「" + DefaultModeName + "」模式中沒有螢幕設定。\n";
                return Array.Empty<DetectedMonitor>();
            }
        }

        /// <summary>以 <paramref name="detected"/> 擷取出「預設」模式，加入設定並設為目前模式。</summary>
        static ModeConfig AddDefaultMode(AppConfig c, IReadOnlyList<DetectedMonitor> detected)
        {
            int id = c.Modes.Count == 0 ? 1 : c.Modes.Max(m => m.Id) + 1;
            var mode = new ModeConfig(id, DefaultModeName);
            mode.Monitors.AddRange(Capture.Run(detected, Array.Empty<MonitorSettings>()));
            c.Modes.Add(mode);
            c.CurrentModeId = id;
            return mode;
        }

        /// <summary>取出並清除載入 cfg 時留下的說明（附加在下一則啟動或重新載入的通知中）。</summary>
        string TakeLoadNote()
        {
            string note = loadNote;
            loadNote = "";
            return note;
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

        /// <summary>整份重寫 cfg。成功時回傳 true。</summary>
        bool Save() => Save(config);

        bool Save(AppConfig c)
        {
            if (writesBlocked) return false;
            try
            {
                store.WriteText(ConfigFile.Format(c));
                return true;
            }
            catch
            {
                // 資料夾沒有寫入權限等情況
                return false;
            }
        }

        void OnChanged() => Changed?.Invoke();
    }
}
