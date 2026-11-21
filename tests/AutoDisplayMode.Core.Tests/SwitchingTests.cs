using System.Linq;
using AutoDisplayMode.Core;
using Xunit;

namespace AutoDisplayMode.Core.Tests
{
    /// <summary>切換的時序：緩衝期、切換中、失敗後暫停、自動切換開關、鉤子。</summary>
    public class SwitchingTests
    {
        static Harness Make(int mode, bool auto = true, params string[] extra) =>
            Harness.PlayAndSave(mode, new[] { "AutoSwitch=" + (auto ? "1" : "0") }.Concat(extra).ToArray());

        [Fact]
        public void 啟動後5秒內不觸發自動切換()
        {
            var h = Make(Harness.Save);

            h.Run(4, () => h.Signals.AudioPeak = 0.5f);
            Assert.Empty(h.Monitors.Applied);

            h.Run(1);
            Assert.Single(h.Monitors.Applied);
        }

        [Fact]
        public void 啟動時不套用任何模式()
        {
            var h = Make(Harness.Play);

            Assert.Empty(h.Monitors.Applied);
        }

        [Fact]
        public void 切換執行期間不觸發自動切換()
        {
            var h = Make(Harness.Play);
            h.RunUntilSwitch(10 * 60);
            Assert.True(h.App.IsSwitching);

            h.Run(30 * 60, () => h.Signals.AudioPeak = 0.5f);   // 切換一直沒完成

            Assert.Single(h.Monitors.Applied);
        }

        [Fact]
        public void 切換完成後10秒緩衝期內不觸發自動切換()
        {
            var h = Make(Harness.Play);
            h.RunUntilSwitch(10 * 60);
            h.FinishSwitch();
            Assert.False(h.App.IsSwitching);

            h.Signals.AudioPeak = 0.5f;
            h.Run(9);
            Assert.Single(h.Monitors.Applied);

            h.Run(1);
            Assert.Equal(new[] { "省電", "遊玩" }, h.Monitors.AppliedTargets);
        }

        [Fact]
        public void 切換完成後閒置計時重新開始()
        {
            var h = Make(Harness.Save);
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
            var h = Make(Harness.Play);
            h.RunUntilSwitch(10 * 60);
            h.FinishSwitch();

            h.Run(9, () => h.Signals.MoveMouse());
            Assert.Single(h.Monitors.Applied);

            h.Run(5);
            Assert.Single(h.Monitors.Applied);
        }

        [Fact]
        public void 自動切換套用失敗時維持原模式並暫停自動切換1分鐘()
        {
            var h = Make(Harness.Play);
            h.RunUntilSwitch(10 * 60);

            h.FinishSwitch("SetDisplayConfig 失敗");

            Assert.Equal(1, h.App.Config.CurrentModeId);
            Assert.Contains(h.Notifier.Shown, n => n.Kind == NotificationKind.Error && n.Text.Contains("SetDisplayConfig 失敗"));
            Assert.DoesNotContain(h.Notifier.Shown, n => n.Text.Contains("已自動切換"));
            Assert.Equal(60, h.RunUntilSwitch(5 * 60));   // 仍是閒置，暫停結束後再試一次
        }

        [Fact]
        public void 拒絕套用時不切換並暫停自動切換1分鐘()
        {
            // 「省電」模式記錄的螢幕沒接上、接上的螢幕依設定關閉：結果沒有任何螢幕開著
            var h = new Harness(Harness.Cfg(
                "[General]", "AutoSwitch=1", "CurrentMode=1",
                "[Switch.1]", "From=1", "Condition=Idle", "IdleTime=5", "To=2",
                "[Mode.1]", "Name=遊玩",
                "[Mode.2]", "Name=省電", "UnlistedMonitors=Off",
                "[Mode.2.Monitor.1]", "Monitor=ACR/0833/SN:X", "Enabled=1", "Primary=1"));

            Assert.Equal(-1, h.RunUntilSwitch(5 * 60));
            Assert.Single(h.Notifier.Shown, n => n.Kind == NotificationKind.Warning);

            h.Run(59);
            Assert.Single(h.Notifier.Shown, n => n.Kind == NotificationKind.Warning);
            h.Run(1);
            Assert.Equal(2, h.Notifier.Shown.FindAll(n => n.Kind == NotificationKind.Warning).Count);
            Assert.Empty(h.Monitors.Applied);
        }

        [Fact]
        public void 套用失敗後鉤子也忽略暫停期間的輸入()
        {
            var h = Make(Harness.Save);
            h.Run(10);
            h.Run(1, () => h.Signals.MoveMouse());   // 開始切換到「遊玩」
            h.FinishSwitch("失敗");

            Assert.True(h.Signals.Policy.Install);
            Assert.Equal(h.Clock.NowMs + 60_000, h.Signals.Policy.IgnoreUntilMs);
        }

        [Fact]
        public void 自動切換關閉時閒置也不切換()
        {
            var h = Make(Harness.Play, auto: false);

            Assert.Equal(-1, h.RunUntilSwitch(30 * 60));
        }

        [Fact]
        public void 自動切換關閉時偵測到使用也不切換()
        {
            var h = Make(Harness.Save, auto: false);

            Assert.Equal(-1, h.RunUntilSwitch(60, () => { h.Signals.AudioPeak = 0.5f; h.Signals.MoveMouse(); }));
        }

        [Fact]
        public void 自動切換成功時依設定跳出通知()
        {
            var on = Make(Harness.Play);
            on.RunUntilSwitch(10 * 60);
            Assert.DoesNotContain(on.Notifier.Shown, n => n.Text.Contains("已自動切換"));   // 套用完成才通知
            on.FinishSwitch();
            Assert.Contains(("閒置 5 分鐘，已自動切換到「省電」。", NotificationKind.Info), on.Notifier.Shown);

            var off = Make(Harness.Play, true, "ShowNotifications=0");
            off.RunUntilSwitch(10 * 60);
            off.FinishSwitch();
            Assert.DoesNotContain(off.Notifier.Shown, n => n.Text.Contains("已自動切換"));
        }

        [Fact]
        public void 開啟自動切換後3秒內不觸發()
        {
            var h = Make(Harness.Save, auto: false);
            h.Run(10);

            h.App.SetAutoSwitch(true);
            h.Run(2, () => h.Signals.MoveMouse());
            Assert.Empty(h.Monitors.Applied);

            h.Run(1, () => h.Signals.MoveMouse());
            Assert.Single(h.Monitors.Applied);
        }

        // ---- 鉤子 ----

        [Fact]
        public void 自動切換關閉時不安裝鉤子()
        {
            var h = Make(Harness.Save, auto: false);
            h.Run(1);
            Assert.False(h.Signals.Policy.Install);

            h.App.SetAutoSwitch(true);
            Assert.True(h.Signals.Policy.Install);

            h.App.SetAutoSwitch(false);
            Assert.False(h.Signals.Policy.Install);
        }

        [Fact]
        public void 切換執行期間移除鉤子()
        {
            var h = Make(Harness.Save);
            h.Run(10);
            h.Signals.MoveMouse();
            h.Run(1);   // 開始切換到「遊玩」

            Assert.True(h.App.IsSwitching);
            Assert.False(h.Signals.Policy.Install);
        }
    }
}
