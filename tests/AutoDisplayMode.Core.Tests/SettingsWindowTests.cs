using System.Linq;
using AutoDisplayMode.Core;
using Xunit;
using static AutoDisplayMode.Core.Tests.Harness;

namespace AutoDisplayMode.Core.Tests
{
    /// <summary>設定視窗：開啟時暫停自動切換、套用設定（暫存副本、寫入 cfg、立即生效）。</summary>
    public class SettingsWindowTests
    {
        // ---- 開啟時暫停自動切換 ----

        [Fact]
        public void 設定視窗開著時閒置條件成立也不自動切換()
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5));
            h.App.OnSettingsWindowOpened();

            Assert.Equal(-1, h.RunUntilSwitch(20 * 60));
        }

        [Fact]
        public void 設定視窗開著時移除滑鼠與鍵盤鉤子_偵測到使用也不切換()
        {
            var h = Modes(Save, Sw.Usage(Save, Play));
            h.Run(10);
            Assert.True(h.Signals.Policy.Install);

            h.App.OnSettingsWindowOpened();
            Assert.False(h.Signals.Policy.Install);

            Assert.Equal(-1, h.RunUntilSwitch(30, () => h.Signals.MoveMouse()));
            Assert.False(h.Signals.Policy.Install);
        }

        [Fact]
        public void 關閉設定視窗後閒置計時歸零_不會一關閉就切換()
        {
            var h = Modes(Play, Sw.Idle(Play, Save, 5, Sw.Audio));
            h.App.OnSettingsWindowOpened();
            h.Run(10 * 60);   // 編輯設定期間沒有播放聲音

            h.App.OnSettingsWindowClosed();

            Assert.Equal(5 * 60, h.RunUntilSwitch(10 * 60));
        }

        [Fact]
        public void 關閉設定視窗後重新安裝鉤子_3秒緩衝期內的輸入不算使用()
        {
            var h = Modes(Save, Sw.Usage(Save, Play));
            h.App.OnSettingsWindowOpened();
            h.Run(10);

            h.App.OnSettingsWindowClosed();
            Assert.True(h.Signals.Policy.Install);
            Assert.Equal(h.Clock.NowMs + 3000, h.Signals.Policy.IgnoreUntilMs);

            Assert.Equal(3, h.RunUntilSwitch(10, () => h.Signals.MoveMouse()));   // 第 1、2 秒的輸入被忽略
        }

        [Fact]
        public void 設定視窗開著時拒絕重新載入cfg()
        {
            var h = Modes(Play);
            h.App.OnSettingsWindowOpened();
            h.Store.Text = h.Store.Text!.Replace("[General]", "[General]\r\nMouseMoveThreshold=30");
            Assert.Equal(30, new Harness(h.Store.Text).App.Config.MouseMoveThreshold);   // 確認 cfg 確實改了

            h.App.ReloadConfig();

            Assert.Equal(10, h.App.Config.MouseMoveThreshold);
            Assert.Contains("設定視窗開啟中", h.Notifier.Shown.Last().Text);
        }

        [Fact]
        public void 設定視窗開著時拒絕從工作列擷取()
        {
            var h = Modes(Play);
            h.App.OnSettingsWindowOpened();
            h.Monitors.Monitors = new() { Mon.On("SN0001", 0, 0, 2560, 1440, 144, primary: true) };
            int writes = h.Store.Writes;

            h.App.CaptureToCurrentMode();

            Assert.Equal(1920, h.App.Config.CurrentModeConfig!.Monitors.Single().Width);
            Assert.Equal(writes, h.Store.Writes);
            Assert.Contains("設定視窗開啟中", h.Notifier.Shown.Last().Text);
        }

        [Fact]
        public void 設定視窗開著時仍可從工作列手動切換()
        {
            var h = Modes(Play);
            h.App.OnSettingsWindowOpened();

            h.App.ManualSwitch(Save);
            h.FinishSwitch();

            Assert.Equal(Save, h.App.Config.CurrentModeId);
        }

        // ---- 套用設定 ----

        /// <summary>把一般設定改成與預設值都不同的值。</summary>
        static void EditGeneral(AppConfig draft)
        {
            draft.MouseMoveThreshold = 25;
            draft.AudioThreshold = 0.02;
            draft.AudioSeconds = 5;
            draft.IgnoreRemoteInput = false;
            draft.ShowNotifications = false;
            draft.StartWithWindows = true;
        }

        /// <summary><see cref="Harness.Modes"/> 的一般設定（未修改）。</summary>
        const string UnchangedGeneral = "10|0.1|3|True|True|False";

        static string General(AppConfig c) =>
            string.Join("|", c.MouseMoveThreshold, c.AudioThreshold, c.AudioSeconds, c.IgnoreRemoteInput, c.ShowNotifications, c.StartWithWindows);

        [Fact]
        public void 套用一般設定後寫入cfg_讀回相同()
        {
            var h = Modes(Play);
            h.App.OnSettingsWindowOpened();
            AppConfig draft = h.App.Config.Clone();
            EditGeneral(draft);

            Assert.Null(h.App.ApplySettings(draft));

            const string expected = "25|0.02|5|False|False|True";
            Assert.Equal(expected, General(h.App.Config));
            Assert.Equal(expected, General(new Harness(h.Store.Text).App.Config));
        }

        [Fact]
        public void 取消時不改變任何東西_暫存的副本與設定互不影響()
        {
            var h = PlayAndSave(Play);
            string before = h.Store.Text!;
            int writes = h.Store.Writes;
            h.App.OnSettingsWindowOpened();
            AppConfig draft = h.App.Config.Clone();

            EditGeneral(draft);
            draft.Modes[0].Name = "改名";
            draft.Modes[0].Monitors.Clear();
            draft.Modes.RemoveAt(2);
            draft.SwitchConditions.Clear();
            h.App.OnSettingsWindowClosed();   // 取消：不呼叫 ApplySettings

            Assert.Equal(UnchangedGeneral, General(h.App.Config));
            Assert.Equal(new[] { "遊玩", "省電", "全關" }, h.App.Config.Modes.Select(m => m.Name));
            Assert.Single(h.App.Config.Modes[0].Monitors);
            Assert.Equal(2, h.App.Config.SwitchConditions.Count);
            Assert.Equal(writes, h.Store.Writes);
            Assert.Equal(before, h.Store.Text);
        }

        [Fact]
        public void 套用時目前模式與自動切換以協調者為準_不被副本蓋掉()
        {
            var h = Modes(Play);
            h.App.OnSettingsWindowOpened();
            AppConfig draft = h.App.Config.Clone();

            // 設定視窗開著時從工作列手動切換、關閉自動切換
            h.App.ManualSwitch(Save);
            h.FinishSwitch();
            h.App.SetAutoSwitch(false);

            Assert.Null(h.App.ApplySettings(draft));

            Assert.Equal(Save, h.App.Config.CurrentModeId);
            Assert.False(h.App.Config.AutoSwitch);
            var reread = new Harness(h.Store.Text).App.Config;
            Assert.Equal(Save, reread.CurrentModeId);
            Assert.False(reread.AutoSwitch);
        }

        [Fact]
        public void 套用後一般設定立即生效()
        {
            var h = Modes(Save, Sw.Usage(Save, Play, Sw.Both));
            h.App.OnSettingsWindowOpened();
            AppConfig draft = h.App.Config.Clone();
            EditGeneral(draft);   // 移動門檻 25、聲音持續 5 秒、不忽略遠端輸入、不跳通知
            Assert.Null(h.App.ApplySettings(draft));
            h.App.OnSettingsWindowClosed();
            h.Run(10);

            Assert.Equal(25, h.Signals.Policy.MoveThreshold);

            h.Signals.AudioPeak = 0.05f;   // 超過新的門檻 0.02（舊的門檻是 0.1）
            Assert.Equal(6, h.RunUntilSwitch(20));   // 聲音要連續超過 5 秒
            int shown = h.Notifier.Shown.Count;
            h.FinishSwitch();
            Assert.Equal(shown, h.Notifier.Shown.Count);   // 不跳自動切換通知
        }

        [Fact]
        public void 無法寫入cfg時套用失敗_回傳原因且設定不變()
        {
            var h = Modes(Play);
            h.Store.FailWrites = true;
            AppConfig draft = h.App.Config.Clone();
            EditGeneral(draft);

            Assert.Contains("無法寫入設定檔", h.App.ApplySettings(draft));
            Assert.Equal(UnchangedGeneral, General(h.App.Config));
        }

        [Fact]
        public void cfg讀取失敗時套用失敗_不覆蓋原本的cfg()
        {
            var h = Modes(Play);
            string original = h.Store.Text!;
            h.Store.FailReads = true;
            h.App.ReloadConfig();
            AppConfig draft = h.App.Config.Clone();
            EditGeneral(draft);

            Assert.Contains("未套用", h.App.ApplySettings(draft));
            Assert.Equal(10, h.App.Config.MouseMoveThreshold);
            Assert.Equal(original, h.Store.Text);
        }

        [Fact]
        public void 副本中沒有目前模式時無法套用()
        {
            var h = Modes(Play);
            AppConfig draft = h.App.Config.Clone();
            h.App.ManualSwitch(Save);   // 設定視窗開著時從工作列切到「省電」
            h.FinishSwitch();
            draft.Modes.RemoveAll(m => m.Id == Save);

            Assert.Contains("目前模式", h.App.ApplySettings(draft));
            Assert.Equal(3, h.App.Config.Modes.Count);
        }

        [Fact]
        public void 副本中有無效的切換條件時無法套用並說明原因()
        {
            var h = Modes(Play);
            AppConfig draft = h.App.Config.Clone();
            draft.SwitchConditions.Add(new SwitchCondition { FromModeId = Play, ToModeId = Play });

            Assert.Contains("來源模式與目標模式相同", h.App.ApplySettings(draft));
            Assert.Empty(h.App.Config.SwitchConditions);
        }

        [Theory]
        [InlineData("MouseMoveThreshold", 0)]
        [InlineData("MouseMoveThreshold", 1001)]
        [InlineData("AudioThreshold", -0.1)]
        [InlineData("AudioThreshold", 1.5)]
        [InlineData("AudioSeconds", 0)]
        [InlineData("AudioSeconds", 3601)]
        public void 一般設定超出範圍時無法套用並說明原因(string field, double value)
        {
            var h = Modes(Play);
            int writes = h.Store.Writes;
            AppConfig draft = h.App.Config.Clone();
            switch (field)
            {
                case "MouseMoveThreshold": draft.MouseMoveThreshold = (int)value; break;
                case "AudioThreshold": draft.AudioThreshold = value; break;
                case "AudioSeconds": draft.AudioSeconds = (int)value; break;
            }

            Assert.NotNull(h.App.ApplySettings(draft));
            Assert.Equal(UnchangedGeneral, General(h.App.Config));
            Assert.Equal(writes, h.Store.Writes);
        }
    }
}
