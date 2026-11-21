using System.Linq;
using AutoDisplayMode.Core;
using Xunit;
using static AutoDisplayMode.Core.Tests.Harness;

namespace AutoDisplayMode.Core.Tests
{
    /// <summary>潛行模式：進入、離開、潛行模式之間的切換、心跳、拒絕切換，以及處於潛行模式時的限制。</summary>
    public class RemoteModeTests
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
            h.FinishSwitch();     // 依一般模式開啟螢幕
            h.Run(2);             // 等 Windows 依記憶改完配置
            h.FinishSwitch();     // 重新套用
        }

        // ---- 進入 ----

        [Fact]
        public void 進入潛行模式時先建立虛擬螢幕再關閉所有螢幕()
        {
            var h = WithRemote(Play);

            h.App.ManualSwitch(Remote);

            Assert.Equal(new[] { "建立 1920x1080@60", "套用 只開虛擬螢幕 1920x1080@60" }, h.Log);
            Assert.All(h.LastApplied, s => Assert.False(s.Enabled));
            Assert.Equal(Play, h.App.Config.CurrentModeId);   // 套用完成前不改變目前模式

            h.FinishSwitch();
            Assert.Equal(Remote, h.App.Config.CurrentModeId);
            Assert.True(h.App.InRemoteMode);
            Assert.Contains("CurrentMode=4\r\n", h.Store.Text);
        }

        [Fact]
        public void 驅動不可用時拒絕切換並通知_維持原本的模式()
        {
            var h = WithRemote(Play);
            h.Virtual.CreateError = "找不到虛擬螢幕驅動";

            h.App.ManualSwitch(Remote);

            Assert.Empty(h.Monitors.Applied);
            Assert.False(h.App.IsSwitching);
            Assert.Equal(Play, h.App.Config.CurrentModeId);
            var (text, kind) = h.Notifier.Shown.Last();
            Assert.Equal(NotificationKind.Warning, kind);
            Assert.Contains("無法切換到潛行模式「潛行」，維持原本的模式", text);
            Assert.Contains("找不到虛擬螢幕驅動", text);
        }

        [Fact]
        public void 套用只開虛擬螢幕失敗時移除虛擬螢幕並重新套用原本的模式()
        {
            var h = WithRemote(Play);
            h.App.ManualSwitch(Remote);

            h.FinishSwitch("虛擬螢幕沒有出現");

            Assert.Equal("移除", h.Log.Last());
            Assert.True(h.App.IsSwitching);   // 等 Windows 依記憶改完配置
            h.Run(2);
            Assert.Equal("套用", h.Log.Last());
            Assert.Equal(1920, h.LastApplied.Single().Width);   // 原本的「遊玩」
            h.FinishSwitch();

            Assert.False(h.App.IsSwitching);
            Assert.Equal(Play, h.App.Config.CurrentModeId);
            var (text, kind) = h.Notifier.Shown.Last();
            Assert.Equal(NotificationKind.Error, kind);
            Assert.Contains("無法切換到潛行模式「潛行」，維持原本的模式：\n虛擬螢幕沒有出現", text);
        }

        [Fact]
        public void 只在進入潛行模式時建立虛擬螢幕()
        {
            var h = WithRemote(Play);

            h.App.ManualSwitch(Save);
            h.FinishSwitch();
            h.Run(60);

            Assert.Equal(0, h.Virtual.Creates);
            Assert.All(h.Monitors.AppliedVirtual, v => Assert.Null(v));
        }

        // ---- 離開 ----

        [Fact]
        public void 離開潛行模式時先開啟螢幕再移除虛擬螢幕_之後重新套用一次()
        {
            var h = InRemote();
            h.Log.Clear();

            h.App.ManualSwitch(Save);
            Assert.Equal(new[] { "套用" }, h.Log);
            Assert.True(h.Virtual.Exists);   // 開啟螢幕時虛擬螢幕還在
            Assert.True(h.LastApplied.Single().Enabled);

            h.FinishSwitch();
            Assert.Equal(new[] { "套用", "移除" }, h.Log);
            Assert.True(h.App.IsSwitching);
            Assert.Equal(Remote, h.App.Config.CurrentModeId);

            h.Run(2);
            Assert.Equal(new[] { "套用", "移除", "套用" }, h.Log);
            Assert.Equal(1280, h.LastApplied.Single().Width);
            h.FinishSwitch();

            Assert.False(h.App.IsSwitching);
            Assert.Equal(Save, h.App.Config.CurrentModeId);
            Assert.False(h.App.InRemoteMode);
        }

        [Fact]
        public void 離開潛行模式時開啟螢幕失敗就維持潛行模式_虛擬螢幕保留()
        {
            var h = InRemote();

            h.App.ManualSwitch(Save);
            h.FinishSwitch("無法開關螢幕");

            Assert.False(h.App.IsSwitching);
            Assert.Equal(Remote, h.App.Config.CurrentModeId);
            Assert.True(h.Virtual.Exists);
            Assert.Equal(0, h.Virtual.Removes);
            int pings = h.Virtual.Pings;
            h.Run(3);
            Assert.Equal(pings + 3, h.Virtual.Pings);   // 心跳照送
        }

        [Fact]
        public void 離開潛行模式時目標模式套用後沒有螢幕會開著_改為開啟所有接上的螢幕()
        {
            // 「全關」記錄的螢幕沒接上、未記錄的螢幕關閉：照原本的規劃會被拒絕，人就困在全暗的潛行模式裡
            var lg = Mon.Off("LG01", new VideoMode(2560, 1440, 144));
            string cfg = Cfg("[General]", "CurrentMode=1",
                             "[Mode.1]", "Name=全關", "UnlistedMonitors=Off",
                             "[Mode.1.Monitor.1]", "Monitor=DEL/A0F4/SN:GONE", "Enabled=1", "Width=1920", "Height=1080", "RefreshRate=60", "Primary=1") +
                         RemoteMode(2, "潛行", 1920, 1080, 60) + Cfg("[Switch.1]") + BuiltIn(2, 1);
            var h = new Harness(cfg, monitors: new[] { lg });
            h.App.ManualSwitch(2);
            h.FinishSwitch();
            h.Run(10);

            h.Run(1, () => h.Signals.MoveMouse());

            Assert.Equal("全關", h.Monitors.AppliedTargets.Last());
            Assert.True(h.LastApplied.Single().Enabled);
            Assert.Contains("改為開啟所有接上的螢幕", h.Notifier.Shown.Last().Text);
            h.FinishSwitch();
            h.Run(2);
            Assert.True(h.LastApplied.Single().Enabled);   // 重新套用時也一樣
            h.FinishSwitch();
            Assert.Equal(1, h.App.Config.CurrentModeId);
        }

        [Fact]
        public void 查詢虛擬螢幕是否存在失敗時不當成意外消失()
        {
            var h = InRemote();
            h.Run(10);
            h.Virtual.ExistsError = true;

            h.Run(5);

            Assert.Single(h.Monitors.Applied);
            Assert.Equal(Remote, h.App.Config.CurrentModeId);
        }

        [Fact]
        public void 更新率59與60視為相同()
        {
            Assert.True(VirtualDisplayModes.SameRefreshRate(59, 60));
            Assert.True(VirtualDisplayModes.SameRefreshRate(60, 59));
            Assert.True(VirtualDisplayModes.SameRefreshRate(144, 144));
            Assert.False(VirtualDisplayModes.SameRefreshRate(60, 72));
            Assert.False(VirtualDisplayModes.SameRefreshRate(58, 59));
        }

        [Fact]
        public void 移除虛擬螢幕後重新套用失敗時仍算已切換_並通知錯誤()
        {
            var h = InRemote();

            h.App.ManualSwitch(Save);
            h.FinishSwitch();
            h.Run(2);
            h.FinishSwitch("顯示卡驅動程式無法套用");

            Assert.Equal(Save, h.App.Config.CurrentModeId);
            var (text, kind) = h.Notifier.Shown.Last();
            Assert.Equal(NotificationKind.Error, kind);
            Assert.Contains("已切換到「省電」，但移除虛擬螢幕後重新套用螢幕設定時失敗", text);
        }

        // ---- 潛行模式之間 ----

        [Fact]
        public void 潛行模式之間切換時只換解析度_不重新建立虛擬螢幕()
        {
            var h = InRemote();
            h.Log.Clear();

            h.App.ManualSwitch(Remote4K);
            h.FinishSwitch();

            Assert.Equal(new[] { "套用 只開虛擬螢幕 3840x2160@60" }, h.Log);
            Assert.Equal(Remote4K, h.App.Config.CurrentModeId);
            Assert.True(h.Virtual.Exists);
        }

        [Fact]
        public void 潛行模式之間切換失敗時維持原本的潛行模式()
        {
            var h = InRemote();

            h.App.ManualSwitch(Remote4K);
            h.FinishSwitch("螢幕不支援這個顯示模式");

            Assert.Equal(Remote, h.App.Config.CurrentModeId);
            Assert.True(h.Virtual.Exists);
            Assert.Equal(0, h.Virtual.Removes);
        }

        // ---- 心跳 ----

        [Fact]
        public void 處於潛行模式時每秒送心跳()
        {
            var h = InRemote();
            int before = h.Virtual.Pings;

            h.Run(5);

            Assert.Equal(before + 5, h.Virtual.Pings);
        }

        [Fact]
        public void 心跳一直送到虛擬螢幕移除為止_套用一般模式期間也送()
        {
            var h = InRemote();
            h.App.ManualSwitch(Save);

            int before = h.Virtual.Pings;
            h.Run(3);   // 套用一般模式中（SetDisplayConfig 可能要 1～2 秒）
            Assert.Equal(before + 3, h.Virtual.Pings);

            h.FinishSwitch();   // 開啟螢幕後移除虛擬螢幕
            before = h.Virtual.Pings;
            h.Run(5);
            Assert.Equal(before, h.Virtual.Pings);
        }

        [Fact]
        public void 建立虛擬螢幕後_套用完成前也送心跳()
        {
            var h = WithRemote(Play);
            h.App.ManualSwitch(Remote);

            h.Run(2);

            Assert.Equal(2, h.Virtual.Pings);
        }

        [Fact]
        public void 一般模式時不送心跳()
        {
            var h = WithRemote(Play);

            h.Run(5);

            Assert.Equal(0, h.Virtual.Pings);
        }

        // ---- 處於潛行模式時的限制 ----

        [Fact]
        public void 處於潛行模式時不能擷取()
        {
            var h = InRemote();

            Assert.NotNull(h.App.CaptureBlocker);
            h.App.CaptureToCurrentMode();
            Assert.Contains("處於潛行模式時無法擷取", h.Notifier.Shown.Last().Text);
            Assert.Null(h.App.CaptureForSettings(h.App.Config.FindMode(Play)!.Monitors));
            Assert.DoesNotContain("[Mode.4.Monitor", h.Store.Text);
        }

        [Fact]
        public void 一般模式時可以擷取()
        {
            var h = WithRemote(Play);

            Assert.Null(h.App.CaptureBlocker);
        }

        [Fact]
        public void 處於潛行模式時不能預覽螢幕配置()
        {
            var h = InRemote();

            Assert.Contains("處於潛行模式時無法預覽", h.App.PreviewLayout(h.App.Config.FindMode(Play)!.Clone()));
            Assert.Single(h.Monitors.Applied);
        }

        [Fact]
        public void 偵測與擷取不含虛擬螢幕_套用一般模式時虛擬螢幕不在狀態中()
        {
            // 虛擬螢幕由轉接層從偵測結果排除：核心只看到實體螢幕，所以套用一般模式時只有實體螢幕的狀態
            var h = InRemote();

            h.App.ManualSwitch(Play);

            Assert.Equal(new[] { DefaultMonitor.Id }, h.LastApplied.Select(s => s.Id));
            Assert.Null(h.Monitors.AppliedVirtual.Last());
        }

        [Fact]
        public void 處於潛行模式時摘要標示潛行()
        {
            var h = InRemote();

            Assert.StartsWith("目前：潛行（潛行模式）｜", h.App.StatusSummary);
        }
    }
}
