using System.Linq;
using AutoDisplayMode.Core;
using Xunit;
using static AutoDisplayMode.Core.Tests.Harness;

namespace AutoDisplayMode.Core.Tests
{
    /// <summary>
    /// 設定視窗中的潛行模式與內建切換條件：新增、複製、刪除、只能改目標、目標失效、順序，
    /// 以及內建切換條件只認實體鍵盤滑鼠、一律強制。
    /// </summary>
    public class BuiltInConditionTests
    {
        static AppConfig Reread(Harness h) => new Harness(h.Store.Text).App.Config;

        // ---- 新增、複製 ----

        [Fact]
        public void 新增潛行模式時在切換條件最後加上內建切換條件_目標為回到上個配置()
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5));
            AppConfig draft = h.App.Config.Clone();

            ModeConfig remote = draft.AddRemoteMode();

            Assert.Equal(("潛行模式", ModeKind.Remote, VirtualDisplayModes.Default), (remote.Name, remote.Kind, remote.VirtualMode));
            SwitchCondition s = draft.SwitchConditions.Last();
            Assert.Equal(2, draft.SwitchConditions.Count);
            Assert.Equal((true, remote.Id, Sw.Previous, true), (s.BuiltIn, s.FromModeId, s.ToModeId, s.Forced));
            Assert.Equal((SwitchConditionKind.UsageDetected, UsageSources.MouseKeyboard), (s.Kind, s.Sources));
            Assert.Equal("潛行模式 2", draft.AddRemoteMode().Name);
        }

        [Fact]
        public void 新增的潛行模式套用後寫入cfg並可以讀回()
        {
            var h = Modes(Play);
            AppConfig draft = h.App.Config.Clone();
            ModeConfig remote = draft.AddRemoteMode("遠端用");
            remote.VirtualMode = new VideoMode(2560, 1440, 120);

            Assert.Null(h.App.ApplySettings(draft));

            AppConfig reread = Reread(h);
            ModeConfig m = reread.FindMode(4)!;
            Assert.Equal(("遠端用", ModeKind.Remote, new VideoMode(2560, 1440, 120)), (m.Name, m.Kind, m.VirtualMode));
            Assert.Empty(m.Monitors);
            Assert.Equal(SwitchCondition.BuiltInFor(4, Sw.Previous), Assert.Single(reread.SwitchConditions));
        }

        [Fact]
        public void 複製潛行模式得到潛行模式_並有自己的內建切換條件_目標為回到上個配置()
        {
            var h = WithRemote(Play);
            AppConfig draft = h.App.Config.Clone();
            int i = draft.SwitchConditions.FindIndex(s => s.FromModeId == Remote);
            draft.SwitchConditions[i] = draft.SwitchConditions[i] with { ToModeId = Save };
            draft.FindMode(Remote)!.VirtualMode = new VideoMode(1280, 720, 30);

            ModeConfig copy = draft.DuplicateMode(Remote);

            Assert.Equal(("潛行 的複本", ModeKind.Remote, new VideoMode(1280, 720, 30)), (copy.Name, copy.Kind, copy.VirtualMode));
            Assert.Equal(SwitchCondition.BuiltInFor(copy.Id, Sw.Previous), draft.SwitchConditions.Last());
            Assert.Null(h.App.ApplySettings(draft));
        }

        [Fact]
        public void 複製一般模式得到一般模式_不加切換條件()
        {
            AppConfig draft = WithRemote(Play).App.Config.Clone();
            int count = draft.SwitchConditions.Count;

            ModeConfig copy = draft.DuplicateMode(Save);

            Assert.Equal(ModeKind.Normal, copy.Kind);
            Assert.Equal(count, draft.SwitchConditions.Count);
        }

        [Fact]
        public void 新增的一般模式是一般模式()
        {
            AppConfig draft = WithRemote(Play).App.Config.Clone();

            Assert.False(draft.AddMode().IsRemote);
        }

        // ---- 刪除 ----

        [Fact]
        public void 內建切換條件不能刪除()
        {
            AppConfig draft = WithRemote(Play, Sw.Idle(Play, Save, 5)).App.Config.Clone();

            Assert.Null(draft.SwitchConditionRemovalBlocker(0));
            Assert.Contains("內建切換條件不能刪除", draft.SwitchConditionRemovalBlocker(1));
        }

        [Fact]
        public void 刪除潛行模式時一起刪除它的內建切換條件()
        {
            var h = WithRemote(Play, Sw.Idle(Play, Remote, 30));
            AppConfig draft = h.App.Config.Clone();

            var removed = draft.RemoveMode(Remote);

            Assert.Equal(2, removed.Count);   // 目標是它的一般切換條件，以及它自己的內建切換條件
            Assert.Null(h.App.ApplySettings(draft));
            AppConfig reread = Reread(h);
            Assert.Equal(new[] { (4, 1) }, reread.SwitchConditions.Select(s => (s.FromModeId, s.ToModeId)));   // 「潛行 4K」重新編號為 4
        }

        [Fact]
        public void 刪除內建切換條件的目標模式時_目標改成第一個一般模式並在套用時提示()
        {
            var h = WithRemote(Save);
            AppConfig draft = h.App.Config.Clone();
            Assert.Equal(2, draft.BuiltInConditionsRetargetedBy(Play).Count);

            var removed = draft.RemoveMode(Play);

            Assert.Empty(removed);
            Assert.All(draft.SwitchConditions, s => Assert.Equal(Save, s.ToModeId));
            Assert.Null(h.App.ApplySettings(draft));
            string note = h.Notifier.Shown.Last().Text;
            Assert.Contains("「潛行」的內建切換條件的目標模式「遊玩」已刪除，改成「省電」。", note);
            Assert.Contains("「潛行 4K」的內建切換條件的目標模式「遊玩」已刪除，改成「省電」。", note);
        }

        [Fact]
        public void 最後一個一般模式不能刪除()
        {
            var h = WithRemote(Play);
            AppConfig draft = h.App.Config.Clone();
            draft.RemoveMode(Save);
            draft.RemoveMode(AllOff);

            Assert.Contains("至少要保留一個一般模式", draft.ModeRemovalBlocker(Play, currentModeId: Remote));
            Assert.Null(draft.ModeRemovalBlocker(Remote4K, currentModeId: Play));
        }

        // ---- 只能改目標 ----

        [Fact]
        public void 內建切換條件的目標只能是一般模式()
        {
            var h = WithRemote(Play);
            AppConfig draft = h.App.Config.Clone();
            int i = draft.SwitchConditions.FindIndex(s => s.FromModeId == Remote);

            draft.SwitchConditions[i] = draft.SwitchConditions[i] with { ToModeId = Remote4K };
            Assert.Contains("內建切換條件的目標模式必須是一般模式", h.App.ApplySettings(draft));

            draft.SwitchConditions[i] = draft.SwitchConditions[i] with { ToModeId = AllOff };
            Assert.Null(h.App.ApplySettings(draft));
            Assert.Equal(AllOff, Reread(h).BuiltInConditionOf(Remote)!.ToModeId);
        }

        [Fact]
        public void 內建切換條件的條件_使用來源與強制都鎖定()
        {
            var h = WithRemote(Play);
            AppConfig draft = h.App.Config.Clone();
            int i = draft.SwitchConditions.FindIndex(s => s.FromModeId == Remote);
            SwitchCondition builtIn = draft.SwitchConditions[i];

            foreach (SwitchCondition changed in new[]
                     {
                         builtIn with { Forced = false },
                         builtIn with { Sources = UsageSources.MouseKeyboard | UsageSources.Audio },
                         builtIn with { Kind = SwitchConditionKind.Idle },
                     })
            {
                draft.SwitchConditions[i] = changed;
                Assert.Contains("內建切換條件的條件固定", h.App.ApplySettings(draft));
            }
        }

        [Fact]
        public void 潛行模式必須剛好有一條內建切換條件()
        {
            var h = WithRemote(Play);
            AppConfig draft = h.App.Config.Clone();
            draft.SwitchConditions.RemoveAll(s => s.FromModeId == Remote);

            Assert.Contains("潛行模式「潛行」必須剛好有一條內建切換條件", h.App.ApplySettings(draft));
        }

        [Fact]
        public void 內建切換條件可以調整順序()
        {
            var h = WithRemote(Play, Sw.Idle(Play, Save, 5));
            AppConfig draft = h.App.Config.Clone();

            draft.MoveSwitchCondition(2, 0);   // 「潛行 4K」的內建切換條件移到最前面
            Assert.Null(h.App.ApplySettings(draft));

            Assert.Equal(new[] { Remote4K, Play, Remote }, Reread(h).SwitchConditions.Select(s => s.FromModeId));
        }

        [Fact]
        public void 一般切換條件可以以潛行模式為來源或目標()
        {
            var h = WithRemote(Play, Sw.Idle(Play, Remote, 30), Sw.Idle(Remote, Save, 60, forced: true));

            Assert.Equal(4, h.App.Config.SwitchConditions.Count);

            Assert.Equal(30 * 60, h.RunUntilSwitch(31 * 60));
            Assert.Equal("潛行", h.Monitors.AppliedTargets.Last());
        }

        // ---- 只認實體鍵盤滑鼠、一律強制 ----

        static Harness InRemote(params string[] extra)
        {
            var h = WithRemote(Play, extra);
            h.App.ManualSwitch(Remote);
            h.FinishSwitch();
            h.Run(10);   // 度過切換後的緩衝期
            h.Monitors.Applied.Clear();
            h.Monitors.AppliedTargets.Clear();
            return h;
        }

        [Fact]
        public void 處於潛行模式時動實體滑鼠就切回內建切換條件的目標()
        {
            var h = InRemote();

            h.Run(1, () => h.Signals.MoveMouse());

            Assert.Equal("遊玩", Assert.Single(h.Monitors.AppliedTargets));
            h.FinishSwitch();
            h.Run(2);
            h.FinishSwitch();
            Assert.Equal(Play, h.App.Config.CurrentModeId);
            Assert.Equal("偵測到使用，已自動切換到「遊玩」。", h.Notifier.Shown.Last().Text);
        }

        [Fact]
        public void 遠端連線軟體送出的輸入不會離開潛行模式_即使不忽略遠端輸入()
        {
            var h = InRemote("IgnoreRemoteInput=0");

            h.Run(10, () => h.Signals.RemoteInput());

            Assert.Empty(h.Monitors.Applied);
        }

        [Fact]
        public void 不忽略遠端輸入時自訂的偵測到使用條件仍認遠端輸入_內建切換條件不認()
        {
            // 同一個潛行模式裡，內建切換條件排在自訂條件前面：遠端輸入只讓後面的自訂條件成立
            var h = InRemote("IgnoreRemoteInput=0", BuiltIn(Remote, Play), Sw.Usage(Remote, Save));
            Assert.True(h.App.Config.SwitchConditions.First(s => s.FromModeId == Remote).BuiltIn);

            h.Run(1, () => h.Signals.RemoteInput());

            Assert.Equal("省電", Assert.Single(h.Monitors.AppliedTargets));
        }

        [Fact]
        public void 自動切換關閉_設定視窗開著時內建切換條件照樣生效()
        {
            var h = InRemote("AutoSwitch=0");
            h.App.OnSettingsWindowOpened();
            Assert.True(h.Signals.Policy.Install);   // 一定有鉤子

            h.Run(1, () => h.Signals.PressKey());

            Assert.Equal("遊玩", Assert.Single(h.Monitors.AppliedTargets));
        }

        [Fact]
        public void 狀態摘要中內建切換條件標示內建()
        {
            var h = InRemote();

            Assert.Contains("「潛行」偵測到使用（鍵盤滑鼠）→「遊玩」（內建）", h.App.StatusSummary);
        }
    }
}
