using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoDisplayMode.Core
{
    /// <summary>
    /// 應用程式協調者：核心對外的單一入口，也是唯一的測試接縫。
    /// 負責依切換條件自動切換（閒置計時、聲音持續判斷、緩衝期、鉤子需求）、手動切換、套用模式與 cfg 讀寫；
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
        /// <summary>切換失敗（或被拒絕）後暫停自動切換的時間，避免每秒重試、一直跳通知。</summary>
        const long FailurePauseMs = 60_000;
        /// <summary>手動開關自動切換後的緩衝期。</summary>
        const long ToggleGraceMs = 3_000;
        /// <summary>首次啟動時以目前螢幕狀態建立的模式名稱。</summary>
        const string DefaultModeName = "預設";
        const string BusyText = "正在切換中，請稍候。";

        readonly IConfigStore store;
        readonly IMonitorSystem monitors;
        readonly IUsageSignals signals;
        readonly IClock clock;
        readonly IStartupRegistration startup;
        readonly INotifier notifier;

        AppConfig config;
        long lastInputUsageMs;   // 最後一次「滑鼠或鍵盤」使用的時間（閒置用：Windows 最後輸入時間改變）
        long lastAudioUsageMs;   // 最後一次「聲音」使用的時間（聲音持續達門檻秒數）
        long ignoreUntilMs;      // 在這個時間之前不做自動切換（切換螢幕後的緩衝、失敗後的暫停）
        int audioStreak;         // 連續偵測到聲音的秒數
        uint lastInputTick;      // 上次看到的 Windows 最後輸入時間
        bool writesBlocked;   // cfg 讀取或舊版備份失敗：不寫入 cfg，避免覆蓋原本的內容
        string loadNote = ""; // 載入 cfg 時發生的事（轉移、備份失敗等），附加在下一則通知中

        /// <summary>進行中的切換：目標模式、是否為手動切換、自動切換成功時要跳出的通知。</summary>
        sealed record PendingSwitch(int ModeId, string ModeName, bool Manual, string? NotifyText);

        PendingSwitch? pending;   // 未切換時為 null

        public AppCoordinator(IConfigStore store, IMonitorSystem monitors, IUsageSignals signals,
            IClock clock, IStartupRegistration startup, INotifier notifier)
        {
            this.store = store;
            this.monitors = monitors;
            this.signals = signals;
            this.clock = clock;
            this.startup = startup;
            this.notifier = notifier;

            config = LoadConfig();
        }

        /// <summary>狀態改變（介面需要更新）時觸發。</summary>
        public event Action? Changed;

        /// <summary>螢幕切換執行中。</summary>
        public bool IsSwitching => pending != null;

        /// <summary>切換中的目標模式 ID；未切換時為 0。</summary>
        public int SwitchTargetId => pending?.ModeId ?? 0;

        /// <summary>切換中的目標模式名稱；未切換時為 null。</summary>
        public string? SwitchTargetName => pending?.ModeName;

        /// <summary>目前的設定。請勿直接修改，改用協調者的方法。</summary>
        public AppConfig Config => config;

        public string ConfigLocation => store.Location;

        public bool StartupEnabled => startup.IsEnabled;

        /// <summary>目前模式的名稱。</summary>
        public string CurrentModeName => config.CurrentModeConfig?.Name ?? "";

        /// <summary>目前狀態的摘要文字（通知用）：目前模式、自動切換、從目前模式出發的切換條件。</summary>
        public string StatusSummary
        {
            get
            {
                string head = "目前：" + CurrentModeName + "｜自動切換：" + OnOff(config.AutoSwitch) + "\n";
                List<SwitchCondition> from = ConditionsFromCurrentMode().ToList();
                return from.Count == 0
                    ? head + "沒有從「" + CurrentModeName + "」出發的切換條件"
                    : head + string.Join("\n", from.Select(s => s.Describe(config)));
            }
        }

        static string OnOff(bool b) => b ? "開" : "關";

        /// <summary>來源模式等於目前模式的切換條件（依表格順序）。</summary>
        IEnumerable<SwitchCondition> ConditionsFromCurrentMode() =>
            config.SwitchConditions.Where(s => s.FromModeId == config.CurrentModeId);

        // ------------------------------------------------------------
        //  啟動與每秒檢查
        // ------------------------------------------------------------

        /// <summary>程式啟動（訊息迴圈開始後）呼叫一次。啟動時不套用任何模式。</summary>
        public void Start()
        {
            long now = clock.NowMs;
            ignoreUntilMs = now + StartupGraceMs;
            ResetUsageTracking(now);
            notifier.Notify("省電螢幕工具已啟動。\n" + TakeLoadNote() + StatusSummary, NotificationKind.Info);
        }

        /// <summary>
        /// 每秒呼叫一次：更新使用跡象，再依表格順序檢查來源模式等於目前模式的切換條件，第一條成立的就切換。
        /// </summary>
        public void Tick()
        {
            long now = clock.NowMs;

            // 聲音：峰值超過門檻且連續超過 AudioSeconds 秒才算一次使用（過濾通知音這類短暫聲音）
            bool sound = signals.ReadAudioPeak() > config.AudioThreshold;
            audioStreak = sound ? audioStreak + 1 : 0;
            bool audioUsage = audioStreak > config.AudioSeconds;
            if (audioUsage) lastAudioUsageMs = now;

            // 滑鼠或鍵盤（閒置用）：Windows 最後輸入時間，不需鉤子
            uint t = signals.LastInputTick;
            if (t != lastInputTick)
            {
                lastInputTick = t;
                lastInputUsageMs = now;
            }

            // 滑鼠或鍵盤（偵測到使用用）：鉤子（經移動門檻與遠端過濾），只在需要時安裝
            bool hookUsage = signals.TakeHookUsage();

            ApplyHookPolicy();

            if (!config.AutoSwitch || IsSwitching || now < ignoreUntilMs) return;

            foreach (SwitchCondition s in ConditionsFromCurrentMode())
            {
                ModeConfig? target = config.FindMode(s.ToModeId);
                if (target == null || !IsSatisfied(s, now, hookUsage, audioUsage)) continue;
                string text = s.Kind == SwitchConditionKind.Idle
                    ? s.DescribeCondition() + "，已自動切換到「" + target.Name + "」。"
                    : "偵測到使用，已自動切換到「" + target.Name + "」。";
                SwitchTo(target, manual: false, text);
                return;
            }
        }

        /// <summary>
        /// 閒置：所選的使用來源全部都沒有使用，持續指定的時間。
        /// 偵測到使用：所選的使用來源任一個在這一秒出現使用。
        /// </summary>
        bool IsSatisfied(SwitchCondition s, long now, bool hookUsage, bool audioUsage)
        {
            bool input = s.Uses(UsageSources.MouseKeyboard), audio = s.Uses(UsageSources.Audio);
            if (s.Kind == SwitchConditionKind.UsageDetected) return (input && hookUsage) || (audio && audioUsage);
            if (!input && !audio) return false;
            long lastUsage = Math.Max(input ? lastInputUsageMs : long.MinValue, audio ? lastAudioUsageMs : long.MinValue);
            return now - lastUsage >= s.IdleMs;
        }

        /// <summary>
        /// 只在「自動切換開 + 沒有在切換 + 目前模式是某條『偵測到使用 + 滑鼠或鍵盤』切換條件的來源模式」時安裝滑鼠與鍵盤鉤子，
        /// 其他情況完全不掛（不影響遊戲的輸入延遲）。
        /// </summary>
        void ApplyHookPolicy()
        {
            bool need = config.AutoSwitch && !IsSwitching && ConditionsFromCurrentMode().Any(s =>
                s.Kind == SwitchConditionKind.UsageDetected && s.Uses(UsageSources.MouseKeyboard));
            signals.ApplyHookPolicy(new InputHookPolicy(need, ignoreUntilMs, config.MouseMoveThreshold, config.IgnoreRemoteInput));
        }

        // ------------------------------------------------------------
        //  切換模式
        // ------------------------------------------------------------

        /// <summary>手動切換到模式 <paramref name="modeId"/>（可以是目前模式：重新套用）。</summary>
        public void ManualSwitch(int modeId)
        {
            if (IsSwitching)
            {
                notifier.Notify(BusyText, NotificationKind.Info);
                return;
            }
            ModeConfig? mode = config.FindMode(modeId);
            if (mode != null) SwitchTo(mode, manual: true, null);
        }

        /// <summary>
        /// 依目前接上的螢幕規劃並開始套用 <paramref name="target"/>。偵測失敗或規劃被拒絕時不切換、
        /// 通知並暫停自動切換。目前模式在套用成功（<see cref="OnSwitchFinished"/>）後才改變。
        /// </summary>
        void SwitchTo(ModeConfig target, bool manual, string? autoNotifyText)
        {
            long now = clock.NowMs;

            IReadOnlyList<DetectedMonitor> detected;
            try
            {
                detected = monitors.Detect();
            }
            catch (Exception ex)
            {
                Abort("無法偵測螢幕，未切換到「" + target.Name + "」：\n" + ex.Message, NotificationKind.Error);
                return;
            }

            ApplyPlan plan = ApplyPlanner.Plan(target, detected);
            if (plan.States == null)
            {
                Abort(plan.Rejection!, NotificationKind.Warning);
                return;
            }

            pending = new PendingSwitch(target.Id, target.Name, manual, autoNotifyText);

            ResetShortTermTracking();
            ignoreUntilMs = now + SwitchingGraceMs;
            ApplyHookPolicy();
            OnChanged();

            monitors.BeginApply(plan.States);
        }

        /// <summary>
        /// 切換沒有開始：通知並暫停自動切換 1 分鐘。閒置計時不歸零，暫停結束後條件仍成立就再試一次。
        /// </summary>
        void Abort(string message, NotificationKind kind)
        {
            long now = clock.NowMs;
            ignoreUntilMs = Math.Max(ignoreUntilMs, now + FailurePauseMs);
            ResetShortTermTracking();
            ApplyHookPolicy();
            notifier.Notify(message, kind);
            OnChanged();
        }

        /// <summary>
        /// 轉接層在套用結束時呼叫；<paramref name="error"/> 為 null 表示成功。
        /// 成功：目前模式改為目標模式（手動切換時依目標模式的設定調整自動切換）並存檔。
        /// 失敗：維持原本的模式、跳出錯誤通知、自動切換暫停 1 分鐘。
        /// </summary>
        public void OnSwitchFinished(string? error)
        {
            if (pending is not PendingSwitch done) return;
            long now = clock.NowMs;
            pending = null;

            if (error == null)
            {
                ModeConfig? target = config.FindMode(done.ModeId);   // 切換期間重新載入 cfg 可能刪掉了這個模式
                if (target != null)
                {
                    config.CurrentModeId = target.Id;
                    if (done.Manual && target.ManualAutoSwitch != ManualAutoSwitch.Keep)
                        config.AutoSwitch = target.ManualAutoSwitch == ManualAutoSwitch.On;
                    Save();
                }
                ignoreUntilMs = now + AfterSwitchGraceMs;
                ResetUsageTracking(now);
                if (done.NotifyText != null && config.ShowNotifications) notifier.Notify(done.NotifyText, NotificationKind.Info);
            }
            else
            {
                // 閒置計時不歸零：暫停結束後條件仍成立就再試一次
                ignoreUntilMs = now + FailurePauseMs;
                ResetShortTermTracking();
                notifier.Notify("無法切換到「" + done.ModeName + "」，維持原本的模式：\n" + error, NotificationKind.Error);
            }

            signals.RequestAudioRefresh();
            ApplyHookPolicy();
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

        /// <summary>所有閒置計時歸零，並清除短期的使用跡象。</summary>
        void ResetUsageTracking(long now)
        {
            lastInputUsageMs = now;
            lastAudioUsageMs = now;
            ResetShortTermTracking();
        }

        /// <summary>清除鉤子累積的使用、聲音持續秒數與最後輸入時間的比較基準（閒置計時不變）。</summary>
        void ResetShortTermTracking()
        {
            signals.ResetHookTracking();
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

        /// <summary>把目前實際的螢幕狀態擷取進目前模式並存檔（確認視窗由介面層負責）。</summary>
        public void CaptureToCurrentMode()
        {
            if (IsSwitching)
            {
                notifier.Notify(BusyText, NotificationKind.Info);
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

        /// <summary>
        /// 重新讀取 cfg。目前實際的螢幕狀態以程式記錄為準：程式記錄的目前模式仍存在時維持不變，不因重新載入而切換。
        /// </summary>
        public void ReloadConfig()
        {
            int current = config.CurrentModeId;
            config = LoadConfig();
            if (config.FindMode(current) != null) config.CurrentModeId = current;

            ResetUsageTracking(clock.NowMs);
            signals.RequestAudioRefresh();
            ApplyHookPolicy();
            OnChanged();

            notifier.Notify("已重新載入設定檔。\n" + TakeLoadNote() + StatusSummary, NotificationKind.Info);
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
            bool save = true;

            if (!TryRead(out string? text))
            {
                writesBlocked = true;
                c = new AppConfig();
                AddDefaultMode(c, DetectForNewMode());
            }
            else if (text == null || text.Trim().Length == 0)
            {
                c = new AppConfig { StartWithWindows = startup.IsEnabled };
                AddDefaultMode(c, DetectForNewMode());
            }
            else if (ConfigFile.IsLegacy(text))
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
            }
            else
            {
                ConfigFile.ParseResult parsed = ConfigFile.Parse(text);
                c = parsed.Config;
                loadNote += parsed.Note;
                // 新格式且有模式時啟動不改寫；但有過時的內容（舊版切換條件、bat 設定）時立即改寫，
                // 避免手動編輯時看到舊的內容、轉換結果只存在記憶體中
                bool noModes = c.Modes.Count == 0;
                save = noModes || parsed.RewriteNeeded;
                if (noModes) AddDefaultMode(c, DetectForNewMode());
            }

            if (save) Save(c);
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
            ModeConfig.AssignMissingColors(c.Modes);
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
