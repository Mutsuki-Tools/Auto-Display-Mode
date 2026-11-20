using AutoDisplayMode.Core;
using Xunit;

namespace AutoDisplayMode.Core.Tests
{
    /// <summary>過渡期的舊版兩條切換條件，以 HomeMode／AwayMode 指向模式。</summary>
    public class AutoSwitchTests
    {
        static Harness InHome(params string[] extra) => Harness.TwoModes("home", extra);

        static Harness InAway(params string[] extra) => Harness.TwoModes("away", extra);

        [Fact]
        public void 遊玩模式閒置達指定分鐘後切換到省電模式()
        {
            var h = InHome();

            int seconds = h.RunUntilSwitch(10 * 60);

            Assert.Equal(5 * 60, seconds);
            Assert.Equal("省電", Assert.Single(h.Monitors.AppliedTargets));
            var state = Assert.Single(h.LastApplied);
            Assert.Equal((1280, 720), (state.Width, state.Height));   // 依「省電」模式的螢幕設定套用

            h.FinishSwitch();
            Assert.Equal(2, h.App.Config.CurrentModeId);
        }

        [Fact]
        public void 遊玩模式中有鍵盤或滑鼠輸入會重新計算閒置時間()
        {
            var h = InHome();
            h.Run(4 * 60);
            h.Signals.UseKeyboardOrMouse();

            int seconds = h.RunUntilSwitch(10 * 60);

            Assert.Equal(1 + 5 * 60, seconds);   // 輸入在下一次 tick 才被看到，之後再閒置滿 5 分鐘
        }

        [Fact]
        public void 省電模式中偵測到滑鼠使用就切換到遊玩模式()
        {
            var h = InAway();
            h.Run(10);   // 度過啟動緩衝期
            Assert.Empty(h.Monitors.Applied);

            h.Signals.MoveMouse();
            h.Run(1);

            Assert.Equal("遊玩", Assert.Single(h.Monitors.AppliedTargets));
            h.FinishSwitch();
            Assert.Equal(1, h.App.Config.CurrentModeId);
        }

        [Fact]
        public void 省電模式中鍵盤輸入不算使用()
        {
            var h = InAway();
            h.Run(10);

            h.Signals.UseKeyboardOrMouse();
            h.Run(5);

            Assert.Empty(h.Monitors.Applied);
        }

        [Fact]
        public void 聲音持續超過門檻秒數才算使用()
        {
            var h = InAway();
            h.Run(10);

            h.Signals.AudioPeak = 0.5f;
            h.Run(3);
            Assert.Empty(h.Monitors.Applied);

            h.Run(1);
            Assert.Equal("遊玩", Assert.Single(h.Monitors.AppliedTargets));
        }

        [Fact]
        public void 聲音中斷就重新計算持續秒數()
        {
            var h = InAway();
            h.Run(10);

            for (int i = 0; i < 5; i++)
            {
                h.Signals.AudioPeak = 0.5f;
                h.Run(3);
                h.Signals.AudioPeak = 0f;
                h.Run(1);
            }

            Assert.Empty(h.Monitors.Applied);
        }

        [Fact]
        public void 音量未超過門檻不算聲音()
        {
            var h = InAway();
            h.Run(10);

            h.Signals.AudioPeak = 0.09f;
            h.Run(30);

            Assert.Empty(h.Monitors.Applied);
        }

        [Fact]
        public void 遊玩模式中持續播放聲音不會被判定為閒置()
        {
            var h = InHome();
            h.Signals.AudioPeak = 0.5f;

            Assert.Equal(-1, h.RunUntilSwitch(20 * 60));
        }

        [Fact]
        public void 關閉聲音偵測時聲音不算使用()
        {
            var h = InAway("AudioDetect=0");
            h.Run(10);

            h.Signals.AudioPeak = 0.5f;
            h.Run(30);

            Assert.Empty(h.Monitors.Applied);
        }

        // ---- 舊版切換條件對應到模式 ----

        [Fact]
        public void 處於舊版切換條件以外的模式時不自動切換也不掛滑鼠鉤子()
        {
            var h = new Harness(Harness.Cfg(
                "[General]", "AutoSwitch=1", "CurrentMode=3",
                "[LegacySwitch]", "HomeMode=1", "AwayMode=2", "IdleMinutes=5",
                "[Mode.1]", "Name=遊玩", "[Mode.2]", "Name=省電", "[Mode.3]", "Name=簡報",
                "[Mode.3.Monitor.1]", "Monitor=" + Harness.DefaultMonitor.Id, "Enabled=1", "Primary=1"));

            Assert.Equal(-1, h.RunUntilSwitch(30 * 60, () => { h.Signals.AudioPeak = 0.5f; h.Signals.MoveMouse(); }));
            Assert.False(h.Signals.Policy.Install);
        }

        [Fact]
        public void 手動切換到舊版切換條件中的模式後從該模式出發()
        {
            var h = new Harness(Harness.Cfg(
                "[General]", "AutoSwitch=1", "CurrentMode=3",
                "[LegacySwitch]", "HomeMode=1", "AwayMode=2", "IdleMinutes=5",
                "[Mode.1]", "Name=遊玩", "[Mode.2]", "Name=省電", "[Mode.3]", "Name=簡報",
                "[Mode.1.Monitor.1]", "Monitor=" + Harness.DefaultMonitor.Id, "Enabled=1", "Primary=1",
                "[Mode.2.Monitor.1]", "Monitor=" + Harness.DefaultMonitor.Id, "Enabled=1", "Primary=1"));

            h.App.ManualSwitch(1);
            h.FinishSwitch();

            Assert.Equal(5 * 60, h.RunUntilSwitch(10 * 60));   // 從切換完成起閒置滿 5 分鐘
            Assert.Equal("省電", h.Monitors.AppliedTargets[1]);
        }

        [Fact]
        public void 只設定其中一個角色時舊版切換條件不作用()
        {
            var h = Harness.TwoModes("home", "AwayMode=0");

            Assert.Equal(-1, h.RunUntilSwitch(30 * 60));
            Assert.Equal(0, h.App.Config.AwayModeId);
        }
    }
}
