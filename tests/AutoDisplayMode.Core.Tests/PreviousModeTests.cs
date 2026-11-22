using System.Linq;
using AutoDisplayMode.Core;
using Xunit;
using static AutoDisplayMode.Core.Tests.Harness;

namespace AutoDisplayMode.Core.Tests
{
    /// <summary>
    /// 目標模式「回到上個配置」：切回上一個一般模式（最近一次所在、而且不是目前模式的一般模式）。
    /// 記錄、備案、重新編號、cfg 讀寫，以及新增或複製潛行模式時的預設值。
    /// </summary>
    public class PreviousModeTests
    {
        static AppConfig Reread(Harness h) => new Harness(h.Store.Text).App.Config;

        /// <summary>手動切換並完成（離開潛行模式時包括移除虛擬螢幕後的等待與重新套用）。</summary>
        static void Switch(Harness h, int modeId)
        {
            h.App.ManualSwitch(modeId);
            FinishAll(h);
        }

        static void FinishAll(Harness h)
        {
            h.FinishSwitch();
            if (h.App.IsSwitching)
            {
                h.Run(2);
                h.FinishSwitch();
            }
        }

        /// <summary>從「遊玩」開始，三個一般模式加上兩個潛行模式；潛行模式的內建切換條件都是「回到上個配置」。</summary>
        static Harness Start(params string[] extra) =>
            WithRemote(Play, extra.Concat(new[] { BuiltIn(Remote, Sw.Previous), BuiltIn(Remote4K, Sw.Previous) }).ToArray());

        /// <summary>一直 tick 到開始切換，完成那次切換，回傳切換到的模式名稱；沒有切換時回傳 null。</summary>
        static string? RunUntilSwitched(Harness h, int maxSeconds, System.Action? eachSecond = null)
        {
            if (h.RunUntilSwitch(maxSeconds, eachSecond) < 0) return null;
            string? name = h.Monitors.AppliedTargets.Last();
            FinishAll(h);
            return name;
        }

        // ---- 一般模式出發 ----

        [Fact]
        public void 從一般模式回到上個配置_切回進入目前模式之前的一般模式()
        {
            var h = Start(Sw.Idle(Save, Sw.Previous, 5));
            Switch(h, Save);

            Assert.Equal("遊玩", RunUntilSwitched(h, 6 * 60));
            Assert.Equal(Play, h.App.Config.CurrentModeId);
        }

        [Fact]
        public void 經過潛行模式回到原本的一般模式後_上一個一般模式是更早的那個()
        {
            var h = Start(Sw.Idle(Save, Sw.Previous, 5));
            Switch(h, Save);
            Switch(h, Remote);
            Switch(h, Save);   // 遊玩 → 省電 → 潛行 → 省電

            Assert.Equal("遊玩", RunUntilSwitched(h, 6 * 60));
        }

        [Fact]
        public void 上一個一般模式不存在時用順序第一個一般模式()
        {
            var h = Modes(Save, Sw.Idle(Save, Sw.Previous, 5));   // 還沒切換過

            Assert.Equal("遊玩", RunUntilSwitched(h, 6 * 60));
        }

        [Fact]
        public void 上一個一般模式被刪除時用順序第一個一般模式()
        {
            var h = Start(Sw.Idle(Play, Sw.Previous, 5));
            Switch(h, Save);
            Switch(h, Play);   // 上一個一般模式是「省電」
            AppConfig draft = h.App.Config.Clone();
            draft.RemoveMode(Save);
            draft.MoveMode(draft.Modes.FindIndex(m => m.Name == "全關"), 0);
            Assert.Null(h.App.ApplySettings(draft));

            Assert.Equal("全關", RunUntilSwitched(h, 6 * 60));
        }

        [Fact]
        public void 刪除復原模式時_改用的第一個一般模式不會同時是上一個一般模式()
        {
            var h = Start();
            Switch(h, Save);
            Switch(h, Play);
            Switch(h, AllOff);   // 復原模式「全關」，之前是「遊玩」
            Switch(h, Remote);
            AppConfig draft = h.App.Config.Clone();

            draft.RemoveMode(AllOff);   // 復原模式改用第一個一般模式「遊玩」
            Assert.Null(h.App.ApplySettings(draft));

            Assert.Equal(Play, h.App.Config.RestoreModeId);
            Assert.Equal(0, h.App.Config.PreviousModeId);
        }

        [Fact]
        public void 內建切換條件的目標被刪除時改成回到上個配置_不通知自動改成第一個一般模式()
        {
            var h = WithRemote(Play);
            AppConfig draft = h.App.Config.Clone();
            int i = draft.SwitchConditions.FindIndex(s => s.BuiltIn && s.FromModeId == Remote);
            draft.SwitchConditions[i] = draft.SwitchConditions[i] with { ToModeId = Save };
            Assert.Null(h.App.ApplySettings(draft));
            int shown = h.Notifier.Shown.Count;

            draft = h.App.Config.Clone();
            i = draft.SwitchConditions.FindIndex(s => s.BuiltIn && s.FromModeId == Remote);
            draft.SwitchConditions[i] = draft.SwitchConditions[i] with { ToModeId = Sw.Previous };
            draft.RemoveMode(Save);
            Assert.Null(h.App.ApplySettings(draft));

            Assert.Equal(shown, h.Notifier.Shown.Count);
        }

        [Fact]
        public void 備案就是目前模式時不切換()
        {
            var h = Modes(Play, Sw.Idle(Play, Sw.Previous, 5));   // 還沒切換過，第一個一般模式就是目前模式

            Assert.Null(RunUntilSwitched(h, 20 * 60));
        }

        // ---- 潛行模式出發 ----

        [Fact]
        public void 內建切換條件回到上個配置_回到進入潛行模式前的一般模式()
        {
            var h = Start();
            Switch(h, Save);
            Switch(h, Remote);
            h.Run(11);

            Assert.Equal("省電", RunUntilSwitched(h, 2, () => h.Signals.MoveMouse()));
            Assert.Equal(Save, h.App.Config.CurrentModeId);
        }

        [Fact]
        public void 潛行模式之間切換後_仍回到進入潛行模式前的一般模式()
        {
            var h = Start();
            Switch(h, Save);
            Switch(h, Remote);
            Switch(h, Remote4K);
            h.Run(11);

            Assert.Equal("省電", RunUntilSwitched(h, 2, () => h.Signals.MoveMouse()));
        }

        // ---- 記錄與 cfg ----

        [Fact]
        public void 復原模式之前的一般模式存進cfg_重新啟動後仍記得()
        {
            var h = Start();
            Switch(h, Save);
            Switch(h, AllOff);

            Assert.Contains("PreviousMode=2\r\n", h.Store.Text);
            Assert.Equal(Save, Reread(h).PreviousModeId);
        }

        [Fact]
        public void 重新套用同一個模式時不改變上一個一般模式()
        {
            var h = Start();
            Switch(h, Save);
            Switch(h, Save);

            Assert.Equal(Play, h.App.Config.PreviousModeId);
        }

        [Fact]
        public void 重新編號時上一個一般模式跟著改_回到上個配置不受影響()
        {
            var h = Start(Sw.Idle(Save, Sw.Previous, 5));
            Switch(h, Save);   // 上一個一般模式是「遊玩」（1）
            AppConfig draft = h.App.Config.Clone();

            draft.MoveMode(0, 2);   // 「遊玩」移到第三個
            Assert.Null(h.App.ApplySettings(draft));

            Assert.Equal("遊玩", h.App.Config.FindMode(h.App.Config.PreviousModeId)!.Name);
            Assert.Equal(3, h.App.Config.PreviousModeId);
            Assert.Equal(Sw.Previous, h.App.Config.SwitchConditions.Single(s => !s.BuiltIn).ToModeId);
            Assert.Equal(3, Reread(h).PreviousModeId);
        }

        [Fact]
        public void cfg讀寫回到上個配置()
        {
            var h = Start(Sw.Idle(Save, Sw.Previous, 5));

            string text = Format(h);
            Assert.Contains("To=Previous\r\n", text);
            AppConfig reread = new Harness(text).App.Config;
            Assert.Equal(Sw.Previous, reread.SwitchConditions.Single(s => !s.BuiltIn).ToModeId);
            Assert.All(reread.SwitchConditions.Where(s => s.BuiltIn), s => Assert.Equal(Sw.Previous, s.ToModeId));
        }

        /// <summary>讓協調者整份重寫 cfg，回傳寫出的內容。</summary>
        static string Format(Harness h)
        {
            h.App.SetAutoSwitch(h.App.Config.AutoSwitch);
            return h.Store.Text!;
        }

        [Fact]
        public void cfg中To寫負數時不是回到上個配置()
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5).Replace("To=2", "To=-1"));

            Assert.DoesNotContain(h.App.Config.SwitchConditions, s => s.ToPrevious);   // 目標模式不存在：讀取時略過
        }

        // ---- 驗證與描述 ----

        [Fact]
        public void 回到上個配置對任何來源模式都有效_內建切換條件也可以()
        {
            AppConfig c = Start(Sw.Idle(Play, Sw.Previous, 5), Sw.Usage(Remote, Sw.Previous)).App.Config;

            Assert.Empty(c.InvalidSwitchConditions());
            Assert.Empty(c.RepairBuiltInConditions());
        }

        [Fact]
        public void 描述寫出回到上個配置()
        {
            AppConfig c = Start(Sw.Idle(Play, Sw.Previous, 5)).App.Config;

            Assert.Equal("「遊玩」閒置 5 分鐘（鍵盤滑鼠）→ 回到上個配置", c.SwitchConditions[0].Describe(c));
            Assert.Equal("「潛行」偵測到使用（鍵盤滑鼠）→ 回到上個配置（內建）", c.BuiltInConditionOf(Remote)!.Describe(c));
        }

        [Fact]
        public void 複製潛行模式時內建切換條件的目標也是回到上個配置()
        {
            AppConfig draft = WithRemote(Play).App.Config.Clone();   // 「潛行」的內建切換條件目標是「遊玩」

            ModeConfig copy = draft.DuplicateMode(Remote);

            Assert.Equal(Sw.Previous, draft.BuiltInConditionOf(copy.Id)!.ToModeId);
        }
    }
}
