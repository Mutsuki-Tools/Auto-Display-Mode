using System.Collections.Generic;
using System.Linq;
using AutoDisplayMode.Core;
using Xunit;

namespace AutoDisplayMode.Core.Tests
{
    /// <summary>手動切換與套用規劃：依模式算出要套用的完整螢幕狀態。</summary>
    public class ApplyTests
    {
        static readonly MonitorId IdA = MonitorId.Create("DEL", "A0F4", "SN0001", "");
        static readonly MonitorId IdB = MonitorId.Create("DEL", "A0F4", "SN0002", "");
        static readonly MonitorId IdC = MonitorId.Create("GSM", "5B7F", "SN0003", "");
        static readonly MonitorId IdGone = MonitorId.Create("ACR", "0833", "SN0009", "");

        /// <summary>目前狀態：A 主螢幕 1920x1080、B 在右邊 2560x1440、C 接上但關閉。</summary>
        static DetectedMonitor[] ThreeMonitors() => new[]
        {
            Mon.On("SN0001", 0, 0, 1920, 1080, 60, primary: true),
            Mon.On("SN0002", 1920, 0, 2560, 1440, 144, connector: "DP2", name: "DELL B"),
            Mon.Off("SN0003", new VideoMode(1920, 1080, 75)),
        };

        static string MonitorSection(int mode, int k, MonitorId id, bool enabled, int x = 0, int y = 0, int w = 1920, int h = 1080,
            int hz = 60, bool primary = false, string orientation = "Landscape") =>
            Harness.Cfg(
                "[Mode." + mode + ".Monitor." + k + "]",
                "Monitor=" + id,
                "Enabled=" + (enabled ? 1 : 0),
                "X=" + x, "Y=" + y, "Width=" + w, "Height=" + h, "RefreshRate=" + hz,
                "Orientation=" + orientation,
                "Primary=" + (primary ? 1 : 0));

        /// <summary>模式 1「目前」與模式 2「目標」；<paramref name="target"/> 是模式 2 的額外內容（模式鍵與螢幕段落）。</summary>
        static Harness Make(string target, string general = "AutoSwitch=1", DetectedMonitor[]? monitors = null) =>
            new(Harness.Cfg("[General]", "CurrentMode=1", general,
                    "[Mode.1]", "Name=目前") +
                MonitorSection(1, 1, IdA, true, primary: true) +
                Harness.Cfg("[Mode.2]", "Name=目標") + target,
                monitors: monitors ?? ThreeMonitors());

        static MonitorSettings State(IReadOnlyList<MonitorSettings> states, MonitorId id) => Assert.Single(states, s => s.Id == id);

        // ---- 套用規劃 ----

        [Fact]
        public void 手動切換依模式的螢幕設定輸出主螢幕與座標()
        {
            var h = Make(
                MonitorSection(2, 1, IdA, true, -2560, 0, 2560, 1440, 120) +
                MonitorSection(2, 2, IdB, true, 0, 0, 3840, 2160, 160, primary: true, orientation: "Portrait") +
                MonitorSection(2, 3, IdC, true, -2560, 1440, 1920, 1080, 75, orientation: "LandscapeFlipped"));

            h.App.ManualSwitch(2);

            var states = h.LastApplied;
            Assert.Equal(3, states.Count);
            Assert.Equal(new MonitorSettings { Id = IdA, Enabled = true, X = -2560, Y = 0, Width = 2560, Height = 1440, RefreshRate = 120 },
                State(states, IdA) with { Name = "" });
            Assert.Equal(new MonitorSettings
                {
                    Id = IdB, Enabled = true, X = 0, Y = 0, Width = 3840, Height = 2160, RefreshRate = 160,
                    Orientation = Orientation.Portrait, Primary = true,
                },
                State(states, IdB) with { Name = "" });
            Assert.Equal(new MonitorSettings
                {
                    Id = IdC, Enabled = true, X = -2560, Y = 1440, Width = 1920, Height = 1080, RefreshRate = 75,
                    Orientation = Orientation.LandscapeFlipped,
                },
                State(states, IdC) with { Name = "" });
        }

        [Fact]
        public void 模式中記錄但沒接上的螢幕略過()
        {
            var h = Make(
                MonitorSection(2, 1, IdA, true, primary: true) +
                MonitorSection(2, 2, IdGone, true, 1920, 0) +
                MonitorSection(2, 3, IdB, false) +
                MonitorSection(2, 4, IdC, false));

            h.App.ManualSwitch(2);

            Assert.Equal(new[] { IdA, IdB, IdC }, h.LastApplied.Select(s => s.Id).OrderBy(i => i.ToString()));
            Assert.True(State(h.LastApplied, IdA).Enabled);
            Assert.False(State(h.LastApplied, IdB).Enabled);
        }

        [Fact]
        public void 未記錄的螢幕預設維持現狀()
        {
            // 模式 2 只記錄 C：A、B 維持目前的開關與設定（A 目前是主螢幕，但主螢幕以模式記錄的為準）
            var h = Make(MonitorSection(2, 1, IdC, true, 0, 0, 1920, 1080, 75, primary: true));

            h.App.ManualSwitch(2);

            var states = h.LastApplied;
            Assert.Equal(new MonitorSettings { Id = IdA, Enabled = true, X = 0, Y = 0, Width = 1920, Height = 1080, RefreshRate = 60 },
                State(states, IdA) with { Name = "" });
            Assert.Equal(new MonitorSettings { Id = IdB, Enabled = true, X = 1920, Y = 0, Width = 2560, Height = 1440, RefreshRate = 144 },
                State(states, IdB) with { Name = "" });
            Assert.True(State(states, IdC).Primary);
        }

        [Fact]
        public void 未記錄的螢幕維持現狀時目前關閉的螢幕保持關閉()
        {
            var h = Make(MonitorSection(2, 1, IdA, true, primary: true) + MonitorSection(2, 2, IdB, true, 1920, 0));

            h.App.ManualSwitch(2);

            Assert.False(State(h.LastApplied, IdC).Enabled);
        }

        [Fact]
        public void 未記錄的螢幕可設定為關閉()
        {
            var h = Make("UnlistedMonitors=Off\r\n" + MonitorSection(2, 1, IdB, true, 0, 0, 2560, 1440, 144, primary: true));

            h.App.ManualSwitch(2);

            Assert.False(State(h.LastApplied, IdA).Enabled);
            Assert.True(State(h.LastApplied, IdB).Enabled);
            Assert.False(State(h.LastApplied, IdC).Enabled);
        }

        [Fact]
        public void 結果沒有任何螢幕開著時拒絕套用並通知()
        {
            var h = Make("UnlistedMonitors=Off\r\n" + MonitorSection(2, 1, IdA, false) + MonitorSection(2, 2, IdGone, true, primary: true));

            h.App.ManualSwitch(2);

            Assert.Empty(h.Monitors.Applied);
            Assert.False(h.App.IsSwitching);
            Assert.Equal(1, h.App.Config.CurrentModeId);
            Assert.Contains(h.Notifier.Shown, n => n.Kind == NotificationKind.Warning && n.Text.Contains("沒有任何螢幕") && n.Text.Contains("目標"));
        }

        [Fact]
        public void 記錄的主螢幕沒接上時改以第一台開啟的螢幕為主螢幕並平移座標()
        {
            var h = Make(
                MonitorSection(2, 1, IdGone, true, 0, 0, primary: true) +
                MonitorSection(2, 2, IdB, true, 1920, 0, 2560, 1440, 144) +
                MonitorSection(2, 3, IdA, true, 4480, 0) +
                MonitorSection(2, 4, IdC, false));

            h.App.ManualSwitch(2);

            var b = State(h.LastApplied, IdB);
            var a = State(h.LastApplied, IdA);
            Assert.True(b.Primary);
            Assert.Equal((0, 0), (b.X, b.Y));
            Assert.False(a.Primary);
            Assert.Equal((2560, 0), (a.X, a.Y));
        }

        [Fact]
        public void 只有一台主螢幕且位於原點()
        {
            // 模式 2 的主螢幕 B 不在 (0,0)（手動編輯 cfg 造成）：整體平移讓主螢幕回到原點
            var h = Make(
                MonitorSection(2, 1, IdA, true, 0, 0, primary: true) +
                MonitorSection(2, 2, IdB, true, 1920, 0, 2560, 1440, 144, primary: true));

            h.App.ManualSwitch(2);

            var primary = Assert.Single(h.LastApplied, s => s.Primary);
            Assert.Equal(IdA, primary.Id);
            Assert.Equal((0, 0), (primary.X, primary.Y));
        }

        // ---- 切換結果 ----

        [Fact]
        public void 套用成功後目前模式改變並存檔()
        {
            var h = Make(MonitorSection(2, 1, IdA, true, primary: true));

            h.App.ManualSwitch(2);
            Assert.True(h.App.IsSwitching);
            Assert.Equal(1, h.App.Config.CurrentModeId);
            Assert.Equal("目標", h.App.SwitchTargetName);

            h.FinishSwitch();

            Assert.False(h.App.IsSwitching);
            Assert.Equal(2, h.App.Config.CurrentModeId);
            Assert.Equal("目標", h.App.CurrentModeName);
            Assert.Contains("CurrentMode=2\r\n", h.Store.Text);
        }

        [Fact]
        public void 套用失敗時維持原本的模式並跳出錯誤通知()
        {
            var h = Make(MonitorSection(2, 1, IdA, true, primary: true));

            h.App.ManualSwitch(2);
            h.FinishSwitch("SetDisplayConfig 失敗");

            Assert.Equal(1, h.App.Config.CurrentModeId);
            Assert.Contains(h.Notifier.Shown, n => n.Kind == NotificationKind.Error && n.Text.Contains("SetDisplayConfig 失敗") && n.Text.Contains("目標"));
        }

        [Fact]
        public void 偵測螢幕失敗時不切換並通知()
        {
            var h = Make(MonitorSection(2, 1, IdA, true, primary: true));
            h.Monitors.Fail = true;

            h.App.ManualSwitch(2);

            Assert.Empty(h.Monitors.Applied);
            Assert.False(h.App.IsSwitching);
            Assert.Contains(h.Notifier.Shown, n => n.Kind == NotificationKind.Error && n.Text.Contains("偵測失敗"));
        }

        [Fact]
        public void 切換中再次手動切換會被拒絕()
        {
            var h = Make(MonitorSection(2, 1, IdA, true, primary: true));
            h.App.ManualSwitch(2);

            h.App.ManualSwitch(1);

            Assert.Single(h.Monitors.Applied);
            Assert.Contains(("正在切換中，請稍候。", NotificationKind.Info), h.Notifier.Shown);
            Assert.Equal("目標", h.App.SwitchTargetName);
        }

        // ---- 手動切換進入時的自動切換設定 ----

        [Theory]
        [InlineData("", true, true)]
        [InlineData("", false, false)]
        [InlineData("ManualAutoSwitch=Keep\r\n", false, false)]
        [InlineData("ManualAutoSwitch=On\r\n", false, true)]
        [InlineData("ManualAutoSwitch=Off\r\n", true, false)]
        public void 手動切換進入模式時依設定維持開啟或關閉自動切換(string modeKey, bool autoBefore, bool autoAfter)
        {
            var h = Make(modeKey + MonitorSection(2, 1, IdA, true, primary: true), general: "AutoSwitch=" + (autoBefore ? 1 : 0));

            h.App.ManualSwitch(2);
            h.FinishSwitch();

            Assert.Equal(autoAfter, h.App.Config.AutoSwitch);
            Assert.Contains("AutoSwitch=" + (autoAfter ? 1 : 0) + "\r\n", h.Store.Text);
        }

        [Fact]
        public void 手動切換失敗時不改變自動切換()
        {
            var h = Make("ManualAutoSwitch=Off\r\n" + MonitorSection(2, 1, IdA, true, primary: true));

            h.App.ManualSwitch(2);
            h.FinishSwitch("失敗");

            Assert.True(h.App.Config.AutoSwitch);
        }

        [Fact]
        public void 手動切換不跳出自動切換通知()
        {
            var h = Make(MonitorSection(2, 1, IdA, true, primary: true));

            h.App.ManualSwitch(2);
            h.FinishSwitch();

            Assert.DoesNotContain(h.Notifier.Shown, n => n.Text.Contains("已自動切換"));
        }
    }
}
