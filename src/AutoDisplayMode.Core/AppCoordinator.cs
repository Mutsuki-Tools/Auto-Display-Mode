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
        const string SettingsOpenText = "設定視窗開啟中，請先關閉設定視窗";

        readonly IConfigStore store;
        readonly IMonitorSystem monitors;
        readonly IUsageSignals signals;
        readonly IClock clock;
        readonly IStartupRegistration startup;
        readonly INotifier notifier;

        AppConfig config;
        long lastInputUsageMs;   // 最後一次「鍵盤滑鼠」使用的時間（閒置用：Windows 最後輸入時間改變）
        long lastAudioUsageMs;   // 最後一次「聲音」使用的時間（聲音持續達門檻秒數）
        long ignoreUntilMs;      // 在這個時間之前不做自動切換（切換螢幕後的緩衝、失敗後的暫停）
        int audioStreak;         // 連續偵測到聲音的秒數
        uint lastInputTick;      // 上次看到的 Windows 最後輸入時間
        bool settingsOpen;    // 設定視窗開著：暫停自動切換
        bool writesBlocked;   // cfg 讀取或舊版備份失敗：不寫入 cfg，避免覆蓋原本的內容
        bool captureStale = true; // 設定視窗最後一次擷取之後又切換過螢幕（或還沒擷取過）：擷取的模式不再代表目前的螢幕狀態
        string loadNote = ""; // 載入 cfg 時發生的事（轉移、備份失敗等），附加在下一則通知中

        /// <summary>套用螢幕狀態的原因。</summary>
        enum SwitchKind
        {
            /// <summary>依切換條件自動切換。</summary>
            Auto,
            /// <summary>手動切換。</summary>
            Manual,
            /// <summary>螢幕配置分頁的預覽套用（不改變目前模式）。</summary>
            Preview,
            /// <summary>還原為預覽前的螢幕狀態。</summary>
            Revert,
        }

        /// <summary>進行中的切換：原因、目標模式（預覽與還原時為 0）、顯示的名稱、自動切換成功時要跳出的通知。</summary>
        sealed record PendingSwitch(SwitchKind Kind, int ModeId, string ModeName, string? NotifyText);

        PendingSwitch? pending;   // 未切換時為 null
        IReadOnlyList<MonitorSettings>? previewBefore;   // 預覽前的螢幕狀態；沒有預覽（或已保留、作廢）時為 null
        bool previewApplied;      // 預覽已套用，等待使用者保留或還原

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
            monitors.Changed += OnMonitorsChanged;
        }

        /// <summary>狀態改變（介面需要更新）時觸發。</summary>
        public event Action? Changed;

        /// <summary>
        /// 接上的螢幕或它們的狀態可能改變（設定視窗據此重新偵測）。切換中不觸發（偵測到的可能是過渡狀態），
        /// 改在切換結束時觸發一次。
        /// </summary>
        public event Action? MonitorsChanged;

        void OnMonitorsChanged()
        {
            if (IsSwitching) return;   // 切換結束時一定會觸發
            else MonitorsChanged?.Invoke();
        }

        /// <summary>螢幕切換執行中。</summary>
        public bool IsSwitching => pending != null;

        /// <summary>切換中的目標模式 ID；未切換時為 0。</summary>
        public int SwitchTargetId => pending?.ModeId ?? 0;

        /// <summary>切換中的目標模式名稱；未切換時為 null。</summary>
        public string? SwitchTargetName => pending?.ModeName;

        /// <summary>目前的設定。請勿直接修改，改用協調者的方法。</summary>
        public AppConfig Config => config;

        public string ConfigLocation => store.Location;

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
            SyncStartupIntoLoadNote();
            notifier.Notify("Auto Display Mode 已啟動。\n" + TakeLoadNote() + StatusSummary, NotificationKind.Info);
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

            // 鍵盤滑鼠（閒置用）：Windows 最後輸入時間，不需鉤子
            uint t = signals.LastInputTick;
            if (t != lastInputTick)
            {
                lastInputTick = t;
                lastInputUsageMs = now;
            }

            // 鍵盤滑鼠（偵測到使用用）：鉤子（經移動門檻與遠端過濾），只在需要時安裝
            bool hookUsage = signals.TakeHookUsage();

            ApplyHookPolicy();

            if (!config.AutoSwitch || IsSwitching || settingsOpen || now < ignoreUntilMs) return;

            foreach (SwitchCondition s in ConditionsFromCurrentMode())
            {
                ModeConfig? target = config.FindMode(s.ToModeId);
                if (target == null || !IsSatisfied(s, now, hookUsage, audioUsage)) continue;
                string text = s.Kind == SwitchConditionKind.Idle
                    ? s.DescribeCondition() + "，已自動切換到「" + target.Name + "」。"
                    : "偵測到使用，已自動切換到「" + target.Name + "」。";
                SwitchTo(target, SwitchKind.Auto, text);
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
        /// 只在「自動切換開 + 沒有在切換 + 設定視窗沒開 + 目前模式是某條『偵測到使用 + 鍵盤滑鼠』切換條件的來源模式」時
        /// 安裝滑鼠與鍵盤鉤子，其他情況完全不掛（不影響遊戲的輸入延遲）。
        /// </summary>
        void ApplyHookPolicy()
        {
            bool need = config.AutoSwitch && !IsSwitching && !settingsOpen && ConditionsFromCurrentMode().Any(s =>
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
            if (mode != null) SwitchTo(mode, SwitchKind.Manual, null);
        }

        /// <summary>
        /// 依目前接上的螢幕規劃並開始套用 <paramref name="target"/>。偵測失敗或規劃被拒絕時不切換、
        /// 通知並暫停自動切換。目前模式在套用成功（<see cref="OnSwitchFinished"/>）後才改變。
        /// </summary>
        void SwitchTo(ModeConfig target, SwitchKind kind, string? autoNotifyText)
        {
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

            // 等待確認的預覽作廢：螢幕已改成這次切換的模式，不應再還原
            previewApplied = false;
            previewBefore = null;
            Begin(new PendingSwitch(kind, target.Id, target.Name, autoNotifyText), plan.States);
        }

        /// <summary>開始在背景套用 <paramref name="states"/>；結束時轉接層呼叫 <see cref="OnSwitchFinished"/>。</summary>
        void Begin(PendingSwitch switching, IReadOnlyList<MonitorSettings> states)
        {
            pending = switching;
            ResetShortTermTracking();
            ignoreUntilMs = clock.NowMs + SwitchingGraceMs;
            ApplyHookPolicy();
            OnChanged();

            monitors.BeginApply(states);
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
        /// 成功：目前模式改為目標模式（手動切換時依目標模式的設定調整自動切換）並存檔；
        /// 預覽成功時等待使用者保留或還原（觸發 <see cref="PreviewApplied"/>），預覽與還原都不改變目前模式。
        /// 失敗：維持原本的模式、跳出錯誤通知、自動切換暫停 1 分鐘。
        /// 結束後觸發 <see cref="MonitorsChanged"/>（螢幕狀態已改變）。
        /// </summary>
        public void OnSwitchFinished(string? error)
        {
            if (pending is not PendingSwitch done) return;
            long now = clock.NowMs;
            pending = null;
            captureStale = true;   // 失敗時螢幕也可能已部分改變

            if (error == null)
            {
                if (done.Kind == SwitchKind.Preview) previewApplied = true;
                ModeConfig? target = done.Kind is SwitchKind.Auto or SwitchKind.Manual
                    ? config.FindMode(done.ModeId)   // 切換期間重新載入 cfg 可能刪掉了這個模式
                    : null;
                if (target != null)
                {
                    config.CurrentModeId = target.Id;
                    if (done.Kind == SwitchKind.Manual && target.ManualAutoSwitch != ManualAutoSwitch.Keep)
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
                if (done.Kind == SwitchKind.Preview) previewBefore = null;   // 轉接層已盡量還原為套用前的狀態
                notifier.Notify(done.Kind switch
                {
                    SwitchKind.Preview => "無法預覽螢幕配置：\n" + error,
                    SwitchKind.Revert => "無法還原為預覽前的螢幕狀態：\n" + error,
                    _ => "無法切換到「" + done.ModeName + "」，維持原本的模式：\n" + error,
                }, NotificationKind.Error);
            }

            signals.RequestAudioRefresh();
            ApplyHookPolicy();
            OnChanged();
            MonitorsChanged?.Invoke();
            if (done.Kind == SwitchKind.Preview && previewApplied) PreviewApplied?.Invoke();
        }

        // ------------------------------------------------------------
        //  預覽套用（螢幕配置分頁）
        // ------------------------------------------------------------

        /// <summary>預覽已套用，等待使用者保留（<see cref="KeepPreview"/>）或還原（<see cref="RevertPreview"/>）。</summary>
        public bool PreviewAwaitingConfirm => previewApplied;

        /// <summary>預覽套用成功、開始等待使用者保留或還原時觸發。</summary>
        public event Action? PreviewApplied;

        /// <summary>
        /// 立即套用編輯中的模式 <paramref name="mode"/>（設定視窗副本中的模式），不改變目前模式也不存檔；
        /// 先記下目前的螢幕狀態供 <see cref="RevertPreview"/> 還原。開始套用時回傳 null，無法預覽時回傳原因。
        /// </summary>
        public string? PreviewLayout(ModeConfig mode)
        {
            if (IsSwitching) return "正在切換中，請稍候再預覽。";
            if (MonitorLayout.Check(mode).Errors.FirstOrDefault() is string invalid) return "螢幕配置有誤：" + invalid + "。";

            if (DetectMonitors(out string? detectError) is not IReadOnlyList<DetectedMonitor> detected) return detectError;

            ApplyPlan plan = ApplyPlanner.Plan(mode, detected);
            if (plan.States == null) return "預覽後沒有任何螢幕會開著，已取消預覽。";

            if (!previewApplied) previewBefore = CurrentStates(detected);   // 連續預覽時還原到第一次預覽前的狀態
            previewApplied = false;
            Begin(new PendingSwitch(SwitchKind.Preview, 0, "預覽的螢幕配置", null), plan.States);
            return null;
        }

        /// <summary>保留預覽套用的螢幕狀態（不存檔；按「套用」或「確定」才存入模式）。</summary>
        public void KeepPreview()
        {
            if (!previewApplied) return;
            previewApplied = false;
            previewBefore = null;
            OnChanged();
        }

        /// <summary>還原為預覽前的螢幕狀態。預覽已保留或已作廢（例如之後從工作列手動切換過）時不動作。</summary>
        public void RevertPreview()
        {
            if (!previewApplied || previewBefore is not IReadOnlyList<MonitorSettings> before) return;
            previewApplied = false;
            previewBefore = null;
            Begin(new PendingSwitch(SwitchKind.Revert, 0, "預覽前的螢幕狀態", null), before);
        }

        /// <summary>
        /// 螢幕配置分頁顯示用：偵測目前接上的螢幕。失敗時回傳 null，原因放在 <paramref name="error"/>
        /// （不跳通知：插拔時會再偵測一次）。
        /// </summary>
        public IReadOnlyList<DetectedMonitor>? DetectMonitors(out string? error)
        {
            try
            {
                error = null;
                return monitors.Detect();
            }
            catch (Exception ex)
            {
                error = "無法偵測螢幕：" + ex.Message;
                return null;
            }
        }

        /// <summary>目前實際的螢幕狀態（每台接上的螢幕一筆，主螢幕只有一台且位於 (0,0)）。</summary>
        static IReadOnlyList<MonitorSettings> CurrentStates(IReadOnlyList<DetectedMonitor> detected)
        {
            var now = new ModeConfig(0, "");
            now.Monitors.AddRange(Capture.Run(detected, Array.Empty<MonitorSettings>()));
            return ApplyPlanner.Plan(now, detected).States ?? now.Monitors;
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
        //  設定視窗
        // ------------------------------------------------------------

        /// <summary>設定視窗開著（暫停自動切換、停用重新載入 cfg 與從工作列擷取）。</summary>
        public bool SettingsWindowOpen => settingsOpen;

        /// <summary>設定視窗開啟時呼叫：暫停自動切換。</summary>
        public void OnSettingsWindowOpened()
        {
            settingsOpen = true;
            ApplyHookPolicy();
            OnChanged();
        }

        /// <summary>
        /// 套用設定視窗暫存的設定（<see cref="AppConfig.Clone"/> 出來修改的複本）：寫入 cfg 並立即生效。
        /// 成功時回傳 null；失敗時回傳原因，設定維持不變。
        /// 成功時模式依順序重新編號為 1、2、3…，切換條件與目前模式跟著改，<paramref name="edited"/> 也一樣重新編號。
        /// </summary>
        /// <param name="capturedModeId">
        /// 在設定視窗中最後一次擷取（<see cref="CaptureForSettings"/>）存入的模式；套用後它成為目前模式並重新計時。
        /// 沒有擷取、該模式已從副本刪除，或擷取之後又切換過螢幕時，目前模式以協調者記錄的為準。
        /// </param>
        public string? ApplySettings(AppConfig edited, int? capturedModeId = null)
        {
            if (IsSwitching) return "正在切換中，請稍候再套用。";   // 切換完成時會改變目前模式

            // 目前模式與自動切換以協調者記錄的為準：設定視窗開著時仍可從工作列手動切換、開關自動切換
            bool captured = capturedModeId is int id && !captureStale && edited.FindMode(id) != null;
            int current = captured ? capturedModeId!.Value : config.CurrentModeId;

            if (edited.Validate(current) is string invalid) return invalid;
            if (writesBlocked)
                return "讀取設定檔或備份舊版設定檔時失敗，為避免覆蓋原本的內容，未套用任何修改。\n" +
                       "請關閉設定視窗，修正設定檔後從工作列選「重新載入 cfg」。";

            AppConfig next = edited.Clone();
            foreach (ModeConfig m in next.Modes) m.Name = m.Name.Trim();
            next.CurrentModeId = current;
            next.AutoSwitch = config.AutoSwitch;
            next.RenumberModes();   // cfg 的 [Mode.N] 依模式的順序編號
            if (!Save(next)) return "無法寫入設定檔：\n" + store.Location;
            edited.RenumberModes();   // 副本也一樣重新編號：設定視窗繼續編輯、再次套用時與目前的設定一致

            bool startupChanged = next.StartWithWindows != config.StartWithWindows;
            config = next;
            if (startupChanged && SyncStartup() is string error)
                notifier.Notify(error + "\n下次啟動程式時會再試一次。", NotificationKind.Error);

            if (captured)
            {
                // 與從工作列擷取相同：存入的模式成為目前模式並重新計時
                captureStale = true;
                ResetUsageTracking(clock.NowMs);
            }
            audioStreak = 0;   // 聲音音量門檻或持續秒數可能改變
            ApplyHookPolicy();
            OnChanged();
            return null;
        }

        /// <summary>
        /// 設定視窗關閉時呼叫：恢復自動切換。所有閒置計時歸零並有 3 秒緩衝期，
        /// 避免例如「只選聲音」的閒置條件在編輯期間累積，一關閉視窗就切換。
        /// </summary>
        public void OnSettingsWindowClosed()
        {
            long now = clock.NowMs;
            settingsOpen = false;
            ignoreUntilMs = Math.Max(ignoreUntilMs, now + ToggleGraceMs);
            ResetUsageTracking(now);
            ApplyHookPolicy();
            OnChanged();
        }

        // ------------------------------------------------------------
        //  開機啟動
        // ------------------------------------------------------------

        /// <summary>
        /// 以 cfg 為準同步登錄檔：開啟時寫入目前的 exe 路徑（exe 搬家後重開程式就會修正），關閉時刪除。
        /// 成功時回傳 null，失敗時回傳說明。cfg 讀取失敗時（設定是預設值）不同步，避免誤刪開機啟動。
        /// </summary>
        string? SyncStartup()
        {
            if (writesBlocked) return null;
            try
            {
                startup.SetEnabled(config.StartWithWindows);
                return null;
            }
            catch (Exception ex)
            {
                return "無法同步開機啟動設定：" + ex.Message;
            }
        }

        /// <summary>同步開機啟動；失敗時把說明附加在下一則啟動或重新載入的通知中。</summary>
        void SyncStartupIntoLoadNote()
        {
            if (SyncStartup() is string error) loadNote += error + "\n";
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
            if (settingsOpen)
            {
                // 設定視窗的副本不含這次擷取，套用時會把它蓋掉
                notifier.Notify(SettingsOpenText + "再擷取。", NotificationKind.Info);
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

        /// <summary>
        /// 設定視窗中的擷取：依目前實際的螢幕狀態更新 <paramref name="existing"/>（模式原本的螢幕設定），
        /// 回傳新的螢幕設定，不存檔也不改變目前模式（由設定視窗存進副本，套用時再傳入 <see cref="ApplySettings"/>）。
        /// 切換中或偵測螢幕失敗時通知並回傳 null。
        /// </summary>
        public List<MonitorSettings>? CaptureForSettings(IReadOnlyList<MonitorSettings> existing)
        {
            if (IsSwitching)
            {
                notifier.Notify(BusyText, NotificationKind.Info);
                return null;
            }

            IReadOnlyList<DetectedMonitor> detected;
            try
            {
                detected = monitors.Detect();
            }
            catch (Exception ex)
            {
                notifier.Notify("無法偵測螢幕：" + ex.Message, NotificationKind.Error);
                return null;
            }

            captureStale = false;
            return Capture.Run(detected, existing);
        }

        // ------------------------------------------------------------
        //  設定檔
        // ------------------------------------------------------------

        /// <summary>
        /// 重新讀取 cfg。目前實際的螢幕狀態以程式記錄為準：程式記錄的目前模式仍存在時維持不變，不因重新載入而切換。
        /// </summary>
        public void ReloadConfig()
        {
            if (settingsOpen)
            {
                notifier.Notify(SettingsOpenText + "再重新載入 cfg。", NotificationKind.Info);
                return;
            }

            int current = config.CurrentModeId;
            config = LoadConfig();
            if (config.FindMode(current) != null) config.CurrentModeId = current;
            SyncStartupIntoLoadNote();

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
            var mode = new ModeConfig(c.NextModeId(), DefaultModeName);
            mode.Monitors.AddRange(Capture.Run(detected, Array.Empty<MonitorSettings>()));
            c.Modes.Add(mode);
            ModeConfig.AssignMissingColors(c.Modes);
            c.CurrentModeId = mode.Id;
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
