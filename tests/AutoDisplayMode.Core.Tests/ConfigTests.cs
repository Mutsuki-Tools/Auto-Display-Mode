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
            "HomeMode=3",
            "AwayMode=7",
            "IdleMinutes=30",
            "AudioDetect=0",
            "",
            "[Mode.3]",
            "Name=遊玩",
            "Color=#12aB0f",
            "ManualAutoSwitch=on",
            "UnlistedMonitors=Off",
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
            Assert.Equal(30, c.IdleMinutes);
            Assert.False(c.AudioDetect);
            Assert.Equal((3, 7), (c.HomeModeId, c.AwayModeId));

            Assert.Equal(new[] { (3, "遊玩"), (7, "省電") }, c.Modes.Select(m => (m.Id, m.Name)));
            Assert.Equal((0x12AB0F, ManualAutoSwitch.On, UnlistedMonitors.Off),
                (c.Modes[0].Color, c.Modes[0].ManualAutoSwitch, c.Modes[0].UnlistedMonitors));
            Assert.Equal((ModeConfig.Palette[0], ManualAutoSwitch.Keep, UnlistedMonitors.Keep),   // 沒寫的用預設值，顏色自動配
                (c.Modes[1].Color, c.Modes[1].ManualAutoSwitch, c.Modes[1].UnlistedMonitors));
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
                "[LegacySwitch]", "IdleMinutes=0", "HomeMode=99", "AwayMode=abc",
                "[Mode.2]", "Name=甲", "Color=red", "ManualAutoSwitch=Sometimes", "UnlistedMonitors=2",
                "[Mode.2.Monitor.1]", "Monitor=亂寫", "Enabled=1",
                "[Mode.2.Monitor.2]", "Monitor=DEL/A0F4/SN:1", "Orientation=Sideways", "Width=abc",
                "[Mode.5]", "Name=",
                "[Mode.9.Monitor.1]", "Monitor=DEL/A0F4/SN:2"));
            var c = h.App.Config;

            Assert.Equal(3, c.AudioSeconds);
            Assert.Equal(1, c.MouseMoveThreshold);
            Assert.True(c.AutoSwitch);
            Assert.Equal(10, c.IdleMinutes);
            Assert.Equal((0, 0), (c.HomeModeId, c.AwayModeId));   // 指向不存在的模式時清除
            Assert.Equal(2, c.CurrentModeId);   // 找不到的模式改用第一個
            Assert.Equal(new[] { "甲", "模式 5" }, c.Modes.Select(m => m.Name));
            var m = Assert.Single(c.Modes[0].Monitors);   // 識別無法解析的螢幕設定略過
            Assert.Equal((Orientation.Landscape, 0), (m.Orientation, m.Width));
            Assert.Equal((ModeConfig.Palette[0], ManualAutoSwitch.Keep, UnlistedMonitors.Keep),
                (c.Modes[0].Color, c.Modes[0].ManualAutoSwitch, c.Modes[0].UnlistedMonitors));
            Assert.Equal(ModeConfig.Palette[1], c.Modes[1].Color);
        }

        [Fact]
        public void 沒有指定顏色的模式自動配上未被使用的顏色()
        {
            var h = new Harness(Harness.Cfg(
                "[Mode.1]", "Name=甲", "[Mode.2]", "Name=乙", "Color=#" + ModeConfig.Palette[0].ToString("X6"), "[Mode.3]", "Name=丙"));

            Assert.Equal(new[] { ModeConfig.Palette[1], ModeConfig.Palette[0], ModeConfig.Palette[2] },
                h.App.Config.Modes.Select(m => m.Color));
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
            var app = new AppCoordinator(store, new FakeMonitors { Fail = true }, new FakeSignals(clock),
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
            var app = new AppCoordinator(store, new FakeMonitors(Harness.DefaultMonitor), new FakeSignals(clock),
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
        public void 舊版切換條件的值在過渡期間保留_bat相關的值捨棄()
        {
            var h = new Harness(Legacy);
            var c = new Harness(h.Store.Text).App.Config;

            Assert.Equal(5, c.IdleMinutes);
            Assert.False(c.AudioDetect);
            Assert.Equal((0, c.CurrentModeId), (c.HomeModeId, c.AwayModeId));   // LastMode=away：「預設」模式扮演「省電」
            Assert.DoesNotContain("BatPath", h.Store.Text);
            Assert.DoesNotContain("HomeArgs", h.Store.Text);
            Assert.DoesNotContain("AwayArgs", h.Store.Text);
            Assert.DoesNotContain("LastMode", h.Store.Text);
        }

        [Fact]
        public void 票02格式的cfg啟動時就以LastMode轉移成舊版切換條件指向的模式並移除bat相關的鍵()
        {
            var h = new Harness(Harness.Cfg(
                "[General]", "CurrentMode=4",
                "[LegacySwitch]", "BatPath=C:\\m.bat", "HomeArgs=home", "AwayArgs=away", "IdleMinutes=7", "LastMode=home",
                "[Mode.4]", "Name=預設"));

            Assert.Equal((4, 0), (h.App.Config.HomeModeId, h.App.Config.AwayModeId));
            Assert.Equal(1, h.Store.Writes);   // 啟動時就改寫，不必等下次存檔
            Assert.Contains("HomeMode=4\r\nAwayMode=0\r\n", h.Store.Text);
            Assert.Contains("IdleMinutes=7\r\n", h.Store.Text);
            Assert.DoesNotContain("BatPath", h.Store.Text);
            Assert.DoesNotContain("LastMode", h.Store.Text);
            Assert.Contains("已將設定檔更新為新版格式", Assert.Single(h.Notifier.Shown).Text);
        }

        [Fact]
        public void 只有bat相關的過時鍵時啟動也會改寫()
        {
            var h = new Harness(Harness.Cfg(
                "[LegacySwitch]", "HomeMode=1", "AwayMode=2", "BatPath=C:\\m.bat", "[Mode.1]", "Name=甲", "[Mode.2]", "Name=乙"));

            Assert.Equal(1, h.Store.Writes);
            Assert.DoesNotContain("BatPath", h.Store.Text);
            Assert.Equal((1, 2), (h.App.Config.HomeModeId, h.App.Config.AwayModeId));
        }

        [Fact]
        public void 舊版切換條件的兩個角色指向同一個模式時清除省電()
        {
            var h = new Harness(Harness.Cfg("[LegacySwitch]", "HomeMode=1", "AwayMode=1", "[Mode.1]", "Name=甲"));

            Assert.Equal((1, 0), (h.App.Config.HomeModeId, h.App.Config.AwayModeId));
        }

        [Fact]
        public void 無法備份舊格式cfg時不改寫檔案()
        {
            var store = new FakeStore(Legacy) { FailBackup = true };
            var clock = new FakeClock();
            var notifier = new FakeNotifier();
            var app = new AppCoordinator(store, new FakeMonitors(Harness.DefaultMonitor), new FakeSignals(clock),
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
            var h = new Harness(Handwritten, monitors: new[] { Mon.On("ABC 123", 0, 0, 3840, 2160, 60, primary: true) });

            h.App.ManualSwitch(3);   // 「遊玩」的 ManualAutoSwitch=on
            h.FinishSwitch();

            var c = new Harness(h.Store.Text).App.Config;
            Assert.True(c.AutoSwitch);
            Assert.Equal(3, c.CurrentModeId);
        }

        [Fact]
        public void 修改閒置時間後以新的時間判斷()
        {
            var h = Harness.TwoModes("home", "IdleMinutes=10");

            h.App.SetIdleMinutes(5);

            Assert.Equal(5 * 60, h.RunUntilSwitch(20 * 60));
        }

        // ---- 重新載入 ----

        [Fact]
        public void 重新載入cfg套用新設定但維持目前模式()
        {
            var h = Harness.TwoModes("home", "IdleMinutes=10");
            h.Store.Text = Harness.Cfg("[General]", "CurrentMode=2", "[LegacySwitch]", "HomeMode=1", "AwayMode=2", "IdleMinutes=5",
                "[Mode.1]", "Name=甲", "[Mode.2]", "Name=乙",
                "[Mode.2.Monitor.1]", "Monitor=" + Harness.DefaultMonitor.Id, "Enabled=1", "Primary=1");

            h.App.ReloadConfig();

            Assert.Equal(1, h.App.Config.CurrentModeId);   // 程式記錄的目前模式仍存在：維持
            Assert.Equal(5, h.App.Config.IdleMinutes);
            Assert.Equal(new[] { "甲", "乙" }, h.App.Config.Modes.Select(m => m.Name));
            Assert.Empty(h.Monitors.Applied);
            Assert.Contains(h.Notifier.Shown, n => n.Kind == NotificationKind.Info && n.Text.StartsWith("已重新載入設定檔。"));
            Assert.Equal(5 * 60, h.RunUntilSwitch(20 * 60));
            Assert.Equal("乙", h.Monitors.AppliedTargets.Single());
        }

        [Fact]
        public void 重新載入後程式記錄的目前模式不存在時改用cfg的目前模式()
        {
            var h = Harness.TwoModes("away");
            h.Store.Text = Harness.Cfg("[General]", "CurrentMode=5", "[Mode.5]", "Name=新", "[Mode.6]", "Name=另一個");

            h.App.ReloadConfig();

            Assert.Equal("新", h.App.CurrentModeName);
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
                "[General]", "AutoSwitch=0", "CurrentMode=2",
                "[LegacySwitch]", "HomeMode=1", "AwayMode=2", "IdleMinutes=30", "AudioDetect=1",
                "[Mode.1]", "Name=甲", "[Mode.2]", "Name=乙"));

            Assert.Equal(("省電螢幕工具已啟動。\n目前：乙｜自動切換：關\n「甲」閒置 30 分鐘 →「乙」；「乙」偵測到使用 →「甲」｜聲音偵測：開",
                    NotificationKind.Info),
                Assert.Single(h.Notifier.Shown));
        }

        [Fact]
        public void 啟動通知說明舊版切換條件未設定()
        {
            var h = new Harness(Harness.Cfg("[General]", "AutoSwitch=1", "[Mode.1]", "Name=預設"));

            Assert.Equal("省電螢幕工具已啟動。\n目前：預設｜自動切換：開\n舊版切換條件未設定（cfg 的 HomeMode／AwayMode）",
                Assert.Single(h.Notifier.Shown).Text);
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
                c.IdleMinutes, c.AudioDetect, c.HomeModeId, c.AwayModeId) +
            string.Concat(c.Modes.Select(m => "\n" + m.Id + ":" + m.Name + ":" + m.Color + ":" + m.ManualAutoSwitch + ":" + m.UnlistedMonitors +
                string.Concat(m.Monitors.Select(s => "\n  " + s))));
    }
}
