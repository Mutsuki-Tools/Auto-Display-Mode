using System.Linq;
using AutoDisplayMode.Core;
using Xunit;
using static AutoDisplayMode.Core.Tests.Harness;

namespace AutoDisplayMode.Core.Tests
{
    /// <summary>螢幕配置分頁的「未接上的螢幕」清單：列出、刪除、「對應到…」接上但此模式未設定的螢幕（ADR-0003）。</summary>
    public class DisconnectedMonitorsTests
    {
        static AppConfig Reread(Harness h) => new Harness(h.Store.Text).App.Config;

        /// <summary>模式「遊玩」中一台螢幕的 cfg 分段（廠牌 DEL、型號 A0F4）。</summary>
        static string[] Recorded(int n, string serial, string name, int x, int w, int h, bool primary) => new[]
        {
            "[Mode.1.Monitor." + n + "]", "Monitor=DEL/A0F4/SN:" + serial, "Name=" + name, "Enabled=1",
            "X=" + x, "Y=0", "Width=" + w, "Height=" + h, "RefreshRate=60", "Primary=" + (primary ? 1 : 0),
        };

        /// <summary>
        /// 模式「遊玩」記錄主螢幕（SN0001）與右邊的舊螢幕（SN0002，2560x1440）。
        /// 現在接上的是主螢幕與一台同型號、不同序號的新螢幕（SN0003），舊螢幕沒接上。
        /// </summary>
        static Harness Replaced(out AppConfig draft)
        {
            var h = new Harness(
                Cfg(new[] { "[General]", "CurrentMode=1", "[Mode.1]", "Name=遊玩" }
                    .Concat(Recorded(1, "SN0001", "主", 0, 1920, 1080, true))
                    .Concat(Recorded(2, "SN0002", "舊螢幕", 1920, 2560, 1440, false)).ToArray()),
                monitors: new[] { DefaultMonitor, NewMonitor });
            draft = h.App.Config.Clone();
            return h;
        }

        static DetectedMonitor NewMonitor => Mon.On("SN0003", 1920, 0, 1920, 1080, 60, connector: "DP2", name: "新螢幕");

        /// <summary>重新讀取 cfg 後模式「遊玩」的每台螢幕：(識別, 名稱, X, 寬, 高, 主螢幕)。</summary>
        static (string, string, int, int, int, bool)[] Saved(Harness h) =>
            Reread(h).FindMode(1)!.Monitors.Select(m => (m.Id.ToString(), m.Name, m.X, m.Width, m.Height, m.Primary)).ToArray();

        // 清單與對應選項是分頁直接顯示的內容，與配置驗證的警告相同，直接斷言回傳值

        [Fact]
        public void 拔掉的螢幕列在未接上的螢幕清單中_對應選項是接上但此模式未設定的螢幕()
        {
            Replaced(out AppConfig draft);
            ModeConfig mode = draft.FindMode(1)!;
            var connected = new[] { DefaultMonitor, NewMonitor };

            Assert.Equal(new[] { "舊螢幕" }, mode.Disconnected(connected).Select(m => m.Name));
            Assert.Equal(new[] { "新螢幕" }, mode.Unrecorded(connected).Select(d => d.Name));

            mode.MapMonitor(MonitorId.Parse("DEL/A0F4/SN:SN0002")!, NewMonitor);

            Assert.Empty(mode.Disconnected(connected));
            Assert.Empty(mode.Unrecorded(connected));
        }

        [Fact]
        public void 未接上的螢幕造成重疊時_分頁的錯誤標出未接上_刪除後即可套用()
        {
            var h = Replaced(out AppConfig draft);
            ModeConfig mode = draft.FindMode(1)!;
            // 舊螢幕（沒接上，畫布上看不到）與主螢幕重疊
            mode.Monitors[1] = mode.Monitors[1] with { X = 1000 };

            Assert.Equal("「主」與「舊螢幕（未接上）」重疊", Assert.Single(MonitorLayout.Check(mode, new[] { DefaultMonitor, NewMonitor }).Errors));
            Assert.Equal(new[] { "DEL/A0F4/SN:SN0001", "DEL/A0F4/SN:SN0002" }, MonitorLayout.Overlapping(mode).Select(id => id.ToString()).OrderBy(s => s));
            Assert.Equal("「遊玩」的螢幕配置有誤：「主」與「舊螢幕」重疊。", h.App.ApplySettings(draft));

            mode.RemoveMonitor(MonitorId.Parse("DEL/A0F4/SN:SN0002")!);

            Assert.Null(h.App.ApplySettings(draft));
        }

        [Fact]
        public void 刪除未接上的螢幕後_它的螢幕設定從模式中移除()
        {
            var h = Replaced(out AppConfig draft);
            ModeConfig mode = draft.FindMode(1)!;

            mode.RemoveMonitor(MonitorId.Parse("DEL/A0F4/SN:SN0002")!);

            Assert.Null(h.App.ApplySettings(draft));
            Assert.Equal(new[] { ("DEL/A0F4/SN:SN0001", "主", 0, 1920, 1080, true) }, Saved(h));
        }

        [Fact]
        public void 對應到新螢幕後_舊螢幕設定改套用到新螢幕_原記錄移除()
        {
            var h = Replaced(out AppConfig draft);
            ModeConfig mode = draft.FindMode(1)!;

            mode.MapMonitor(MonitorId.Parse("DEL/A0F4/SN:SN0002")!, NewMonitor);

            Assert.Null(h.App.ApplySettings(draft));
            Assert.Equal(new[]
            {
                ("DEL/A0F4/SN:SN0001", "主", 0, 1920, 1080, true),
                ("DEL/A0F4/SN:SN0003", "新螢幕", 1920, 2560, 1440, false),   // 座標與解析度沿用舊螢幕
            }, Saved(h));

            // 切換到此模式時，新螢幕依沿用的螢幕設定套用
            h.App.ManualSwitch(1);
            MonitorSettings applied = h.LastApplied.Single(s => s.Id == NewMonitor.Id);
            Assert.Equal((true, 1920, 0, 2560, 1440), (applied.Enabled, applied.X, applied.Y, applied.Width, applied.Height));
        }

        /// <summary>
        /// 模式「遊玩」以舊螢幕（SN0002，沒接上）為主螢幕，SN0001 在它右邊，另有一台關閉、但 cfg 中殘留主螢幕標記的螢幕（SN0009）。
        /// 接上的是 SN0001 與新螢幕（SN0003）。
        /// </summary>
        static Harness OldPrimary(out AppConfig draft)
        {
            string[] stale = Recorded(3, "SN0009", "關閉的", 0, 1920, 1080, true);
            stale[3] = "Enabled=0";
            var h = new Harness(
                Cfg(new[] { "[General]", "CurrentMode=1", "[Mode.1]", "Name=遊玩" }
                    .Concat(Recorded(1, "SN0002", "舊螢幕", 0, 2560, 1440, true))
                    .Concat(Recorded(2, "SN0001", "主", 2560, 1920, 1080, false))
                    .Concat(stale).ToArray()),
                monitors: new[] { DefaultMonitor, NewMonitor });
            draft = h.App.Config.Clone();
            return h;
        }

        [Fact]
        public void 刪除的未接上螢幕是主螢幕時_第一台開啟的螢幕接任並移到原點()
        {
            var h = OldPrimary(out AppConfig draft);

            draft.FindMode(1)!.RemoveMonitor(MonitorId.Parse("DEL/A0F4/SN:SN0002")!);

            Assert.Null(h.App.ApplySettings(draft));
            Assert.Equal(new[]
            {
                ("DEL/A0F4/SN:SN0001", "主", 0, 1920, 1080, true),
                ("DEL/A0F4/SN:SN0009", "關閉的", 0, 1920, 1080, false),   // 關閉的螢幕不是主螢幕
            }, Saved(h));
        }

        [Fact]
        public void 對應的舊記錄是主螢幕時_新螢幕成為主螢幕並維持在原點()
        {
            var h = OldPrimary(out AppConfig draft);

            draft.FindMode(1)!.MapMonitor(MonitorId.Parse("DEL/A0F4/SN:SN0002")!, NewMonitor);

            Assert.Null(h.App.ApplySettings(draft));
            Assert.Equal(new[]
            {
                ("DEL/A0F4/SN:SN0003", "新螢幕", 0, 2560, 1440, true),
                ("DEL/A0F4/SN:SN0001", "主", 2560, 1920, 1080, false),
                ("DEL/A0F4/SN:SN0009", "關閉的", 0, 1920, 1080, false),
            }, Saved(h));
        }
    }
}
