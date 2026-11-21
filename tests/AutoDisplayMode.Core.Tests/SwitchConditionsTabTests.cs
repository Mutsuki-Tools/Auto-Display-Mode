using System.Linq;
using AutoDisplayMode.Core;
using Xunit;
using static AutoDisplayMode.Core.Tests.Harness;

namespace AutoDisplayMode.Core.Tests
{
    /// <summary>設定視窗的切換條件分頁：在副本中新增、編輯、刪除、調整順序，套用後寫入 cfg 並立即生效。</summary>
    public class SwitchConditionsTabTests
    {
        static AppConfig Reread(Harness h) => new Harness(h.Store.Text).App.Config;

        // ---- 新增 ----

        [Fact]
        public void 新增的切換條件來源與目標不同_編輯後套用即寫入cfg並立即生效()
        {
            var h = Modes(Play);
            AppConfig draft = h.App.Config.Clone();

            SwitchCondition added = draft.AddSwitchCondition(Play);
            Assert.Equal(Play, added.FromModeId);
            Assert.NotEqual(Play, added.ToModeId);
            Assert.Null(added.Validate(draft));   // 預設就是有效的

            draft.SwitchConditions[0] = added with { IdleAmount = 3, ToModeId = AllOff };
            Assert.Null(h.App.ApplySettings(draft));

            Assert.Equal(3 * 60, h.RunUntilSwitch(10 * 60));
            Assert.Equal("全關", Assert.Single(h.Monitors.AppliedTargets));
            var reread = Assert.Single(Reread(h).SwitchConditions);
            Assert.Equal((Play, SwitchConditionKind.Idle, 3, AllOff), (reread.FromModeId, reread.Kind, reread.IdleAmount, reread.ToModeId));
        }

        [Fact]
        public void 只有一個模式時無法新增切換條件並說明原因()
        {
            var h = Modes(Play);
            AppConfig draft = h.App.Config.Clone();
            Assert.Null(draft.SwitchConditionAddBlocker());

            draft.RemoveMode(Save);
            draft.RemoveMode(AllOff);

            Assert.Equal("至少要有兩個模式才能新增切換條件（來源模式與目標模式不能相同）", draft.SwitchConditionAddBlocker());
        }

        // ---- 驗證 ----

        public static TheoryData<string, string> 無效的編輯 => new()
        {
            { "來源與目標相同", "第 2 條切換條件無效：來源模式與目標模式相同。" },
            { "未選使用來源", "第 2 條切換條件無效：未選任何使用來源。" },
            { "閒置零分鐘", "第 2 條切換條件無效：閒置時間必須是正整數。" },
            { "閒置負數", "第 2 條切換條件無效：閒置時間必須是正整數。" },
        };

        [Theory]
        [MemberData(nameof(無效的編輯))]
        public void 無效的切換條件無法套用並說明原因_設定與cfg都不變(string edit, string reason)
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5), Sw.Usage(Save, Play));
            string before = h.Store.Text!;
            AppConfig draft = h.App.Config.Clone();
            SwitchCondition s = draft.SwitchConditions[1];

            draft.SwitchConditions[1] = edit switch
            {
                "來源與目標相同" => s with { ToModeId = Save },
                "未選使用來源" => s with { Sources = UsageSources.None },
                "閒置零分鐘" => s with { Kind = SwitchConditionKind.Idle, IdleAmount = 0 },
                _ => s with { Kind = SwitchConditionKind.Idle, IdleAmount = -1 },
            };

            Assert.Equal(reason, h.App.ApplySettings(draft));
            Assert.Equal(before, h.Store.Text);
            Assert.Equal(SwitchConditionKind.UsageDetected, h.App.Config.SwitchConditions[1].Kind);
        }

        [Fact]
        public void 偵測到使用不檢查閒置時間()
        {
            var h = Modes(Save, Sw.Usage(Save, Play));
            AppConfig draft = h.App.Config.Clone();

            draft.SwitchConditions[0] = draft.SwitchConditions[0] with { IdleAmount = 0 };

            Assert.Null(h.App.ApplySettings(draft));
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("1.5")]
        [InlineData("")]
        [InlineData("-3")]
        [InlineData("0")]
        [InlineData("99999999999")]
        public void 表格中閒置時間輸入的不是正整數時無法套用(string text)
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5));
            AppConfig draft = h.App.Config.Clone();

            draft.SwitchConditions[0] = draft.SwitchConditions[0] with { IdleAmount = SwitchCondition.ParseIdleAmount(text) };

            Assert.Equal("第 1 條切換條件無效：閒置時間必須是正整數。", h.App.ApplySettings(draft));
        }

        [Theory]
        [InlineData(" 7 ")]
        [InlineData("７")]   // 全形數字
        public void 表格中閒置時間前後的空白與全形數字不影響(string text)
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5));
            AppConfig draft = h.App.Config.Clone();

            draft.SwitchConditions[0] = draft.SwitchConditions[0] with { IdleAmount = SwitchCondition.ParseIdleAmount(text) };

            Assert.Null(h.App.ApplySettings(draft));
            Assert.Equal(7 * 60, h.RunUntilSwitch(10 * 60));
        }

        // ---- 順序 ----

        [Fact]
        public void 調整順序後同一來源模式的優先順序隨之改變()
        {
            var h = Modes(Play, Sw.Idle(Play, AllOff, 5), Sw.Idle(Play, Save, 5));
            AppConfig draft = h.App.Config.Clone();

            draft.MoveSwitchCondition(1, 0);   // 把「遊玩→省電」拖到最上面

            Assert.Null(h.App.ApplySettings(draft));
            Assert.Equal(5 * 60, h.RunUntilSwitch(10 * 60));   // 兩條同時成立，執行較前面的
            Assert.Equal("省電", Assert.Single(h.Monitors.AppliedTargets));
            Assert.Equal(new[] { Save, AllOff }, Reread(h).SwitchConditions.Select(s => s.ToModeId));
        }
    }
}
