using System.Linq;
using AutoDisplayMode.Core;
using Xunit;
using static AutoDisplayMode.Core.Tests.Harness;

namespace AutoDisplayMode.Core.Tests
{
    /// <summary>
    /// 分層復原（ADR-0006）：程式結束前先切回復原模式、守護程式的啟動與結束、心跳，
    /// 以及守護程式接手時的復原。
    /// </summary>
    public class LayeredRecoveryTests
    {
        /// <summary>手動切到「潛行」並完成套用。</summary>
        static Harness InRemote(params string[] extra)
        {
            var h = WithRemote(Play, extra);
            h.App.ManualSwitch(Remote);
            h.FinishSwitch();
            return h;
        }

        /// <summary>完成離開潛行模式的切換：開啟螢幕的套用、移除虛擬螢幕後的等待，以及重新套用。</summary>
        static void FinishLeaving(Harness h)
        {
            h.FinishSwitch();
            h.Run(2);
            h.FinishSwitch();
        }

        // ---- 程式自己結束時 ----

        [Fact]
        public void 處於潛行模式時結束程式_先切回復原模式_完成後才可以結束()
        {
            var h = InRemote("RestoreMode=2");

            h.App.PrepareExit();

            Assert.False(h.App.ReadyToExit);
            Assert.Equal("省電", h.Monitors.AppliedTargets.Last());
            h.FinishSwitch();
            Assert.False(h.App.ReadyToExit);   // 移除虛擬螢幕後還要等 Windows 改完配置、重新套用
            h.Run(2);
            h.FinishSwitch();

            Assert.True(h.App.ReadyToExit);
            Assert.Equal(Save, h.App.Config.CurrentModeId);
            Assert.Contains("CurrentMode=2\r\n", h.Store.Text);
            Assert.Equal(1, h.Virtual.Removes);
        }

        [Fact]
        public void 處於一般模式時結束程式_不套用_立即可以結束()
        {
            var h = WithRemote(Play);

            h.App.PrepareExit();

            Assert.True(h.App.ReadyToExit);
            Assert.Empty(h.Monitors.Applied);
        }

        [Fact]
        public void 正在進入潛行模式時結束程式_等切換完成再切回復原模式()
        {
            var h = WithRemote(Play);
            h.App.ManualSwitch(Remote);

            h.App.PrepareExit();
            Assert.False(h.App.ReadyToExit);
            Assert.Single(h.Monitors.Applied);

            h.FinishSwitch();   // 進入潛行模式完成
            Assert.Equal("遊玩", h.Monitors.AppliedTargets.Last());
            FinishLeaving(h);

            Assert.True(h.App.ReadyToExit);
            Assert.Equal(Play, h.App.Config.CurrentModeId);
        }

        [Fact]
        public void 結束中不再自動或手動切換()
        {
            var h = InRemote();
            h.App.PrepareExit();
            h.FinishSwitch();   // 移除虛擬螢幕，等待中
            int applied = h.Monitors.Applied.Count;

            h.App.ManualSwitch(Remote);
            Assert.Equal(applied, h.Monitors.Applied.Count);
            Assert.False(h.Signals.Policy.Install);   // 不掛鉤子

            h.Run(2);
            h.FinishSwitch();
            h.Run(30, () => h.Signals.MoveMouse());
            Assert.Equal(applied + 1, h.Monitors.Applied.Count);   // 只有切回復原模式的重新套用
            Assert.Equal(1, h.Virtual.Creates);
        }

        [Fact]
        public void 結束前切回復原模式失敗時照樣結束_並移除虛擬螢幕()
        {
            var h = InRemote();
            h.Monitors.Fail = true;

            h.App.PrepareExit();

            Assert.True(h.App.ReadyToExit);
            Assert.Equal(1, h.Virtual.Removes);
        }

        [Fact]
        public void 結束前切回復原模式的套用失敗時照樣結束()
        {
            var h = InRemote();

            h.App.PrepareExit();
            h.FinishSwitch("SetDisplayConfig 失敗");

            Assert.True(h.App.ReadyToExit);
            Assert.Equal(1, h.Virtual.Removes);
        }

        // ---- 守護程式的啟動與結束 ----

        [Fact]
        public void 建立虛擬螢幕後啟動守護程式_每秒心跳_經由切換離開潛行模式時結束守護()
        {
            var h = WithRemote(Play);

            h.App.ManualSwitch(Remote);
            Assert.True(h.Guard.Armed);
            h.FinishSwitch();
            h.Run(3);
            Assert.True(h.Guard.Beats >= 3);

            h.App.ManualSwitch(Play);
            Assert.True(h.Guard.Armed);   // 虛擬螢幕還在（先依一般模式開啟螢幕）
            FinishLeaving(h);

            Assert.False(h.Guard.Armed);
            Assert.Equal(new[] { "守護", "結束守護" }, h.Guard.Log);
            int beats = h.Guard.Beats;
            h.Run(3);
            Assert.Equal(beats, h.Guard.Beats);
        }

        [Fact]
        public void 潛行模式之間切換時沿用守護程式()
        {
            var h = InRemote();

            h.App.ManualSwitch(Remote4K);
            h.FinishSwitch();

            Assert.Equal(new[] { "守護" }, h.Guard.Log);
        }

        [Fact]
        public void 驅動不可用時不啟動守護程式()
        {
            var h = WithRemote(Play);
            h.Virtual.CreateError = "找不到虛擬螢幕驅動";

            h.App.ManualSwitch(Remote);

            Assert.Empty(h.Guard.Log);
        }

        [Fact]
        public void 進入潛行模式的套用失敗時移除虛擬螢幕並結束守護()
        {
            var h = WithRemote(Play);
            h.App.ManualSwitch(Remote);

            h.FinishSwitch("無法開啟虛擬螢幕");

            Assert.False(h.Guard.Armed);
        }

        [Fact]
        public void 虛擬螢幕意外消失時結束守護()
        {
            var h = InRemote();
            h.Run(11);   // 切換後的緩衝期

            h.Virtual.Vanish();
            h.Run(1);

            Assert.False(h.Guard.Armed);
        }

        [Fact]
        public void 結束程式時結束守護()
        {
            var h = InRemote();

            h.App.PrepareExit();
            FinishLeaving(h);

            Assert.False(h.Guard.Armed);
        }

        // ---- 守護程式接手 ----

        [Fact]
        public void 主程式從卡住中恢復時若守護程式已接手_切回復原模式並說明_不會又切回潛行模式()
        {
            var h = InRemote("RestoreMode=2");
            h.Run(11);

            h.Guard.TakeOver = GuardTakeOver.Done;
            h.Virtual.Vanish();   // 守護程式已移除虛擬螢幕
            h.Run(1);

            Assert.False(h.Guard.Armed);
            h.Run(2);
            Assert.Equal("省電", h.Monitors.AppliedTargets.Last());
            h.FinishSwitch();

            Assert.Equal(Save, h.App.Config.CurrentModeId);
            Assert.False(h.App.InRemoteMode);
            Assert.Equal(1, h.Virtual.Creates);
            var (text, kind) = h.Notifier.Shown.Last();
            Assert.Equal(NotificationKind.Warning, kind);
            Assert.Equal("Auto Display Mode 有一段時間沒有回應，守護程式已切回復原模式「省電」。", text);
        }

        [Fact]
        public void 守護程式接手時虛擬螢幕還在_主程式不再送心跳()
        {
            var h = InRemote();
            h.Run(11);

            h.Guard.TakeOver = GuardTakeOver.Done;
            h.Run(1);
            int pings = h.Virtual.Pings;
            h.Run(3);

            Assert.Equal(pings, h.Virtual.Pings);
        }

        [Fact]
        public void 守護程式還在切回復原模式時_主程式等它完成才切回_期間不能手動切換()
        {
            var h = InRemote("RestoreMode=2");
            h.Run(11);
            int applied = h.Monitors.Applied.Count;

            h.Guard.TakeOver = GuardTakeOver.InProgress;
            h.Virtual.Vanish();
            h.Run(5);
            Assert.Equal(applied, h.Monitors.Applied.Count);   // 不和守護程式同時改配置
            h.App.ManualSwitch(Remote4K);
            Assert.Equal(applied, h.Monitors.Applied.Count);
            Assert.Equal(1, h.Virtual.Creates);
            Assert.Contains("守護程式正在切回復原模式", h.Notifier.Shown.Last().Text);

            h.Guard.TakeOver = GuardTakeOver.Done;
            h.Run(3);
            Assert.Equal("省電", h.Monitors.AppliedTargets.Last());
            h.FinishSwitch();
            Assert.Equal(Save, h.App.Config.CurrentModeId);
            Assert.StartsWith("Auto Display Mode 有一段時間沒有回應", h.Notifier.Shown.Last().Text);
        }

        [Fact]
        public void 守護程式一直沒完成時_主程式等滿上限後自己切回復原模式()
        {
            var h = InRemote("RestoreMode=2");
            h.Run(11);
            int applied = h.Monitors.Applied.Count;

            h.Guard.TakeOver = GuardTakeOver.InProgress;   // 例如守護程式在復原途中被結束
            h.Run(59);
            Assert.Equal(applied, h.Monitors.Applied.Count);
            h.Run(4);

            Assert.Equal("省電", h.Monitors.AppliedTargets.Last());
        }

        [Fact]
        public void 守護程式接手時正在結束程式_等它完成再切回復原模式後結束()
        {
            var h = InRemote("RestoreMode=2");
            h.Run(11);
            h.Guard.TakeOver = GuardTakeOver.InProgress;
            h.Run(1);
            int applied = h.Monitors.Applied.Count;

            h.App.PrepareExit();
            h.Run(3);
            Assert.Equal(applied, h.Monitors.Applied.Count);
            Assert.False(h.App.ReadyToExit);

            h.Guard.TakeOver = GuardTakeOver.Done;
            h.Run(1);
            Assert.Equal("省電", h.Monitors.AppliedTargets.Last());
            h.FinishSwitch();
            Assert.True(h.App.ReadyToExit);
        }

        // ---- 守護程式的判斷 ----

        [Theory]
        [InlineData(true, false, 0, GuardianVerdict.KeepWatching)]
        [InlineData(true, false, 6_000, GuardianVerdict.KeepWatching)]   // 開關螢幕時介面卡住幾秒不算
        [InlineData(true, false, 6_001, GuardianVerdict.MainHung)]
        [InlineData(true, true, 0, GuardianVerdict.MainExited)]
        [InlineData(false, true, 0, GuardianVerdict.Quit)]               // 正常結束：先結束守護再結束程式
        [InlineData(false, false, 60_000, GuardianVerdict.Quit)]         // 經由切換離開潛行模式
        public void 守護程式依守護狀態_主程式存活與心跳決定(bool armed, bool mainExited, long heartbeatAgeMs, GuardianVerdict expected)
        {
            Assert.Equal(expected, GuardianRules.Decide(armed, mainExited, heartbeatAgeMs));
        }

        /// <summary>守護程式中的協調者：cfg 的目前模式是「潛行」、復原模式是「省電」，虛擬螢幕（主程式留下的）還在。</summary>
        static Harness Guardian(int current = Remote, bool virtualExists = true) =>
            new Harness(WithRemote(current, "RestoreMode=2").Store.Text, start: false, virtualDisplay: v => v.Exists = virtualExists);

        [Fact]
        public void 守護程式接手_移除虛擬螢幕_等Windows改完配置_套用復原模式並更新cfg的目前模式後結束()
        {
            var h = Guardian();

            h.App.TakeOver(hung: false);

            Assert.Equal(new[] { "移除" }, h.Log);
            Assert.False(h.App.ReadyToExit);
            h.Run(2);
            Assert.Equal("省電", Assert.Single(h.Monitors.AppliedTargets));
            Assert.Null(h.Monitors.AppliedVirtual.Single());
            h.FinishSwitch();

            Assert.True(h.App.ReadyToExit);
            Assert.Contains("CurrentMode=2\r\n", h.Store.Text);
            Assert.Equal("Auto Display Mode 意外結束，守護程式已切回復原模式「省電」。", Assert.Single(h.Notifier.Shown).Text);
            Assert.Empty(h.Startup.Calls);   // 不是啟動：不同步開機啟動
        }

        [Fact]
        public void 守護程式因主程式沒有回應而接手時說明原因()
        {
            var h = Guardian();

            h.App.TakeOver(hung: true);
            h.Run(2);
            h.FinishSwitch();

            Assert.StartsWith("Auto Display Mode 沒有回應，守護程式已切回復原模式", Assert.Single(h.Notifier.Shown).Text);
        }

        [Fact]
        public void 守護程式接手時目前模式是一般模式也套用復原模式()
        {
            // 例如進入潛行模式途中閃退：cfg 的目前模式還沒改，但螢幕可能都關了
            var h = Guardian(current: Play);

            h.App.TakeOver(hung: false);
            h.Run(2);

            Assert.Equal("省電", Assert.Single(h.Monitors.AppliedTargets));
        }

        [Fact]
        public void 守護程式接手時虛擬螢幕已被驅動移除_仍等Windows改完配置再套用()
        {
            var h = Guardian(virtualExists: false);

            h.App.TakeOver(hung: false);

            Assert.Empty(h.Log);
            h.Run(2);
            Assert.Single(h.Monitors.Applied);
        }
    }
}
