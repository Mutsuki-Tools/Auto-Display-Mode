using System;
using System.Collections.Generic;
using System.Linq;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Core.Tests
{
    sealed class FakeClock : IClock
    {
        public long NowMs { get; set; } = 1_000_000;
    }

    sealed class FakeSignals : IUsageSignals
    {
        readonly FakeClock clock;
        bool hookUsage;

        public FakeSignals(FakeClock clock) => this.clock = clock;

        public uint LastInputTick { get; set; } = 1;
        public float AudioPeak { get; set; }
        public int AudioRefreshRequests { get; private set; }
        public InputHookPolicy Policy { get; private set; }

        /// <summary>
        /// 模擬實體滑鼠動作：Windows 最後輸入時間改變；鉤子已安裝且不在忽略期間時，鉤子也偵測到使用（與真實鉤子相同）。
        /// </summary>
        public void MoveMouse() => PhysicalInput();

        /// <summary>模擬實體鍵盤輸入（與 <see cref="MoveMouse"/> 相同：核心不區分滑鼠與鍵盤）。</summary>
        public void PressKey() => PhysicalInput();

        /// <summary>
        /// 模擬遠端連線軟體送出（注入）的輸入：Windows 最後輸入時間改變，但真實鉤子在忽略遠端輸入時會濾掉，
        /// 所以只有 <see cref="InputHookPolicy.IgnoreRemoteInput"/> 關閉時鉤子才算數。
        /// </summary>
        public void RemoteInput()
        {
            LastInputTick++;
            if (!Policy.IgnoreRemoteInput && HookActive) hookUsage = true;
        }

        /// <summary>模擬鍵盤或滑鼠輸入，只改變 Windows 最後輸入時間（鉤子沒看到，例如沒有安裝）。</summary>
        public void UseKeyboardOrMouse() => LastInputTick++;

        bool HookActive => Policy.Install && clock.NowMs >= Policy.IgnoreUntilMs;

        void PhysicalInput()
        {
            LastInputTick++;
            if (HookActive) hookUsage = true;
        }

        public bool TakeHookUsage()
        {
            bool r = hookUsage;
            hookUsage = false;
            return r;
        }

        public void ResetHookTracking() => hookUsage = false;
        public float ReadAudioPeak() => AudioPeak;
        public void RequestAudioRefresh() => AudioRefreshRequests++;
        public void ApplyHookPolicy(InputHookPolicy policy) => Policy = policy;
    }

    sealed class FakeStore : IConfigStore
    {
        public FakeStore(string? text = null) => Text = text;

        public string? Text { get; set; }
        public bool FailReads { get; set; }
        public bool FailWrites { get; set; }
        public int Writes { get; private set; }
        public string Location => @"C:\app\AutoDisplayMode.cfg";

        /// <summary>最後一次建立備份時 cfg 的內容；沒有備份過為 null。</summary>
        public string? BackupText { get; private set; }
        public bool FailBackup { get; set; }

        public string? ReadText() => FailReads ? throw new System.IO.IOException("讀取失敗") : Text;

        public void CreateBackup()
        {
            if (FailBackup) throw new System.IO.IOException("備份失敗");
            BackupText = Text;
        }

        public void WriteText(string text)
        {
            if (FailWrites) throw new System.IO.IOException("寫入失敗");
            Text = text;
            Writes++;
        }
    }

    sealed class FakeMonitors : IMonitorSystem
    {
        public FakeMonitors(params DetectedMonitor[] monitors) => Monitors = new List<DetectedMonitor>(monitors);

        public List<DetectedMonitor> Monitors { get; set; }
        public bool Fail { get; set; }

        /// <summary>每次開始套用時要求的完整螢幕狀態。</summary>
        public List<IReadOnlyList<MonitorSettings>> Applied { get; } = new();

        public IReadOnlyList<DetectedMonitor> Detect() =>
            Fail ? throw new InvalidOperationException("偵測失敗") : Monitors.ToArray();

        /// <summary>每次開始套用時，協調者回報的目標模式名稱。</summary>
        public List<string?> AppliedTargets { get; } = new();

        /// <summary>開始套用時用來讀取目標模式名稱。</summary>
        public Func<string?>? TargetName { get; set; }

        public void BeginApply(IReadOnlyList<MonitorSettings> states)
        {
            Applied.Add(states);
            AppliedTargets.Add(TargetName?.Invoke());
        }
    }

    /// <summary>建立假螢幕的捷徑。</summary>
    static class Mon
    {
        public static DetectedMonitor On(string serial, int x, int y, int w, int h, int hz,
            Orientation orientation = Orientation.Landscape, bool primary = false,
            string manufacturer = "DEL", string product = "A0F4", string connector = "DP1", string name = "DELL U2720Q") =>
            new()
            {
                Manufacturer = manufacturer, ProductCode = product, Serial = serial, Connector = connector, Name = name,
                IsActive = true, X = x, Y = y, Current = new VideoMode(w, h, hz), Orientation = orientation, IsPrimary = primary,
                Native = new VideoMode(w, h, hz), SupportedModes = new[] { new VideoMode(w, h, hz) },
            };

        public static DetectedMonitor Off(string serial, VideoMode? native,
            string manufacturer = "GSM", string product = "5B7F", string connector = "HDMI1", string name = "LG ULTRAGEAR") =>
            new()
            {
                Manufacturer = manufacturer, ProductCode = product, Serial = serial, Connector = connector, Name = name,
                IsActive = false, Native = native,
                SupportedModes = native == null ? Array.Empty<VideoMode>() : new[] { native },
            };
    }

    /// <summary>手寫 cfg 中一條切換條件（Switch）的內容（不含分段標題；由 <see cref="Harness.Modes"/> 編號）。</summary>
    static class Sw
    {
        /// <summary>使用來源「滑鼠或鍵盤」。</summary>
        public const UsageSources MouseOrKeyboard = UsageSources.MouseKeyboard;
        /// <summary>使用來源「聲音」。</summary>
        public const UsageSources Audio = UsageSources.Audio;
        /// <summary>兩種使用來源都選。</summary>
        public const UsageSources Both = UsageSources.MouseKeyboard | UsageSources.Audio;

        public static string Idle(int from, int to, int amount, UsageSources sources = MouseOrKeyboard, IdleUnit unit = IdleUnit.Minutes) =>
            Harness.Cfg("From=" + from, "Condition=Idle", "IdleTime=" + amount, "IdleUnit=" + unit,
                "MouseKeyboard=" + Flag(sources, MouseOrKeyboard), "Audio=" + Flag(sources, Audio), "To=" + to);

        public static string Usage(int from, int to, UsageSources sources = MouseOrKeyboard) =>
            Harness.Cfg("From=" + from, "Condition=UsageDetected",
                "MouseKeyboard=" + Flag(sources, MouseOrKeyboard), "Audio=" + Flag(sources, Audio), "To=" + to);

        static string Flag(UsageSources sources, UsageSources flag) => (sources & flag) != 0 ? "1" : "0";
    }

    sealed class FakeStartup : IStartupRegistration
    {
        public bool IsEnabled { get; set; }
        public string? FailWith { get; set; }

        /// <summary>每次呼叫 <see cref="SetEnabled"/> 的值（開啟時真實的轉接層會寫入目前的 exe 路徑）。</summary>
        public List<bool> Calls { get; } = new();

        public void SetEnabled(bool enabled)
        {
            Calls.Add(enabled);
            if (FailWith != null) throw new UnauthorizedAccessException(FailWith);
            IsEnabled = enabled;
        }
    }

    sealed class FakeNotifier : INotifier
    {
        public List<(string Text, NotificationKind Kind)> Shown { get; } = new();
        public void Notify(string text, NotificationKind kind) => Shown.Add((text, kind));
    }

    /// <summary>把協調者與所有假物件組在一起，並提供以秒為單位推進時間的方法。</summary>
    sealed class Harness
    {
        public FakeClock Clock { get; } = new();
        public FakeSignals Signals { get; }
        public FakeStore Store { get; }
        public FakeStartup Startup { get; } = new();
        public FakeNotifier Notifier { get; } = new();
        public FakeMonitors Monitors { get; }
        public AppCoordinator App { get; }

        /// <summary>預設接上一台開啟中的主螢幕。</summary>
        public static DetectedMonitor DefaultMonitor => Mon.On("SN0001", 0, 0, 1920, 1080, 60, primary: true);

        public Harness(string? cfg = null, bool start = true, DetectedMonitor[]? monitors = null, bool startupEnabled = false)
        {
            Signals = new FakeSignals(Clock);
            Store = new FakeStore(cfg);
            Monitors = new FakeMonitors(monitors ?? new[] { DefaultMonitor });
            Startup.IsEnabled = startupEnabled;
            App = new AppCoordinator(Store, Monitors, Signals, Clock, Startup, Notifier);
            Monitors.TargetName = () => App.SwitchTargetName;
            if (start) App.Start();
        }

        /// <summary>每秒 tick 一次，共 <paramref name="seconds"/> 秒；每秒 tick 前先執行 <paramref name="eachSecond"/>。</summary>
        public void Run(int seconds, Action? eachSecond = null)
        {
            for (int i = 0; i < seconds; i++)
            {
                Clock.NowMs += 1000;
                eachSecond?.Invoke();
                App.Tick();
            }
        }

        /// <summary>一直 tick 到開始切換為止（最多 <paramref name="maxSeconds"/> 秒），回傳經過的秒數；沒切換則回傳 -1。</summary>
        public int RunUntilSwitch(int maxSeconds, Action? eachSecond = null)
        {
            int before = Monitors.Applied.Count;
            for (int i = 1; i <= maxSeconds; i++)
            {
                Run(1, eachSecond);
                if (Monitors.Applied.Count > before) return i;
            }
            return -1;
        }

        /// <summary>最後一次開始套用時要求的螢幕狀態。</summary>
        public IReadOnlyList<MonitorSettings> LastApplied => Monitors.Applied[Monitors.Applied.Count - 1];

        /// <summary>完成目前進行中的切換。</summary>
        public void FinishSwitch(string? error = null) => App.OnSwitchFinished(error);

        public static string Cfg(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

        /// <summary><see cref="Modes"/> 中的三個模式。</summary>
        public const int Play = 1, Save = 2, AllOff = 3;

        /// <summary>
        /// 三個模式「遊玩」（1，1920x1080）、「省電」（2，1280x720）、「全關」（3，800x600），各記錄預設螢幕為主螢幕。
        /// <paramref name="extra"/> 中以 <c>From=</c> 開頭的是切換條件（見 <see cref="Sw"/>，依出現順序編號），其餘放在 [General]。
        /// </summary>
        public static Harness Modes(int current, params string[] extra)
        {
            static string Mode(int id, string name, int w, int h) => Cfg(
                "[Mode." + id + "]", "Name=" + name,
                "[Mode." + id + ".Monitor.1]", "Monitor=" + DefaultMonitor.Id, "Enabled=1", "X=0", "Y=0",
                "Width=" + w, "Height=" + h, "RefreshRate=60", "Primary=1");

            var general = new List<string>
            {
                "[General]", "AutoSwitch=1", "CurrentMode=" + current, "AudioSeconds=3", "AudioThreshold=0.1",
            };
            string conditions = "";
            int n = 0;
            foreach (string item in extra)
            {
                if (item.StartsWith("From=", StringComparison.Ordinal)) conditions += Cfg("[Switch." + ++n + "]") + item;
                else general.Add(item);
            }

            return new Harness(
                Cfg(general.ToArray()) + conditions +
                Mode(Play, "遊玩", 1920, 1080) + Mode(Save, "省電", 1280, 720) + Mode(AllOff, "全關", 800, 600));
        }

        /// <summary>
        /// <see cref="Modes"/> 加上與舊版相同的兩條切換條件：「遊玩」閒置 5 分鐘（滑鼠或鍵盤、聲音）→「省電」；
        /// 「省電」偵測到使用（滑鼠或鍵盤、聲音）→「遊玩」。
        /// </summary>
        public static Harness PlayAndSave(int current, params string[] extra) =>
            Modes(current, extra.Concat(new[]
            {
                Sw.Idle(Play, Save, 5, Sw.Both),
                Sw.Usage(Save, Play, Sw.Both),
            }).ToArray());
    }
}
