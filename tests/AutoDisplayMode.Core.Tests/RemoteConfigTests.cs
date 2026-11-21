using System.Linq;
using AutoDisplayMode.Core;
using Xunit;
using static AutoDisplayMode.Core.Tests.Harness;

namespace AutoDisplayMode.Core.Tests
{
    /// <summary>cfg 中潛行模式、內建切換條件與復原模式的欄位：讀寫、註解、驗證與修正。</summary>
    public class RemoteConfigTests
    {
        [Fact]
        public void 潛行模式_內建切換條件與復原模式寫入cfg後讀回相同()
        {
            var h = WithRemote(Play, "RestoreMode=2");
            AppConfig draft = h.App.Config.Clone();
            draft.FindMode(Remote4K)!.VirtualMode = new VideoMode(2560, 1440, 144);
            Assert.Null(h.App.ApplySettings(draft));

            AppConfig reread = new Harness(h.Store.Text).App.Config;

            Assert.Equal(2, reread.RestoreModeId);
            Assert.Equal(new[] { ModeKind.Normal, ModeKind.Normal, ModeKind.Normal, ModeKind.Remote, ModeKind.Remote },
                reread.Modes.Select(m => m.Kind));
            Assert.Equal(new VideoMode(1920, 1080, 60), reread.FindMode(Remote)!.VirtualMode);
            Assert.Equal(new VideoMode(2560, 1440, 144), reread.FindMode(Remote4K)!.VirtualMode);
            Assert.Equal(new[] { SwitchCondition.BuiltInFor(Remote, Play), SwitchCondition.BuiltInFor(Remote4K, Play) },
                reread.SwitchConditions);
            Assert.Equal(h.Store.Text, new Harness(h.Store.Text).Store.Text);   // 讀回後不改寫
        }

        [Fact]
        public void 潛行模式寫出種類與虛擬螢幕_不寫未記錄的螢幕與螢幕設定()
        {
            string cfg = Written(WithRemote(Play));

            string remote = Section(cfg, "[Mode.4]");
            Assert.Contains("Kind=Remote\r\n", remote);
            Assert.Contains("VirtualWidth=1920\r\nVirtualHeight=1080\r\nVirtualRefreshRate=60\r\n", remote);
            Assert.DoesNotContain("UnlistedMonitors", remote);
            Assert.DoesNotContain("[Mode.4.Monitor", cfg);
            Assert.Contains("Kind=Normal\r\n", Section(cfg, "[Mode.1]"));
            Assert.Contains("BuiltIn=1\r\n", Section(cfg, "[Switch.1]"));
            Assert.Contains("RestoreMode=1\r\n", cfg);
        }

        [Fact]
        public void cfg註解說明新欄位()
        {
            string cfg = Written(WithRemote(Play));

            Assert.Contains("# 復原模式", cfg);
            Assert.Contains("#   BuiltIn", cfg);
            Assert.Contains("#    Kind ", cfg);
            Assert.Contains("#    VirtualWidth, VirtualHeight, VirtualRefreshRate", cfg);
        }

        [Fact]
        public void 一般切換條件不寫BuiltIn()
        {
            string cfg = Written(Modes(Play, Sw.Idle(Play, Save, 5)));

            Assert.DoesNotContain("\r\nBuiltIn=", cfg);
        }

        /// <summary>經由套用設定，讓程式整份重寫 cfg（手寫的 cfg 讀取時不改寫），回傳寫出的內容。</summary>
        static string Written(Harness h)
        {
            Assert.Null(h.App.ApplySettings(h.App.Config.Clone()));
            return h.Store.Text!;
        }

        [Fact]
        public void 沒寫Kind的模式是一般模式_沒寫RestoreMode時用目前模式()
        {
            AppConfig c = Modes(Save).App.Config;

            Assert.All(c.Modes, m => Assert.Equal(ModeKind.Normal, m.Kind));
            Assert.Equal(Save, c.RestoreModeId);
        }

        [Fact]
        public void 潛行模式的虛擬螢幕設定無效時用預設值()
        {
            var h = new Harness(Cfg("[Mode.1]", "Name=遊玩", "[Mode.2]", "Name=潛行", "Kind=Remote",
                "VirtualWidth=abc", "VirtualHeight=1440", "VirtualRefreshRate=0") + Cfg("[Switch.1]") + BuiltIn(2, 1));

            Assert.Equal(VirtualDisplayModes.Default, h.App.Config.FindMode(2)!.VirtualMode);
        }

        [Fact]
        public void 潛行模式的螢幕設定與未記錄的螢幕在讀取時忽略()
        {
            var h = new Harness(Cfg("[Mode.1]", "Name=遊玩", "[Mode.2]", "Name=潛行", "Kind=Remote", "UnlistedMonitors=Off",
                "[Mode.2.Monitor.1]", "Monitor=" + DefaultMonitor.Id, "Enabled=1") + Cfg("[Switch.1]") + BuiltIn(2, 1));

            ModeConfig remote = h.App.Config.FindMode(2)!;
            Assert.Empty(remote.Monitors);
            Assert.Equal(UnlistedMonitors.Keep, remote.UnlistedMonitors);
        }

        // ---- 內建切換條件的驗證與修正 ----

        static Harness Load(params string[] switches)
        {
            string cfg = Cfg("[General]", "CurrentMode=1",
                             "[Mode.1]", "Name=遊玩", "[Mode.2]", "Name=省電") +
                         RemoteMode(3, "潛行", 1920, 1080, 60) + RemoteMode(4, "潛行 4K", 3840, 2160, 60);
            int n = 0;
            foreach (string s in switches) cfg += Cfg("[Switch." + ++n + "]") + s;
            return new Harness(cfg);
        }

        static string StartNote(Harness h) => h.Notifier.Shown.First().Text;

        [Fact]
        public void 缺少的內建切換條件補在最後並提示()
        {
            var h = Load(BuiltIn(3, 2), Sw.Idle(1, 2, 5));

            Assert.Equal(new[] { SwitchCondition.BuiltInFor(3, 2), SwitchCondition.BuiltInFor(4, 1) },
                h.App.Config.SwitchConditions.Where(s => s.BuiltIn));
            Assert.True(h.App.Config.SwitchConditions.Last().BuiltIn);
            Assert.Contains("已為「潛行 4K」補上內建切換條件（目標為「遊玩」）。", StartNote(h));
            Assert.Contains("[Switch.3]", h.Store.Text);   // 修正後改寫 cfg
        }

        [Fact]
        public void 內建切換條件的目標不存在或是潛行模式時改成第一個一般模式並提示()
        {
            var h = Load(BuiltIn(3, 9), BuiltIn(4, 3));

            Assert.All(h.App.Config.SwitchConditions, s => Assert.Equal(1, s.ToModeId));
            Assert.Contains("「潛行」的內建切換條件的目標模式不存在或不是一般模式，已改成「遊玩」。", StartNote(h));
            Assert.Contains("「潛行 4K」的內建切換條件的目標模式不存在或不是一般模式，已改成「遊玩」。", StartNote(h));
        }

        [Fact]
        public void 內建切換條件鎖定的欄位被改掉時改回固定值並提示()
        {
            var h = Load(
                Cfg("From=3", "Condition=Idle", "IdleTime=5", "MouseKeyboard=1", "Audio=1", "To=2", "Forced=0", "BuiltIn=1"),
                BuiltIn(4, 1));

            Assert.Equal(SwitchCondition.BuiltInFor(3, 2), h.App.Config.SwitchConditions[0]);
            Assert.Contains("已將「潛行」的內建切換條件改回固定的條件", StartNote(h));
        }

        [Fact]
        public void 重複的內建切換條件與來源不是潛行模式的內建切換條件刪除並提示()
        {
            var h = Load(BuiltIn(3, 1), BuiltIn(3, 2), BuiltIn(1, 2), BuiltIn(4, 1));

            Assert.Equal(new[] { SwitchCondition.BuiltInFor(3, 1), SwitchCondition.BuiltInFor(4, 1) }, h.App.Config.SwitchConditions);
            Assert.Contains("已刪除「潛行」重複的內建切換條件。", StartNote(h));
            Assert.Contains("已刪除來源模式不是潛行模式的內建切換條件", StartNote(h));
        }

        [Fact]
        public void 內建切換條件都正確時不提示也不改寫()
        {
            var h = Load(BuiltIn(3, 2), BuiltIn(4, 1));

            Assert.Equal(0, h.Store.Writes);
            Assert.DoesNotContain("內建切換條件", StartNote(h));
        }

        static string Section(string cfg, string header)
        {
            int start = cfg.IndexOf("\r\n" + header + "\r\n", System.StringComparison.Ordinal);   // 不算註解中的範例
            Assert.True(start >= 0, header);
            start += 2;
            int end = cfg.IndexOf("\r\n[", start + header.Length, System.StringComparison.Ordinal);
            return end < 0 ? cfg.Substring(start) : cfg.Substring(start, end - start);
        }
    }
}
