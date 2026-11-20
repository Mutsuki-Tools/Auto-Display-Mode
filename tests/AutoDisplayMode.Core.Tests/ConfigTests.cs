using System.Linq;
using AutoDisplayMode.Core;
using Xunit;

namespace AutoDisplayMode.Core.Tests
{
    public class ConfigTests
    {
        /// <summary>手寫的新格式 cfg：非預設的一般設定、兩個模式、各種螢幕設定。</summary>
        static readonly string Handwritten = Harness.Cfg(
            "# 使用者的註解",
            "[General]",
            "AutoSwitch=0",
            "CurrentMode=7",
            "MouseMoveThreshold=25",
            "AudioThreshold=0.125",
            "AudioSeconds=5",
            "IgnoreRemoteInput=0",
            "ShowNotifications=0",
            "StartWithWindows=1",
            "",
            "[LegacySwitch]",
            "BatPath=D:\\tools\\switch.bat",
            "HomeArgs=play",
            "AwayArgs=save",
            "IdleMinutes=30",
            "AudioDetect=0",
            "LastMode=away",
            "",
            "[Mode.3]",
            "Name=遊玩",
            "[Mode.3.Monitor.1]",
            "Monitor=DEL/A0F4/SN:ABC 123",
            "Name=DELL U2720Q",
            "Enabled=1",
            "X=0",
            "Y=0",
            "Width=3840",
            "Height=2160",
            "RefreshRate=60",
            "Orientation=Landscape",
            "Primary=1",
            "[Mode.3.Monitor.2]",
            "Monitor=GSM/5B7F/PORT:HDMI1",
            "Name=LG",
            "Enabled=0",
            "X=-1080",
            "Y=-200",
            "Width=1920",
            "Height=1080",
            "RefreshRate=144",
            "Orientation=PortraitFlipped",
            "Primary=0",
            "",
            "[Mode.7]",
            "Name=省電",
            "[Mode.7.Monitor.1]",
            "Monitor=DEL/A0F4/SN:ABC 123",
            "Enabled=1",
            "Width=1920",
            "Height=1080",
            "RefreshRate=60",
            "Primary=1");

        // ---- 讀取與寫出 ----

        [Fact]
        public void 讀取新格式cfg的各項設定()
        {
            var h = new Harness(Handwritten);
            var c = h.App.Config;

            Assert.False(c.AutoSwitch);
            Assert.Equal(7, c.CurrentModeId);
            Assert.Equal("省電", h.App.CurrentModeName);
            Assert.Equal(25, c.MouseMoveThreshold);
            Assert.Equal(0.125, c.AudioThreshold);
            Assert.Equal(5, c.AudioSeconds);
            Assert.False(c.IgnoreRemoteInput);
            Assert.False(c.ShowNotifications);
            Assert.True(c.StartWithWindows);
            Assert.Equal("D:\\tools\\switch.bat", c.BatPath);
            Assert.Equal(30, c.IdleMinutes);
            Assert.Equal(Mode.Away, h.App.CurrentMode);

            Assert.Equal(new[] { (3, "遊玩"), (7, "省電") }, c.Modes.Select(m => (m.Id, m.Name)));
            Assert.Equal(new MonitorSettings
            {
                Id = MonitorId.Create("GSM", "5B7F", null, "HDMI1"), Name = "LG", Enabled = false,
                X = -1080, Y = -200, Width = 1920, Height = 1080, RefreshRate = 144, Orientation = Orientation.PortraitFlipped,
            }, c.Modes[0].Monitors[1]);
            Assert.Equal(MonitorId.Create("DEL", "A0F4", "ABC 123", "DP1"), c.Modes[1].Monitors[0].Id);
            Assert.Equal(0, h.Store.Writes);   // 新格式且有模式：啟動時不改寫
        }

        [Fact]
        public void cfg寫出再讀回結果相同()
        {
            var first = new Harness(Handwritten);
            first.App.SetAudioDetect(false);   // 觸發整份重寫
            string written = first.Store.Text!;

            var second = new Harness(written);
            second.App.SetAudioDetect(false);

            Assert.Equal(written, second.Store.Text);
            Assert.Equal(Describe(first.App.Config), Describe(second.App.Config));
            Assert.Equal(Describe(new Harness(Handwritten).App.Config), Describe(second.App.Config));
        }

        [Fact]
        public void 寫出的cfg附中文註解且不保留使用者自行加入的內容()
        {
            var h = new Harness(Handwritten);

            h.App.SetAutoSwitch(true);

            Assert.StartsWith("# 省電螢幕工具 設定檔\r\n", h.Store.Text);
            Assert.Contains("# 自動切換總開關（1=開，0=關）\r\nAutoSwitch=1\r\n", h.Store.Text);
            Assert.Contains("#  模式「遊玩」\r\n", h.Store.Text);
            Assert.Contains("[Mode.3.Monitor.2]\r\nMonitor=GSM/5B7F/PORT:HDMI1\r\n", h.Store.Text);
            Assert.DoesNotContain("# 使用者的註解", h.Store.Text);
        }

        [Fact]
        public void 不合理的數值改用預設值()
        {
            var h = new Harness(Harness.Cfg(
                "[General]", "AudioSeconds=-1", "MouseMoveThreshold=0", "AutoSwitch=maybe", "CurrentMode=99",
                "[LegacySwitch]", "IdleMinutes=0", "LastMode=garbage",
                "[Mode.2]", "Name=甲",
                "[Mode.2.Monitor.1]", "Monitor=亂寫", "Enabled=1",
                "[Mode.2.Monitor.2]", "Monitor=DEL/A0F4/SN:1", "Orientation=Sideways", "Width=abc",
                "[Mode.5]", "Name=",
                "[Mode.9.Monitor.1]", "Monitor=DEL/A0F4/SN:2"));
            var c = h.App.Config;

            Assert.Equal(3, c.AudioSeconds);
            Assert.Equal(1, c.MouseMoveThreshold);
            Assert.True(c.AutoSwitch);
            Assert.Equal(10, c.IdleMinutes);
            Assert.Equal(Mode.Home, h.App.CurrentMode);
            Assert.Equal(2, c.CurrentModeId);   // 找不到的模式改用第一個
            Assert.Equal(new[] { "甲", "模式 5" }, c.Modes.Select(m => m.Name));
            var m = Assert.Single(c.Modes[0].Monitors);   // 識別無法解析的螢幕設定略過
            Assert.Equal((Orientation.Landscape, 0), (m.Orientation, m.Width));
        }

        [Fact]
        public void 新格式cfg沒有任何模式時以目前螢幕狀態建立預設模式()
        {
            var h = new Harness(Harness.Cfg("[General]", "AutoSwitch=0"));

            var mode = Assert.Single(h.App.Config.Modes);
            Assert.Equal("預設", mode.Name);
            Assert.Equal(Harness.DefaultMonitor.Id, Assert.Single(mode.Monitors).Id);
            Assert.False(h.App.Config.AutoSwitch);
            Assert.Contains("Name=預設\r\n", h.Store.Text);
        }

        [Fact]
        public void 首次啟動時開機啟動狀態從登錄檔讀取()
        {
            var h = new Harness(cfg: null, startupEnabled: true);

            Assert.True(h.App.Config.StartWithWindows);
            Assert.Contains("StartWithWindows=1\r\n", h.Store.Text);
        }

        [Fact]
        public void 首次啟動偵測螢幕失敗時預設模式沒有螢幕設定並在啟動通知中說明()
        {
            var store = new FakeStore();
            var clock = new FakeClock();
            var notifier = new FakeNotifier();
            var app = new AppCoordinator(store, new FakeSwitcher(), new FakeMonitors { Fail = true }, new FakeSignals(clock),
                clock, new FakeStartup(), notifier);
            app.Start();

            Assert.Empty(Assert.Single(app.Config.Modes).Monitors);
            Assert.Contains("無法偵測螢幕", Assert.Single(notifier.Shown).Text);
        }

        [Fact]
        public void cfg讀取失敗時使用預設值且不覆寫檔案()
        {
            var store = new FakeStore(Harness.Cfg("[LegacySwitch]", "IdleMinutes=30")) { FailReads = true };
            var clock = new FakeClock();
            var app = new AppCoordinator(store, new FakeSwitcher(), new FakeMonitors(Harness.DefaultMonitor), new FakeSignals(clock),
                clock, new FakeStartup(), new FakeNotifier());

            app.SetAutoSwitch(false);

            Assert.Equal(10, app.Config.IdleMinutes);
            Assert.Equal("預設", app.CurrentModeName);
            Assert.Equal(0, store.Writes);
        }

        // ---- 舊格式轉移 ----

        static readonly string Legacy = Harness.Cfg(
            "# 省電螢幕工具 設定檔",
            "BatPath=D:\\tools\\switch.bat",
            "HomeArgs=play",
            "AwayArgs=save",
            "AutoMode=0",
            "IdleMinutes=5",
            "LastMode=away",
            "AudioDetect=0",
            "AudioSeconds=7",
            "AudioThreshold=0.25",
            "MouseMoveThreshold=4",
            "IgnoreRemoteInput=0",
            "ShowNotifications=0");

        [Fact]
        public void 舊格式cfg先備份再轉為新格式()
        {
            var h = new Harness(Legacy, startupEnabled: true);

            Assert.Equal(Legacy, h.Store.BackupText);
            Assert.Contains("[General]\r\n", h.Store.Text);
            Assert.Contains("Name=預設\r\n", h.Store.Text);
            Assert.DoesNotContain("AutoMode=", h.Store.Text);
            Assert.Contains("已將舊版設定檔轉換為新格式，原檔備份為 .bak。", h.Notifier.Shown.Single().Text);
        }

        [Fact]
        public void 舊格式cfg的指定值正確轉移_開機啟動從登錄檔沿用()
        {
            var h = new Harness(Legacy, startupEnabled: true);
            var c = new Harness(h.Store.Text).App.Config;   // 讀回轉出的新格式

            Assert.False(c.AutoSwitch);
            Assert.Equal(4, c.MouseMoveThreshold);
            Assert.Equal(0.25, c.AudioThreshold);
            Assert.Equal(7, c.AudioSeconds);
            Assert.False(c.IgnoreRemoteInput);
            Assert.False(c.ShowNotifications);
            Assert.True(c.StartWithWindows);
            var mode = Assert.Single(c.Modes);
            Assert.Equal(c.CurrentModeId, mode.Id);
            Assert.Equal(Harness.DefaultMonitor.Id, Assert.Single(mode.Monitors).Id);
        }

        [Fact]
        public void 舊版切換用的值在過渡期間保留()
        {
            var h = new Harness(Legacy);
            var c = new Harness(h.Store.Text).App.Config;

            Assert.Equal("D:\\tools\\switch.bat", c.BatPath);
            Assert.Equal("play", c.HomeArgs);
            Assert.Equal("save", c.AwayArgs);
            Assert.Equal(5, c.IdleMinutes);
            Assert.False(c.AudioDetect);
            Assert.Equal(Mode.Away, c.LastMode);
        }

        [Fact]
        public void 無法備份舊格式cfg時不改寫檔案()
        {
            var store = new FakeStore(Legacy) { FailBackup = true };
            var clock = new FakeClock();
            var notifier = new FakeNotifier();
            var app = new AppCoordinator(store, new FakeSwitcher(), new FakeMonitors(Harness.DefaultMonitor), new FakeSignals(clock),
                clock, new FakeStartup(), notifier);
            app.Start();
            app.SetAutoSwitch(true);

            Assert.Equal(Legacy, store.Text);
            Assert.Equal(0, store.Writes);
            Assert.False(app.Config.ShowNotifications);   // 仍以轉移後的設定執行
            Assert.Contains("無法備份舊版設定檔", Assert.Single(notifier.Shown).Text);
        }

        // ---- 從選單修改設定 ----

        [Fact]
        public void 從選單修改的設定會寫入cfg()
        {
            var h = new Harness(Handwritten);

            h.App.SetIdleMinutes(10);
            h.App.SetAudioDetect(true);
            h.App.SetAutoSwitch(true);

            var c = new Harness(h.Store.Text).App.Config;
            Assert.Equal(10, c.IdleMinutes);
            Assert.True(c.AudioDetect);
            Assert.True(c.AutoSwitch);
        }

        [Fact]
        public void 手動切換會把自動切換與模式寫入cfg()
        {
            var h = new Harness(Handwritten);

            h.App.ManualSwitch(Mode.Home, autoSwitch: true);

            var c = new Harness(h.Store.Text).App.Config;
            Assert.True(c.AutoSwitch);
            Assert.Equal(Mode.Home, c.LastMode);
        }

        [Fact]
        public void 修改閒置時間後以新的時間判斷()
        {
            var h = new Harness(Harness.Cfg("IdleMinutes=10", "LastMode=home"));

            h.App.SetIdleMinutes(5);

            Assert.Equal(5 * 60, h.RunUntilSwitch(20 * 60));
        }

        // ---- 重新載入 ----

        [Fact]
        public void 重新載入cfg套用新設定但維持目前模式()
        {
            var h = new Harness(Harness.Cfg("IdleMinutes=10", "LastMode=home"));
            h.Store.Text = Harness.Cfg("[LegacySwitch]", "IdleMinutes=5", "LastMode=away", "[Mode.1]", "Name=甲", "[Mode.2]", "Name=乙");

            h.App.ReloadConfig();

            Assert.Equal(Mode.Home, h.App.CurrentMode);
            Assert.Equal(5, h.App.Config.IdleMinutes);
            Assert.Equal(new[] { "甲", "乙" }, h.App.Config.Modes.Select(m => m.Name));
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
            var h = new Harness(Handwritten);
            h.Store.Text = null;

            h.App.EnsureConfigFileExists();

            Assert.Contains("[Mode.7]\r\n", h.Store.Text);
            Assert.Contains("Name=省電\r\n", h.Store.Text);
        }

        [Fact]
        public void 啟動時跳出目前狀態通知()
        {
            var h = new Harness(Harness.Cfg(
                "[General]", "AutoSwitch=0", "[LegacySwitch]", "IdleMinutes=30", "LastMode=away", "AudioDetect=1", "[Mode.1]", "Name=甲"));

            Assert.Equal(("省電螢幕工具已啟動。\n目前：省電模式｜自動模式：關\n閒置 30 分鐘切換省電｜聲音偵測：開", NotificationKind.Info),
                Assert.Single(h.Notifier.Shown));
        }

        // ---- 開機啟動 ----

        [Fact]
        public void 切換開機啟動並寫入cfg()
        {
            var h = new Harness(Handwritten);

            h.App.ToggleStartup();
            Assert.True(h.App.StartupEnabled);
            Assert.Contains("StartWithWindows=1\r\n", h.Store.Text);

            h.App.ToggleStartup();
            Assert.False(h.App.StartupEnabled);
            Assert.Contains("StartWithWindows=0\r\n", h.Store.Text);
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

        /// <summary>把設定轉成可比較的文字（一般設定、舊版切換、每個模式與螢幕設定）。</summary>
        static string Describe(AppConfig c) =>
            string.Join("|", c.AutoSwitch, c.CurrentModeId, c.MouseMoveThreshold, c.AudioThreshold, c.AudioSeconds,
                c.IgnoreRemoteInput, c.ShowNotifications, c.StartWithWindows,
                c.BatPath, c.HomeArgs, c.AwayArgs, c.IdleMinutes, c.AudioDetect, c.LastMode) +
            string.Concat(c.Modes.Select(m => "\n" + m.Id + ":" + m.Name + string.Concat(m.Monitors.Select(s => "\n  " + s))));
    }
}
