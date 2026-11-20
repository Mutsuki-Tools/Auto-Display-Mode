using AutoDisplayMode.Core;
using Xunit;

namespace AutoDisplayMode.Core.Tests
{
    public class ConfigTests
    {
        [Fact]
        public void cfg不存在時以預設值建立()
        {
            var h = new Harness(cfg: null);

            Assert.NotNull(h.Store.Text);
            Assert.Contains("BatPath=C:\\multimonitortool-x64\\monitormode.bat\r\n", h.Store.Text);
            Assert.Contains("AutoMode=1\r\n", h.Store.Text);
            Assert.Contains("IdleMinutes=10\r\n", h.Store.Text);
            Assert.Contains("LastMode=home\r\n", h.Store.Text);
            Assert.StartsWith("# 省電螢幕工具 設定檔\r\n", h.Store.Text);
            Assert.Equal(Mode.Home, h.App.CurrentMode);
        }

        [Fact]
        public void 讀取cfg的各項設定()
        {
            var h = new Harness(Harness.Cfg(
                "# 註解",
                "; 也是註解",
                "  batpath = \"D:\\tools\\switch.bat\"  ",
                "HomeArgs=play",
                "AwayArgs=save",
                "AutoMode=off",
                "IdleMinutes=30",
                "LastMode=AWAY",
                "AudioDetect=no",
                "AudioSeconds=7",
                "AudioThreshold=0.25",
                "MouseMoveThreshold=4",
                "IgnoreRemoteInput=false",
                "ShowNotifications=0",
                "Unknown=1"));

            var c = h.App.Config;
            Assert.Equal("D:\\tools\\switch.bat", c.BatPath);
            Assert.Equal("play", c.HomeArgs);
            Assert.Equal("save", c.AwayArgs);
            Assert.False(c.AutoSwitch);
            Assert.Equal(30, c.IdleMinutes);
            Assert.Equal(Mode.Away, h.App.CurrentMode);
            Assert.False(c.AudioDetect);
            Assert.Equal(7, c.AudioSeconds);
            Assert.Equal(0.25, c.AudioThreshold);
            Assert.Equal(4, c.MouseMoveThreshold);
            Assert.False(c.IgnoreRemoteInput);
            Assert.False(c.ShowNotifications);
        }

        [Fact]
        public void 不合理的數值改用預設值()
        {
            var h = new Harness(Harness.Cfg("IdleMinutes=0", "AudioSeconds=-1", "MouseMoveThreshold=0", "LastMode=garbage", "AutoMode=maybe"));

            Assert.Equal(10, h.App.Config.IdleMinutes);
            Assert.Equal(3, h.App.Config.AudioSeconds);
            Assert.Equal(1, h.App.Config.MouseMoveThreshold);
            Assert.Equal(Mode.Home, h.App.CurrentMode);
            Assert.True(h.App.Config.AutoSwitch);
        }

        [Fact]
        public void cfg讀取失敗時使用預設值且不覆寫檔案()
        {
            var store = new FakeStore("IdleMinutes=30\r\n") { FailReads = true };
            var clock = new FakeClock();
            var app = new AppCoordinator(store, new FakeSwitcher(), new FakeSignals(clock), clock, new FakeStartup(), new FakeNotifier());

            Assert.Equal(10, app.Config.IdleMinutes);
            Assert.Equal(0, store.Writes);
        }

        [Fact]
        public void 切換模式時只更新LastMode其餘內容保持不變()
        {
            string original = Harness.Cfg("# 我的註解", "IdleMinutes=5", "LastMode=home", "HomeArgs=play   ", "", "# 結尾");
            var h = new Harness(original);
            h.Store.Text = h.Store.Text!.Replace("HomeArgs=play   ", "HomeArgs=edited");   // 使用者改了但還沒重新載入

            h.RunUntilSwitch(10 * 60);

            Assert.Equal(Harness.Cfg("# 我的註解", "IdleMinutes=5", "LastMode=away", "HomeArgs=edited", "", "# 結尾"), h.Store.Text);
        }

        [Fact]
        public void 從選單修改的設定會寫入cfg缺少的項目附加在最後()
        {
            var h = new Harness(Harness.Cfg("BatPath=C:\\a.bat"));

            h.App.SetIdleMinutes(30);
            h.App.SetAudioDetect(false);
            h.App.SetAutoSwitch(false);

            Assert.Equal(Harness.Cfg("BatPath=C:\\a.bat", "IdleMinutes=30", "AudioDetect=0", "AutoMode=0"), h.Store.Text);
        }

        [Fact]
        public void 手動切換會把自動切換與模式寫入cfg()
        {
            var h = new Harness(Harness.Cfg("AutoMode=1", "LastMode=home"));

            h.App.ManualSwitch(Mode.Away, autoSwitch: false);

            Assert.Equal(Harness.Cfg("AutoMode=0", "LastMode=away"), h.Store.Text);
        }

        [Fact]
        public void 修改閒置時間後以新的時間判斷()
        {
            var h = new Harness(Harness.Cfg("IdleMinutes=10", "LastMode=home"));

            h.App.SetIdleMinutes(5);

            Assert.Equal(5 * 60, h.RunUntilSwitch(20 * 60));
        }

        [Fact]
        public void 重新載入cfg套用新設定但維持目前模式()
        {
            var h = new Harness(Harness.Cfg("IdleMinutes=10", "LastMode=home"));
            h.Store.Text = Harness.Cfg("IdleMinutes=5", "LastMode=away");

            h.App.ReloadConfig();

            Assert.Equal(Mode.Home, h.App.CurrentMode);
            Assert.Equal(5, h.App.Config.IdleMinutes);
            Assert.Empty(h.Switcher.Started);
            Assert.Contains(h.Notifier.Shown, n => n.Kind == NotificationKind.Info && n.Text.StartsWith("已重新載入設定檔。"));
            Assert.Equal(5 * 60, h.RunUntilSwitch(20 * 60));
        }

        [Fact]
        public void 重新載入時找不到bat會提醒()
        {
            var h = new Harness(Harness.Cfg("LastMode=home"));
            h.Switcher.Ready = false;

            h.App.ReloadConfig();

            Assert.Contains(h.Notifier.Shown, n => n.Kind == NotificationKind.Warning && n.Text.Contains("找不到 bat 檔"));
        }

        [Fact]
        public void 開啟cfg前若檔案不存在先建立()
        {
            var h = new Harness(Harness.Cfg("IdleMinutes=30"));
            h.Store.Text = null;

            h.App.EnsureConfigFileExists();

            Assert.Contains("IdleMinutes=30\r\n", h.Store.Text);
        }

        [Fact]
        public void 啟動時跳出目前狀態通知()
        {
            var h = new Harness(Harness.Cfg("IdleMinutes=30", "LastMode=away", "AutoMode=0", "AudioDetect=1"));

            Assert.Equal(("省電螢幕工具已啟動。\n目前：省電模式｜自動模式：關\n閒置 30 分鐘切換省電｜聲音偵測：開", NotificationKind.Info),
                Assert.Single(h.Notifier.Shown));
        }

        // ---- 開機啟動 ----

        [Fact]
        public void 切換開機啟動()
        {
            var h = new Harness(Harness.Cfg());

            h.App.ToggleStartup();
            Assert.True(h.App.StartupEnabled);

            h.App.ToggleStartup();
            Assert.False(h.App.StartupEnabled);
        }

        [Fact]
        public void 無法修改開機啟動時跳出錯誤通知()
        {
            var h = new Harness(Harness.Cfg());
            h.Startup.FailWith = "拒絕存取";

            h.App.ToggleStartup();

            Assert.False(h.App.StartupEnabled);
            Assert.Contains(("無法修改開機啟動設定：拒絕存取", NotificationKind.Error), h.Notifier.Shown);
        }
    }
}
