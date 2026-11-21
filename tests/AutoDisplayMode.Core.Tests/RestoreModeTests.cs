using System.Linq;
using AutoDisplayMode.Core;
using Xunit;
using static AutoDisplayMode.Core.Tests.Harness;

namespace AutoDisplayMode.Core.Tests
{
    /// <summary>
    /// 復原模式：切換到一般模式時更新、重新編號時跟著改、程式啟動時從潛行模式改套用復原模式、
    /// 虛擬螢幕意外消失時切回復原模式、復原模式不存在時的備案。
    /// </summary>
    public class RestoreModeTests
    {
        static AppConfig Reread(Harness h) => new Harness(h.Store.Text).App.Config;

        static void Switch(Harness h, int modeId)
        {
            h.App.ManualSwitch(modeId);
            h.FinishSwitch();
            if (h.App.IsSwitching)
            {
                // 離開潛行模式：移除虛擬螢幕後等一下再重新套用
                h.Run(2);
                h.FinishSwitch();
            }
        }

        // ---- 記錄 ----

        [Fact]
        public void 切換到一般模式時更新復原模式_切換到潛行模式時不變()
        {
            var h = WithRemote(Play);

            Switch(h, Save);
            Assert.Equal(Save, h.App.Config.RestoreModeId);
            Assert.Contains("RestoreMode=2\r\n", h.Store.Text);

            Switch(h, Remote);
            Assert.Equal(Save, h.App.Config.RestoreModeId);
            Assert.Contains("RestoreMode=2\r\n", h.Store.Text);
        }

        [Fact]
        public void 擷取存入的一般模式成為復原模式()
        {
            var h = WithRemote(Play);

            AppConfig draft = h.App.Config.Clone();
            ModeConfig save = draft.FindMode(Save)!;
            var captured = h.App.CaptureForSettings(save.Monitors)!;
            save.Monitors.Clear();
            save.Monitors.AddRange(captured);
            Assert.Null(h.App.ApplySettings(draft, Save));

            Assert.Equal(Save, h.App.Config.RestoreModeId);
        }

        [Fact]
        public void 重新編號時復原模式跟著改()
        {
            var h = WithRemote(Play);
            Switch(h, Save);
            AppConfig draft = h.App.Config.Clone();

            draft.MoveMode(1, 0);   // 「省電」移到最前面
            Assert.Null(h.App.ApplySettings(draft));

            Assert.Equal(1, h.App.Config.RestoreModeId);
            Assert.Equal("省電", h.App.Config.FindMode(h.App.Config.RestoreModeId)!.Name);
            Assert.Equal(1, Reread(h).RestoreModeId);
        }

        [Fact]
        public void 復原模式被刪除時改用順序第一個一般模式()
        {
            var h = WithRemote(Play);
            Switch(h, Save);
            Switch(h, Remote);
            AppConfig draft = h.App.Config.Clone();

            draft.RemoveMode(Save);
            Assert.Null(h.App.ApplySettings(draft));

            Assert.Equal("遊玩", h.App.Config.RestoreTarget()!.Name);
            Assert.Equal(Play, Reread(h).RestoreModeId);
        }

        // ---- 程式啟動時 ----

        [Fact]
        public void 程式啟動時目前模式是潛行模式就改套用復原模式()
        {
            var h = WithRemote(Remote, "RestoreMode=2");

            Assert.Equal("省電", Assert.Single(h.Monitors.AppliedTargets));
            Assert.Null(h.Monitors.AppliedVirtual.Single());
            Assert.Contains("目前模式是潛行模式「潛行」，改套用復原模式「省電」", h.Notifier.Shown.First().Text);
            h.FinishSwitch();

            Assert.Equal(Save, h.App.Config.CurrentModeId);
            Assert.Equal(0, h.Virtual.Creates);   // 不會又自動進入潛行模式
        }

        [Fact]
        public void 程式啟動時目前模式是一般模式就不套用任何模式()
        {
            var h = WithRemote(Save, "RestoreMode=2");

            Assert.Empty(h.Monitors.Applied);
        }

        [Fact]
        public void 程式啟動時移除上次留下的虛擬螢幕_等Windows改完配置再套用復原模式()
        {
            var h = new Harness(WithRemote(Remote, "RestoreMode=2").Store.Text, virtualDisplay: v => v.Exists = true);

            Assert.Equal(new[] { "移除" }, h.Log);
            Assert.Empty(h.Monitors.Applied);
            h.Run(2);
            Assert.Equal("省電", Assert.Single(h.Monitors.AppliedTargets));
        }

        [Fact]
        public void 復原模式不存在時使用第一個一般模式()
        {
            var h = WithRemote(Remote, "RestoreMode=9");

            Assert.Equal("遊玩", Assert.Single(h.Monitors.AppliedTargets));
        }

        [Fact]
        public void 復原模式套用後沒有螢幕會開著時_開啟所有接上的螢幕()
        {
            var lg = Mon.Off("LG01", new VideoMode(2560, 1440, 144));
            string cfg = Cfg("[General]", "CurrentMode=2", "RestoreMode=1",
                             "[Mode.1]", "Name=全關", "UnlistedMonitors=Off",
                             "[Mode.1.Monitor.1]", "Monitor=" + DefaultMonitor.Id, "Enabled=0", "Width=1920", "Height=1080", "RefreshRate=60") +
                         RemoteMode(2, "潛行", 1920, 1080, 60) + Cfg("[Switch.1]") + BuiltIn(2, 1);

            var h = new Harness(cfg, monitors: new[] { DefaultMonitor, lg });

            Assert.Equal("全關", Assert.Single(h.Monitors.AppliedTargets));
            Assert.Equal(new[] { (true, 0, true), (true, 1920, false) }, h.LastApplied.Select(s => (s.Enabled, s.X, s.Primary)));
            Assert.Equal(2560, h.LastApplied[1].Width);   // 關閉中的螢幕用原生解析度
        }

        [Fact]
        public void 沒有一般模式的cfg讀取時以目前螢幕狀態補上預設模式_並當成復原模式()
        {
            var h = new Harness(Cfg("[General]", "CurrentMode=1") + RemoteMode(1, "潛行", 1920, 1080, 60));

            Assert.Equal(new[] { "潛行", "預設" }, h.App.Config.Modes.Select(m => m.Name));
            Assert.Contains("設定檔中沒有一般模式", h.Notifier.Shown.First().Text);
            Assert.Equal("預設", Assert.Single(h.Monitors.AppliedTargets));
            Assert.Equal(SwitchCondition.BuiltInFor(1, 2), Assert.Single(h.App.Config.SwitchConditions));
        }

        // ---- 虛擬螢幕意外消失 ----

        static Harness InRemote(params string[] extra)
        {
            var h = WithRemote(Play, extra);
            Switch(h, Save);
            Switch(h, Remote);
            h.Run(10);   // 度過切換後的緩衝期
            return h;
        }

        [Fact]
        public void 虛擬螢幕意外消失時切回復原模式並通知_不自動重建()
        {
            var h = InRemote();
            h.Log.Clear();

            h.Virtual.Vanish();
            h.Run(1);
            Assert.True(h.App.IsSwitching);   // 先等 Windows 依記憶改完配置
            Assert.Empty(h.Log);
            h.Run(2);
            Assert.Equal(new[] { "套用" }, h.Log);   // 不移除（已經不在）、不重建
            Assert.Equal("省電", h.Monitors.AppliedTargets.Last());
            h.FinishSwitch();

            Assert.Equal(Save, h.App.Config.CurrentModeId);
            var (text, kind) = h.Notifier.Shown.Last();
            Assert.Equal(NotificationKind.Warning, kind);
            Assert.Contains("虛擬螢幕意外消失", text);
            Assert.Contains("已切回復原模式「省電」", text);
            int pings = h.Virtual.Pings;
            h.Run(20);
            Assert.Equal(pings, h.Virtual.Pings);
            Assert.Equal(1, h.Virtual.Creates);
        }

        [Fact]
        public void 關閉通知時虛擬螢幕意外消失仍然通知()
        {
            var h = InRemote("ShowNotifications=0");

            h.Virtual.Vanish();
            h.Run(3);
            h.FinishSwitch();

            Assert.Contains("虛擬螢幕意外消失", h.Notifier.Shown.Last().Text);
        }

        [Fact]
        public void 切回復原模式失敗時維持原本的模式並通知_一分鐘後再試()
        {
            var h = InRemote();
            h.Virtual.Vanish();
            h.Run(3);

            h.FinishSwitch("無法開關螢幕");

            Assert.Equal(Remote, h.App.Config.CurrentModeId);
            Assert.Contains("無法切回復原模式「省電」", h.Notifier.Shown.Last().Text);
            int applied = h.Monitors.Applied.Count;
            h.Run(50);
            Assert.Equal(applied, h.Monitors.Applied.Count);
            Assert.True(h.RunUntilSwitch(20) > 0);
        }

        [Fact]
        public void 重新載入cfg後目前模式不是潛行模式卻留著虛擬螢幕_就重新套用目前模式並移除虛擬螢幕()
        {
            var h = InRemote();
            h.Store.Text = h.Store.Text!.Replace("Name=潛行\r\nKind=Remote", "Name=潛行\r\nKind=Normal");
            h.App.ReloadConfig();
            h.Log.Clear();

            h.Run(1);
            Assert.Equal(new[] { "套用" }, h.Log);
            h.FinishSwitch();
            Assert.Equal(new[] { "套用", "移除" }, h.Log);
        }
    }
}
