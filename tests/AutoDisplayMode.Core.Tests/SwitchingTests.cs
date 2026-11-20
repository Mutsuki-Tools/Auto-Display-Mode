using AutoDisplayMode.Core;
using Xunit;

namespace AutoDisplayMode.Core.Tests
{
    public class SwitchingTests
    {
        const string Base = "IdleMinutes=5\r\nAudioSeconds=3\r\nAudioThreshold=0.1\r\n";

        static Harness Make(string mode, bool auto = true, string extra = "") =>
            new(Base + "LastMode=" + mode + "\r\nAutoMode=" + (auto ? "1" : "0") + "\r\n" + extra);

        [Fact]
        public void 啟動後5秒內不觸發自動切換()
        {
            var h = Make("away");

            h.Run(4, () => h.Signals.AudioPeak = 0.5f);
            Assert.Empty(h.Switcher.Started);

            h.Run(1);
            Assert.Single(h.Switcher.Started);
        }

        [Fact]
        public void 切換執行期間不觸發自動切換()
        {
            var h = Make("home");
            h.RunUntilSwitch(10 * 60);
            Assert.True(h.App.IsSwitching);

            h.Run(30 * 60, () => h.Signals.AudioPeak = 0.5f);   // 切換一直沒完成

            Assert.Single(h.Switcher.Started);
        }

        [Fact]
        public void 切換完成後10秒緩衝期內不觸發自動切換()
        {
            var h = Make("home");
            h.RunUntilSwitch(10 * 60);
            h.FinishSwitch();
            Assert.False(h.App.IsSwitching);

            h.Signals.AudioPeak = 0.5f;
            h.Run(9);
            Assert.Single(h.Switcher.Started);

            h.Run(1);
            Assert.Equal(new[] { Mode.Away, Mode.Home }, h.Switcher.Started.ConvertAll(s => s.Target));
        }

        [Fact]
        public void 切換完成後閒置計時重新開始()
        {
            var h = Make("away");
            h.Run(10);
            h.Signals.MoveMouse();
            h.Run(1);
            h.Run(60);              // 切換花了 60 秒
            h.FinishSwitch();

            Assert.Equal(5 * 60, h.RunUntilSwitch(10 * 60));
        }

        [Fact]
        public void 緩衝期中的滑鼠動作不算使用()
        {
            var h = Make("home");
            h.RunUntilSwitch(10 * 60);
            h.FinishSwitch();

            h.Run(9, () => h.Signals.MoveMouse());
            Assert.Single(h.Switcher.Started);

            h.Run(5);
            Assert.Single(h.Switcher.Started);
        }

        [Fact]
        public void 切換失敗時跳出錯誤通知()
        {
            var h = Make("home");
            h.RunUntilSwitch(10 * 60);

            h.FinishSwitch("bat 執行超過 60 秒沒有結束，已強制停止。");

            Assert.Contains(("bat 執行超過 60 秒沒有結束，已強制停止。", NotificationKind.Error), h.Notifier.Shown);
        }

        [Fact]
        public void 自動切換關閉時閒置也不切換()
        {
            var h = Make("home", auto: false);

            Assert.Equal(-1, h.RunUntilSwitch(30 * 60));
        }

        [Fact]
        public void 自動切換關閉時偵測到使用也不切換()
        {
            var h = Make("away", auto: false);

            Assert.Equal(-1, h.RunUntilSwitch(60, () => { h.Signals.AudioPeak = 0.5f; h.Signals.MoveMouse(); }));
        }

        [Fact]
        public void 自動切換時依設定跳出通知()
        {
            var on = Make("home");
            on.RunUntilSwitch(10 * 60);
            Assert.Contains(("閒置 5 分鐘，已自動切換成省電模式。", NotificationKind.Info), on.Notifier.Shown);

            var off = Make("home", extra: "ShowNotifications=0\r\n");
            off.RunUntilSwitch(10 * 60);
            Assert.DoesNotContain(off.Notifier.Shown, n => n.Text.Contains("已自動切換"));
        }

        [Fact]
        public void 找不到bat時不切換並暫停自動切換1分鐘()
        {
            var h = Make("home", extra: "BatPath=C:\\missing.bat\r\n");
            h.Switcher.Ready = false;

            Assert.Equal(-1, h.RunUntilSwitch(5 * 60));
            Assert.Contains(h.Notifier.Shown, n => n.Kind == NotificationKind.Warning && n.Text.Contains("C:\\missing.bat"));
            Assert.Equal(Mode.Home, h.App.CurrentMode);

            h.Switcher.Ready = true;
            Assert.Equal(60, h.RunUntilSwitch(5 * 60));
        }

        [Fact]
        public void 找不到bat後滑鼠鉤子也忽略暫停期間的輸入()
        {
            var h = Make("away");
            h.Switcher.Ready = false;
            h.Run(10);
            h.Run(1, () => h.Signals.MoveMouse());   // 想切回遊玩模式，但找不到 bat

            Assert.True(h.Signals.Policy.Install);
            Assert.Equal(h.Clock.NowMs + 60_000, h.Signals.Policy.IgnoreUntilMs);
        }

        // ---- 手動切換 ----

        [Fact]
        public void 手動切換到省電模式並關閉自動切換()
        {
            var h = Make("home");

            h.App.ManualSwitch(Mode.Away, autoSwitch: false);
            h.FinishSwitch();

            Assert.Equal(Mode.Away, Assert.Single(h.Switcher.Started).Target);
            Assert.False(h.App.Config.AutoSwitch);
            Assert.Equal(-1, h.RunUntilSwitch(60, () => h.Signals.MoveMouse()));
        }

        [Fact]
        public void 手動切換到省電模式並開啟自動切換後動到滑鼠會切回遊玩模式()
        {
            var h = Make("home", auto: false);

            h.App.ManualSwitch(Mode.Away, autoSwitch: true);
            h.FinishSwitch();
            h.Run(10);
            h.Signals.MoveMouse();
            h.Run(1);

            Assert.Equal(new[] { Mode.Away, Mode.Home }, h.Switcher.Started.ConvertAll(s => s.Target));
        }

        [Fact]
        public void 手動切換不跳出自動切換通知()
        {
            var h = Make("home");

            h.App.ManualSwitch(Mode.Away, autoSwitch: false);

            Assert.DoesNotContain(h.Notifier.Shown, n => n.Text.Contains("已自動切換"));
        }

        [Fact]
        public void 切換中再次手動切換會被拒絕()
        {
            var h = Make("home");
            h.App.ManualSwitch(Mode.Away, autoSwitch: false);

            h.App.ManualSwitch(Mode.Home, autoSwitch: true);

            Assert.Single(h.Switcher.Started);
            Assert.Contains(("正在切換中，請稍候。", NotificationKind.Info), h.Notifier.Shown);
            Assert.False(h.App.Config.AutoSwitch);
        }

        [Fact]
        public void 開啟自動切換後3秒內不觸發()
        {
            var h = Make("away", auto: false);
            h.Run(10);

            h.App.SetAutoSwitch(true);
            h.Run(2, () => h.Signals.MoveMouse());
            Assert.Empty(h.Switcher.Started);

            h.Run(1, () => h.Signals.MoveMouse());
            Assert.Single(h.Switcher.Started);
        }

        // ---- 滑鼠鉤子 ----

        [Fact]
        public void 只在自動切換開啟且處於省電模式時要求滑鼠鉤子()
        {
            var home = Make("home");
            home.Run(1);
            Assert.False(home.Signals.Policy.Install);

            var awayManual = Make("away", auto: false);
            awayManual.Run(1);
            Assert.False(awayManual.Signals.Policy.Install);

            var away = Make("away", extra: "MouseMoveThreshold=25\r\nIgnoreRemoteInput=0\r\n");
            away.Run(1);
            Assert.True(away.Signals.Policy.Install);
            Assert.Equal(25, away.Signals.Policy.MoveThreshold);
            Assert.False(away.Signals.Policy.IgnoreRemoteInput);
        }

        [Fact]
        public void 切換執行期間移除滑鼠鉤子()
        {
            var h = Make("away");
            h.Run(10);
            h.Signals.MoveMouse();
            h.Run(1);   // 開始切換到遊玩模式

            Assert.True(h.App.IsSwitching);
            Assert.False(h.Signals.Policy.Install);
        }
    }
}
