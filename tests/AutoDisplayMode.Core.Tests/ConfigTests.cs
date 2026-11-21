using System.Linq;
using AutoDisplayMode.Core;
using Xunit;

namespace AutoDisplayMode.Core.Tests
{
    public class ConfigTests
    {
        /// <summary>手寫的新格式 cfg：非預設的一般設定、兩條切換條件（分段序號與檔案中的順序相反）、兩個模式、各種螢幕設定。</summary>
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
            "[Switch.20]",
            "From=7",
            "Condition=usagedetected",
            "MouseKeyboard=1",
            "Audio=1",
            "To=3",
            "",
            "[Switch.10]",
            "From=3",
            "Condition=Idle",
            "IdleTime=2",
            "IdleUnit=hours",
            "MouseKeyboard=0",
            "Audio=1",
            "To=7",
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
            Assert.Equal(new[]   // 依分段序號排序，而不是檔案中的順序
            {
                new SwitchCondition
                {
                    FromModeId = 3, Kind = SwitchConditionKind.Idle, IdleAmount = 2, IdleUnit = IdleUnit.Hours,
                    Sources = UsageSources.Audio, ToModeId = 7,
                },
                new SwitchCondition
                {
                    FromModeId = 7, Kind = SwitchConditionKind.UsageDetected,
                    Sources = UsageSources.MouseKeyboard | UsageSources.Audio, ToModeId = 3,
                },
            }, c.SwitchConditions);

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
            first.App.SetAutoSwitch(false);   // 觸發整份重寫
            string written = first.Store.Text!;

            var second = new Harness(written);
            second.App.SetAutoSwitch(false);

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
            Assert.Contains(
                "# 切換條件 1：「遊玩」閒置 2 小時（聲音）→「省電」\r\n[Switch.1]\r\nFrom=3\r\nCondition=Idle\r\n" +
                "IdleTime=2\r\nIdleUnit=Hours\r\nMouseKeyboard=0\r\nAudio=1\r\nTo=7\r\n", h.Store.Text);
            Assert.Contains(   // 偵測到使用不寫閒置時間
                "# 切換條件 2：「省電」偵測到使用（滑鼠或鍵盤、聲音）→「遊玩」\r\n[Switch.2]\r\nFrom=7\r\nCondition=UsageDetected\r\n" +
                "MouseKeyboard=1\r\nAudio=1\r\nTo=3\r\n", h.Store.Text);
            Assert.DoesNotContain("# 使用者的註解", h.Store.Text);
        }

        [Fact]
        public void 不合理的數值改用預設值()
        {
            var h = new Harness(Harness.Cfg(
                "[General]", "AudioSeconds=-1", "MouseMoveThreshold=0", "AutoSwitch=maybe", "CurrentMode=99",
                "[Mode.2]", "Name=甲", "Color=red", "ManualAutoSwitch=Sometimes", "UnlistedMonitors=2",
                "[Mode.2.Monitor.1]", "Monitor=亂寫", "Enabled=1",
                "[Mode.2.Monitor.2]", "Monitor=DEL/A0F4/SN:1", "Orientation=Sideways", "Width=abc",
                "[Mode.5]", "Name=",
                "[Mode.9.Monitor.1]", "Monitor=DEL/A0F4/SN:2"));
            var c = h.App.Config;

            Assert.Equal(3, c.AudioSeconds);
            Assert.Equal(1, c.MouseMoveThreshold);
            Assert.True(c.AutoSwitch);
            Assert.Equal(2, c.CurrentModeId);   // 找不到的模式改用第一個
            Assert.Equal(new[] { "甲", "模式 5" }, c.Modes.Select(m => m.Name));
            var m = Assert.Single(c.Modes[0].Monitors);   // 識別無法解析的螢幕設定略過
            Assert.Equal((Orientation.Landscape, 0), (m.Orientation, m.Width));
            Assert.Equal((ModeConfig.Palette[0], ManualAutoSwitch.Keep, UnlistedMonitors.Keep),
                (c.Modes[0].Color, c.Modes[0].ManualAutoSwitch, c.Modes[0].UnlistedMonitors));
            Assert.Equal(ModeConfig.Palette[1], c.Modes[1].Color);
        }

        [Theory]
        [InlineData("MouseMoveThreshold=5000", "1000|0.005|3")]
        [InlineData("AudioThreshold=1.5", "10|0.005|3")]
        [InlineData("AudioThreshold=-0.1", "10|0.005|3")]
        [InlineData("AudioThreshold=NaN", "10|0.005|3")]
        [InlineData("AudioThreshold=1", "10|1|3")]
        [InlineData("AudioThreshold=0", "10|0|3")]
        [InlineData("AudioSeconds=9999", "10|0.005|3600")]
        public void 一般設定超出設定視窗的範圍時讀取後落在範圍內(string line, string expected)
        {
            var c = new Harness(Harness.Cfg("[General]", line)).App.Config;

            Assert.Equal(expected, string.Join("|", c.MouseMoveThreshold, c.AudioThreshold, c.AudioSeconds));
        }

        [Fact]
        public void 無效的切換條件略過並在啟動通知中說明原因()
        {
            var h = new Harness(Harness.Cfg(
                "[Switch.1]", "From=1", "Condition=Idle", "IdleTime=0", "To=2",
                "[Switch.2]", "From=1", "To=1",
                "[Switch.3]", "From=1", "To=99",
                "[Switch.4]", "From=abc", "To=2",
                "[Switch.5]", "From=1", "MouseKeyboard=0", "Audio=0", "To=2",
                "[Switch.6]", "From=1", "Condition=Sometimes", "IdleTime=5", "To=2",       // 無法辨識的條件類型不當成閒置
                "[Switch.7]", "From=2", "Condition=UsageDetected", "IdleTime=abc", "To=1",   // 偵測到使用不看閒置時間
                "[Switch.8]", "From=1", "Condition=Idle", "To=2",                            // 沒寫的用預設值
                "[Mode.1]", "Name=甲", "[Mode.2]", "Name=乙"));

            Assert.Equal(new[]
            {
                new SwitchCondition { FromModeId = 2, Kind = SwitchConditionKind.UsageDetected, Sources = UsageSources.MouseKeyboard, ToModeId = 1 },
                new SwitchCondition { FromModeId = 1, Kind = SwitchConditionKind.Idle, IdleAmount = 10, Sources = UsageSources.MouseKeyboard, ToModeId = 2 },
            }, h.App.Config.SwitchConditions);
            string note = Assert.Single(h.Notifier.Shown).Text;
            Assert.Contains("已略過無效的切換條件 [Switch.1]：閒置時間必須是正整數。\n", note);
            Assert.Contains("已略過無效的切換條件 [Switch.2]：來源模式與目標模式相同。\n", note);
            Assert.Contains("已略過無效的切換條件 [Switch.3]：目標模式不存在。\n", note);
            Assert.Contains("已略過無效的切換條件 [Switch.4]：來源模式不存在。\n", note);
            Assert.Contains("已略過無效的切換條件 [Switch.5]：未選任何使用來源。\n", note);
            Assert.Contains("已略過無效的切換條件 [Switch.6]：無法辨識的條件類型「Sometimes」（應為 Idle 或 UsageDetected）。\n", note);
            Assert.Equal(0, h.Store.Writes);   // 不立即改寫：使用者可以修正後重新載入
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
            var store = new FakeStore(Harness.Cfg("[General]", "MouseMoveThreshold=30")) { FailReads = true };
            var clock = new FakeClock();
            var app = new AppCoordinator(store, new FakeMonitors(Harness.DefaultMonitor), new FakeSignals(clock),
                clock, new FakeStartup(), new FakeNotifier());

            app.SetAutoSwitch(false);

            Assert.Equal(10, app.Config.MouseMoveThreshold);
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
        public void 舊格式cfg的閒置分鐘_聲音偵測_上次模式與bat相關的值捨棄()
        {
            var h = new Harness(Legacy);

            Assert.Empty(h.App.Config.SwitchConditions);
            foreach (string key in new[] { "BatPath", "HomeArgs", "AwayArgs", "LastMode", "IdleMinutes", "AudioDetect", "LegacySwitch", "\r\n[Switch." })
                Assert.DoesNotContain(key, h.Store.Text);
        }

        // ---- 過渡用的舊版切換條件（票 03 的 [LegacySwitch]）----

        [Fact]
        public void 舊版切換條件啟動時就轉換為等效的切換條件並改寫cfg()
        {
            var h = new Harness(Harness.Cfg(
                "[General]", "CurrentMode=1",
                "[LegacySwitch]", "HomeMode=1", "AwayMode=2", "IdleMinutes=5", "AudioDetect=1",
                "[Mode.1]", "Name=預設", "[Mode.2]", "Name=省電"));

            var both = UsageSources.MouseKeyboard | UsageSources.Audio;
            Assert.Equal(new[]
            {
                new SwitchCondition { FromModeId = 1, Kind = SwitchConditionKind.Idle, IdleAmount = 5, Sources = both, ToModeId = 2 },
                new SwitchCondition { FromModeId = 2, Kind = SwitchConditionKind.UsageDetected, Sources = both, ToModeId = 1 },
            }, h.App.Config.SwitchConditions);
            Assert.Equal(1, h.Store.Writes);   // 啟動時就改寫，轉換結果不會只存在記憶體中
            Assert.Contains("[Switch.1]\r\nFrom=1\r\nCondition=Idle\r\nIdleTime=5\r\nIdleUnit=Minutes\r\nMouseKeyboard=1\r\nAudio=1\r\nTo=2\r\n", h.Store.Text);
            Assert.Contains("[Switch.2]\r\nFrom=2\r\nCondition=UsageDetected\r\nMouseKeyboard=1\r\nAudio=1\r\nTo=1\r\n", h.Store.Text);
            foreach (string key in new[] { "LegacySwitch", "HomeMode", "AwayMode", "IdleMinutes", "AudioDetect" })
                Assert.DoesNotContain(key, h.Store.Text);
            Assert.Contains("已將舊版切換條件轉換為 2 條切換條件", Assert.Single(h.Notifier.Shown).Text);

            var reread = new Harness(h.Store.Text);   // 改寫後的 cfg 不需要再改寫
            Assert.Equal(h.App.Config.SwitchConditions, reread.App.Config.SwitchConditions);
            Assert.Equal(0, reread.Store.Writes);
        }

        [Fact]
        public void 舊版切換條件關閉聲音偵測時兩條切換條件都不選聲音()
        {
            var h = new Harness(Harness.Cfg(
                "[LegacySwitch]", "HomeMode=2", "AwayMode=1", "IdleMinutes=30", "AudioDetect=0",
                "[Mode.1]", "Name=甲", "[Mode.2]", "Name=乙"));

            Assert.Equal(new[] { (2, UsageSources.MouseKeyboard, 1), (1, UsageSources.MouseKeyboard, 2) },
                h.App.Config.SwitchConditions.Select(s => (s.FromModeId, s.Sources, s.ToModeId)));
            Assert.Equal(30, h.App.Config.SwitchConditions[0].IdleAmount);
        }

        [Fact]
        public void 舊版切換條件沒有指向兩個不同的模式時不轉換但仍移除()
        {
            foreach (string[] roles in new[] { new[] { "HomeMode=1", "AwayMode=1" }, new[] { "HomeMode=1", "AwayMode=0" }, new[] { "HomeMode=1" } })
            {
                var h = new Harness(Harness.Cfg("[LegacySwitch]") + Harness.Cfg(roles) + Harness.Cfg("[Mode.1]", "Name=甲", "[Mode.2]", "Name=乙"));

                Assert.Empty(h.App.Config.SwitchConditions);
                Assert.Equal(1, h.Store.Writes);
                Assert.DoesNotContain("LegacySwitch", h.Store.Text);
                Assert.Contains("已移除舊版切換條件", Assert.Single(h.Notifier.Shown).Text);
            }
        }

        [Fact]
        public void 票02格式的cfg啟動時就移除bat相關的鍵與LastMode()
        {
            var h = new Harness(Harness.Cfg(
                "[General]", "CurrentMode=4",
                "[LegacySwitch]", "BatPath=C:\\m.bat", "HomeArgs=home", "AwayArgs=away", "IdleMinutes=7", "LastMode=home",
                "[Mode.4]", "Name=預設"));

            Assert.Empty(h.App.Config.SwitchConditions);   // 只有一個模式：舊版的兩條切換條件本來就不作用
            Assert.Equal(1, h.Store.Writes);
            foreach (string key in new[] { "BatPath", "HomeArgs", "AwayArgs", "LastMode", "IdleMinutes", "LegacySwitch" })
                Assert.DoesNotContain(key, h.Store.Text);
            Assert.Contains("已移除不再使用的設定（bat 設定、LastMode）。", Assert.Single(h.Notifier.Shown).Text);
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

            h.App.SetAutoSwitch(true);

            var c = new Harness(h.Store.Text).App.Config;
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

        // ---- 重新載入 ----

        [Fact]
        public void 重新載入cfg套用新設定但維持目前模式()
        {
            var h = Harness.Modes(Harness.Play, Sw.Idle(Harness.Play, Harness.Save, 10));
            h.Store.Text = Harness.Cfg("[General]", "CurrentMode=2", "[Switch.1]") + Sw.Idle(1, 2, 5) + Harness.Cfg(
                "[Mode.1]", "Name=甲", "[Mode.2]", "Name=乙",
                "[Mode.2.Monitor.1]", "Monitor=" + Harness.DefaultMonitor.Id, "Enabled=1", "Primary=1");

            h.App.ReloadConfig();

            Assert.Equal(1, h.App.Config.CurrentModeId);   // 程式記錄的目前模式仍存在：維持
            Assert.Equal(5, Assert.Single(h.App.Config.SwitchConditions).IdleAmount);
            Assert.Equal(new[] { "甲", "乙" }, h.App.Config.Modes.Select(m => m.Name));
            Assert.Empty(h.Monitors.Applied);
            Assert.Contains(h.Notifier.Shown, n => n.Kind == NotificationKind.Info && n.Text.StartsWith("已重新載入設定檔。"));
            Assert.Equal(5 * 60, h.RunUntilSwitch(20 * 60));
            Assert.Equal("乙", h.Monitors.AppliedTargets.Single());
        }

        [Fact]
        public void 重新載入後程式記錄的目前模式不存在時改用cfg的目前模式()
        {
            var h = Harness.Modes(Harness.Save);
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
            var h = new Harness(Harness.Cfg("[General]", "AutoSwitch=0", "CurrentMode=2", "[Switch.1]") + Sw.Idle(1, 2, 30) +
                Harness.Cfg("[Switch.2]") + Sw.Usage(2, 1, Sw.Both) +
                Harness.Cfg("[Switch.3]") + Sw.Idle(2, 3, 1, Sw.MouseOrKeyboard, IdleUnit.Hours) +
                Harness.Cfg("[Mode.1]", "Name=甲", "[Mode.2]", "Name=乙", "[Mode.3]", "Name=丙"));

            Assert.Equal(("省電螢幕工具已啟動。\n目前：乙｜自動切換：關\n" +   // 只列出從目前模式出發的切換條件
                          "「乙」偵測到使用（滑鼠或鍵盤、聲音）→「甲」\n「乙」閒置 1 小時（滑鼠或鍵盤）→「丙」",
                    NotificationKind.Info),
                Assert.Single(h.Notifier.Shown));
        }

        [Fact]
        public void 啟動通知說明沒有從目前模式出發的切換條件()
        {
            var h = new Harness(Harness.Cfg("[General]", "AutoSwitch=1", "[Mode.1]", "Name=預設"));

            Assert.Equal("省電螢幕工具已啟動。\n目前：預設｜自動切換：開\n沒有從「預設」出發的切換條件",
                Assert.Single(h.Notifier.Shown).Text);
        }

        // ---- 開機啟動 ----

        [Theory]
        [InlineData(true, false)]
        [InlineData(true, true)]    // 已經開啟也重寫一次：exe 搬家後更新為目前的路徑
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void 啟動時依cfg同步開機啟動(bool inCfg, bool inRegistry)
        {
            var h = new Harness(Harness.Cfg("[General]", "StartWithWindows=" + (inCfg ? 1 : 0)), startupEnabled: inRegistry);

            Assert.Equal(new[] { inCfg }, h.Startup.Calls);
            Assert.Equal(inCfg, h.Startup.IsEnabled);
        }

        [Fact]
        public void 無法同步開機啟動時在啟動通知中說明()
        {
            var h = new Harness(Harness.Cfg("[General]", "StartWithWindows=1"), start: false);
            h.Startup.FailWith = "拒絕存取";

            h.App.Start();

            Assert.Contains("無法同步開機啟動設定：拒絕存取", Assert.Single(h.Notifier.Shown).Text);
        }

        [Fact]
        public void cfg讀取失敗時不同步開機啟動()
        {
            var store = new FakeStore(Harness.Cfg("[General]", "StartWithWindows=1")) { FailReads = true };
            var clock = new FakeClock();
            var startup = new FakeStartup { IsEnabled = true };
            var app = new AppCoordinator(store, new FakeMonitors(Harness.DefaultMonitor), new FakeSignals(clock),
                clock, startup, new FakeNotifier());

            app.Start();

            Assert.Empty(startup.Calls);
            Assert.True(startup.IsEnabled);
        }

        [Fact]
        public void 重新載入cfg時依cfg同步開機啟動()
        {
            var h = new Harness(Harness.Cfg("[General]", "StartWithWindows=0"));
            h.Store.Text = h.Store.Text!.Replace("StartWithWindows=0", "StartWithWindows=1");

            h.App.ReloadConfig();

            Assert.Equal(new[] { false, true }, h.Startup.Calls);
            Assert.True(h.Startup.IsEnabled);
        }

        [Fact]
        public void 在設定視窗變更開機啟動後寫入cfg並同步登錄檔()
        {
            var h = new Harness(Handwritten);   // StartWithWindows=1
            AppConfig draft = h.App.Config.Clone();
            draft.StartWithWindows = false;

            Assert.Null(h.App.ApplySettings(draft));

            Assert.False(h.Startup.IsEnabled);
            Assert.Contains("StartWithWindows=0\r\n", h.Store.Text);
        }

        [Fact]
        public void 在設定視窗變更開機啟動但無法修改登錄檔時跳出錯誤通知_cfg仍寫入()
        {
            var h = new Harness(Harness.Cfg());
            h.Startup.FailWith = "拒絕存取";
            AppConfig draft = h.App.Config.Clone();
            draft.StartWithWindows = true;

            Assert.Null(h.App.ApplySettings(draft));

            Assert.False(h.Startup.IsEnabled);
            Assert.Contains("StartWithWindows=1\r\n", h.Store.Text);
            Assert.Equal(NotificationKind.Error, h.Notifier.Shown.Last().Kind);
            Assert.Contains("無法同步開機啟動設定：拒絕存取", h.Notifier.Shown.Last().Text);
        }

        /// <summary>把設定轉成可比較的文字（一般設定、切換條件、每個模式與螢幕設定）。</summary>
        static string Describe(AppConfig c) =>
            string.Join("|", c.AutoSwitch, c.CurrentModeId, c.MouseMoveThreshold, c.AudioThreshold, c.AudioSeconds,
                c.IgnoreRemoteInput, c.ShowNotifications, c.StartWithWindows) +
            string.Concat(c.SwitchConditions.Select(s => "\n" + s)) +
            string.Concat(c.Modes.Select(m => "\n" + m.Id + ":" + m.Name + ":" + m.Color + ":" + m.ManualAutoSwitch + ":" + m.UnlistedMonitors +
                string.Concat(m.Monitors.Select(s => "\n  " + s))));
    }
}
