using System.Linq;
using AutoDisplayMode.Core;
using Xunit;

namespace AutoDisplayMode.Core.Tests
{
    public class AutoSwitchTests
    {
        static Harness InHome(params string[] extra) =>
            new(Harness.Cfg(new[] { "AutoMode=1", "IdleMinutes=5", "LastMode=home", "AudioSeconds=3", "AudioThreshold=0.1" }.Concat(extra).ToArray()));

        static Harness InAway(params string[] extra) =>
            new(Harness.Cfg(new[] { "AutoMode=1", "IdleMinutes=5", "LastMode=away", "AudioSeconds=3", "AudioThreshold=0.1" }.Concat(extra).ToArray()));

        [Fact]
        public void 遊玩模式閒置達指定分鐘後切換到省電模式()
        {
            var h = new Harness(Harness.Cfg("AutoMode=1", "IdleMinutes=5", "LastMode=home", "BatPath=C:\\x\\m.bat", "AwayArgs=away"));

            int seconds = h.RunUntilSwitch(10 * 60);

            Assert.Equal(5 * 60, seconds);
            Assert.Equal((Mode.Away, "C:\\x\\m.bat", "away"), Assert.Single(h.Switcher.Started));
            Assert.Equal(Mode.Away, h.App.CurrentMode);
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
            var h = InAway("HomeArgs=home");
            h.Run(10);   // 度過啟動緩衝期
            Assert.Empty(h.Switcher.Started);

            h.Signals.MoveMouse();
            h.Run(1);

            Assert.Equal(Mode.Home, Assert.Single(h.Switcher.Started).Target);
            Assert.Equal(Mode.Home, h.App.CurrentMode);
        }

        [Fact]
        public void 省電模式中鍵盤輸入不算使用()
        {
            var h = InAway();
            h.Run(10);

            h.Signals.UseKeyboardOrMouse();
            h.Run(5);

            Assert.Empty(h.Switcher.Started);
        }

        [Fact]
        public void 聲音持續超過門檻秒數才算使用()
        {
            var h = InAway();
            h.Run(10);

            h.Signals.AudioPeak = 0.5f;
            h.Run(3);
            Assert.Empty(h.Switcher.Started);

            h.Run(1);
            Assert.Equal(Mode.Home, Assert.Single(h.Switcher.Started).Target);
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

            Assert.Empty(h.Switcher.Started);
        }

        [Fact]
        public void 音量未超過門檻不算聲音()
        {
            var h = InAway();
            h.Run(10);

            h.Signals.AudioPeak = 0.09f;
            h.Run(30);

            Assert.Empty(h.Switcher.Started);
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

            Assert.Empty(h.Switcher.Started);
        }
    }
}
