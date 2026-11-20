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

        public string? ReadText() => FailReads ? throw new System.IO.IOException("讀取失敗") : Text;

        public void WriteText(string text)
        {
            Text = text;
            Writes++;
        }
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
        public AppCoordinator App { get; }

        public Harness(string? cfg = null, bool start = true)
        {
            Signals = new FakeSignals(Clock);
            Store = new FakeStore(cfg);
            App = new AppCoordinator(Store, Switcher, Signals, Clock, Startup, Notifier);
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
