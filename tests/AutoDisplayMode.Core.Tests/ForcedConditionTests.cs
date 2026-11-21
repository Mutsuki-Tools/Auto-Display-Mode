using AutoDisplayMode.Core;
using Xunit;
using static AutoDisplayMode.Core.Tests.Harness;

namespace AutoDisplayMode.Core.Tests
{
    /// <summary>切換條件的「強制」：自動切換關閉、設定視窗開著時照樣生效；鉤子需求跟著調整。</summary>
    public class ForcedConditionTests
    {
        const string AutoOff = "AutoSwitch=0";

        // ---- 自動切換關閉 ----

        [Fact]
        public void 自動切換關閉時只檢查有勾強制的切換條件()
        {
            var h = Modes(Play, AutoOff, Sw.Idle(Play, AllOff, 3), Sw.Idle(Play, Save, 5, forced: true));

            Assert.Equal(5 * 60, h.RunUntilSwitch(10 * 60));
            Assert.Equal("省電", Assert.Single(h.Monitors.AppliedTargets));
            h.FinishSwitch();
            Assert.Equal(Save, h.App.Config.CurrentModeId);
            Assert.False(h.App.Config.AutoSwitch);   // 依強制條件切換不改變自動切換
        }

        [Fact]
        public void 自動切換關閉且沒有強制條件時不切換()
        {
            var h = Modes(Play, AutoOff, Sw.Idle(Play, Save, 5));

            Assert.Equal(-1, h.RunUntilSwitch(20 * 60));
        }

        [Fact]
        public void 自動切換關閉時強制條件仍照編號檢查_第一條成立的就切換()
        {
            var h = Modes(Play, AutoOff, Sw.Idle(Play, AllOff, 5, forced: true), Sw.Idle(Play, Save, 5, forced: true));

            h.RunUntilSwitch(10 * 60);

            Assert.Equal("全關", Assert.Single(h.Monitors.AppliedTargets));
        }

        [Fact]
        public void 自動切換開啟時強制與不強制的條件一起照編號檢查()
        {
            var h = Modes(Play, Sw.Idle(Play, AllOff, 5), Sw.Idle(Play, Save, 5, forced: true));

            h.RunUntilSwitch(10 * 60);

            Assert.Equal("全關", Assert.Single(h.Monitors.AppliedTargets));
        }

        [Fact]
        public void 自動切換關閉時強制的偵測到使用照樣切換()
        {
            var h = Modes(Save, AutoOff, Sw.Usage(Save, Play, forced: true));
            h.Run(10);   // 度過啟動緩衝期
            Assert.Empty(h.Monitors.Applied);

            h.Run(1, () => h.Signals.MoveMouse());

            Assert.Equal("遊玩", Assert.Single(h.Monitors.AppliedTargets));
        }

        [Fact]
        public void 從右鍵選單關閉自動切換後強制條件照樣生效()
        {
            var h = Modes(Play, Sw.Idle(Play, AllOff, 3), Sw.Idle(Play, Save, 5, forced: true));

            h.App.SetAutoSwitch(false);

            Assert.Equal(5 * 60, h.RunUntilSwitch(10 * 60));
            Assert.Equal("省電", Assert.Single(h.Monitors.AppliedTargets));
        }

        [Fact]
        public void 強制條件也遵守切換後的緩衝期()
        {
            var h = Modes(Save, AutoOff, Sw.Usage(Save, Play, forced: true));

            Assert.Equal(-1, h.RunUntilSwitch(4, () => h.Signals.MoveMouse()));   // 啟動後 5 秒內的輸入不算使用
            Assert.Equal(1, h.RunUntilSwitch(2, () => h.Signals.MoveMouse()));
        }

        // ---- 設定視窗開著 ----

        [Fact]
        public void 設定視窗開著時只有強制條件生效_切換後設定視窗維持開啟()
        {
            var h = Modes(Play, Sw.Idle(Play, AllOff, 3), Sw.Idle(Play, Save, 5, forced: true));
            h.App.OnSettingsWindowOpened();

            Assert.Equal(5 * 60, h.RunUntilSwitch(10 * 60));
            Assert.Equal("省電", Assert.Single(h.Monitors.AppliedTargets));
            h.FinishSwitch();

            Assert.Equal(Save, h.App.Config.CurrentModeId);
            Assert.True(h.App.SettingsWindowOpen);
        }

        [Fact]
        public void 設定視窗開著時強制切換不影響編輯中的副本_套用時目前模式以協調者為準()
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5, forced: true));
            h.App.OnSettingsWindowOpened();
            AppConfig draft = h.App.Config.Clone();
            draft.MouseMoveThreshold = 30;

            h.RunUntilSwitch(10 * 60);
            h.FinishSwitch();
            Assert.Equal(Play, draft.CurrentModeId);   // 編輯中的副本不受影響
            Assert.Equal(30, draft.MouseMoveThreshold);

            Assert.Null(h.App.ApplySettings(draft));
            Assert.Equal(Save, h.App.Config.CurrentModeId);
            Assert.Equal(30, h.App.Config.MouseMoveThreshold);
        }

        [Fact]
        public void 設定視窗開著且自動切換關閉時強制條件照樣生效()
        {
            var h = Modes(Save, AutoOff, Sw.Usage(Save, Play, forced: true));
            h.App.OnSettingsWindowOpened();
            h.Run(10);

            h.Run(1, () => h.Signals.MoveMouse());

            Assert.Equal("遊玩", Assert.Single(h.Monitors.AppliedTargets));
        }

        // ---- 鉤子需求 ----

        [Fact]
        public void 自動切換關閉時只有強制的偵測到使用加鍵盤滑鼠條件需要鉤子()
        {
            var plain = Modes(Save, AutoOff, Sw.Usage(Save, Play));
            plain.Run(1);
            Assert.False(plain.Signals.Policy.Install);

            var forced = Modes(Save, AutoOff, Sw.Usage(Save, Play, forced: true));
            forced.Run(1);
            Assert.True(forced.Signals.Policy.Install);
        }

        [Fact]
        public void 設定視窗開著時有強制的偵測到使用加鍵盤滑鼠條件就保留鉤子()
        {
            var h = Modes(Save, Sw.Usage(Save, Play, forced: true));
            h.Run(10);

            h.App.OnSettingsWindowOpened();
            Assert.True(h.Signals.Policy.Install);

            h.Run(1, () => h.Signals.MoveMouse());
            Assert.Equal("遊玩", Assert.Single(h.Monitors.AppliedTargets));
            Assert.False(h.Signals.Policy.Install);   // 切換期間不偵測
        }

        [Fact]
        public void 強制條件只有閒置或只選聲音時不需要鉤子()
        {
            var h = Modes(Save, AutoOff,
                Sw.Usage(Save, Play, Sw.Audio, forced: true),
                Sw.Idle(Save, AllOff, 5, forced: true),
                Sw.Usage(Save, Play));   // 有鍵盤滑鼠但沒勾強制

            h.Run(1);

            Assert.False(h.Signals.Policy.Install);
        }

        [Fact]
        public void 關閉自動切換時移除只給不強制條件用的鉤子_開啟時重新安裝()
        {
            var h = Modes(Save, Sw.Usage(Save, Play));
            h.Run(1);
            Assert.True(h.Signals.Policy.Install);

            h.App.SetAutoSwitch(false);
            Assert.False(h.Signals.Policy.Install);

            h.App.SetAutoSwitch(true);
            Assert.True(h.Signals.Policy.Install);
        }

        // ---- 其他 ----

        [Fact]
        public void 新增的切換條件預設不勾強制()
        {
            AppConfig draft = Modes(Play).App.Config.Clone();

            Assert.False(draft.AddSwitchCondition(Play).Forced);
        }

        [Fact]
        public void 狀態摘要中有勾強制的切換條件標示強制()
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5, forced: true), Sw.Idle(Play, AllOff, 9));

            Assert.Contains("「遊玩」閒置 5 分鐘（鍵盤滑鼠）→「省電」（強制）\n", h.App.StatusSummary);
            Assert.EndsWith("「遊玩」閒置 9 分鐘（鍵盤滑鼠）→「全關」", h.App.StatusSummary);
        }
    }
}
