using System;
using System.Collections.Generic;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Core.Tests
{
    sealed class FakeClock : IClock
    {
        public long NowMs { get; set; } = 1_000_000;
    }

    sealed class FakeSwitcher : IDisplaySwitcher
    {
        public bool Ready { get; set; } = true;
        public List<(Mode Target, string Bat, string Args)> Started { get; } = new();

        public bool IsReady(AppConfig config) => Ready;

        public void Begin(Mode target, AppConfig config) =>
            Started.Add((target, config.BatPath, config.ArgsFor(target)));
    }

    sealed class FakeSignals : IUsageSignals
    {
        readonly FakeClock clock;
        bool mouseUsage;

        public FakeSignals(FakeClock clock) => this.clock = clock;

        public uint LastInputTick { get; set; } = 1;
        public float AudioPeak { get; set; }
        public int AudioRefreshRequests { get; private set; }
        public MouseHookPolicy Policy { get; private set; }

        /// <summary>模擬實體滑鼠動作：只有鉤子已安裝且不在忽略期間才算數（與真實鉤子相同）。</summary>
        public void MoveMouse()
        {
            if (Policy.Install && clock.NowMs >= Policy.IgnoreUntilMs) mouseUsage = true;
        }

        /// <summary>模擬鍵盤或滑鼠輸入：Windows 最後輸入時間改變。</summary>
        public void UseKeyboardOrMouse() => LastInputTick++;

        public bool TakeMouseUsage()
        {
            bool r = mouseUsage;
            mouseUsage = false;
            return r;
        }

        public void ResetMouseTracking() => mouseUsage = false;
        public float ReadAudioPeak() => AudioPeak;
        public void RequestAudioRefresh() => AudioRefreshRequests++;
        public void ApplyHookPolicy(MouseHookPolicy policy) => Policy = policy;
    }

    sealed class FakeStore : IConfigStore
    {
        public FakeStore(string? text = null) => Text = text;

        public string? Text { get; set; }
        public bool FailReads { get; set; }
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
            Text = text;
            Writes++;
        }
    }

    sealed class FakeMonitors : IMonitorSystem
    {
        public FakeMonitors(params DetectedMonitor[] monitors) => Monitors = new List<DetectedMonitor>(monitors);

        public List<DetectedMonitor> Monitors { get; set; }
        public bool Fail { get; set; }

        public IReadOnlyList<DetectedMonitor> Detect() =>
            Fail ? throw new InvalidOperationException("偵測失敗") : Monitors.ToArray();
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

    sealed class FakeStartup : IStartupRegistration
    {
        public bool IsEnabled { get; set; }
        public string? FailWith { get; set; }

        public void SetEnabled(bool enabled)
        {
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
        public FakeSwitcher Switcher { get; } = new();
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
            App = new AppCoordinator(Store, Switcher, Monitors, Signals, Clock, Startup, Notifier);
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
            int before = Switcher.Started.Count;
            for (int i = 1; i <= maxSeconds; i++)
            {
                Run(1, eachSecond);
                if (Switcher.Started.Count > before) return i;
            }
            return -1;
        }

        /// <summary>完成目前進行中的切換。</summary>
        public void FinishSwitch(string? error = null) => App.OnSwitchFinished(error);

        public static string Cfg(params string[] lines) => string.Join("\r\n", lines) + "\r\n";
    }
}
