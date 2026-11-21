using System.Linq;
using AutoDisplayMode.Core;
using Xunit;

namespace AutoDisplayMode.Core.Tests
{
    /// <summary>首次啟動建立「預設」模式、擷取規則、螢幕識別。</summary>
    public class CaptureTests
    {
        [Fact]
        public void 首次啟動以目前螢幕狀態建立預設模式並寫入新格式cfg()
        {
            var h = new Harness(cfg: null, monitors: new[]
            {
                Mon.On("SN-A", 0, 0, 2560, 1440, 144, primary: true),
                Mon.On("SN-B", -1080, -300, 1920, 1080, 60, Orientation.Portrait, connector: "DP2", name: "DELL P2419H"),
            });

            var mode = Assert.Single(h.App.Config.Modes);
            Assert.Equal("預設", mode.Name);
            Assert.Equal(mode.Id, h.App.Config.CurrentModeId);
            Assert.Equal(new[]
            {
                new MonitorSettings
                {
                    Id = MonitorId.Create("DEL", "A0F4", "SN-A", "DP1"), Name = "DELL U2720Q", Enabled = true,
                    X = 0, Y = 0, Width = 2560, Height = 1440, RefreshRate = 144, Orientation = Orientation.Landscape, Primary = true,
                },
                new MonitorSettings
                {
                    Id = MonitorId.Create("DEL", "A0F4", "SN-B", "DP2"), Name = "DELL P2419H", Enabled = true,
                    X = -1080, Y = -300, Width = 1920, Height = 1080, RefreshRate = 60, Orientation = Orientation.Portrait, Primary = false,
                },
            }, mode.Monitors);

            Assert.StartsWith("# Auto Display Mode 設定檔\r\n", h.Store.Text);
            Assert.Contains("[General]\r\n", h.Store.Text);
            Assert.Contains("Name=預設\r\n", h.Store.Text);
            Assert.Contains("Monitor=DEL/A0F4/SN:SN-B\r\n", h.Store.Text);
        }

        [Fact]
        public void 擷取時接上且開啟的螢幕記錄完整狀態_接上但關閉的保留原有解析度_沒接上的保持不變()
        {
            var h = new Harness(Cfg(
                Monitor("DEL/A0F4/SN:ON", enabled: false, x: 100, y: 100, w: 1280, h: 720, hz: 50),
                Monitor("GSM/5B7F/SN:OFF", enabled: true, x: 1920, y: 0, w: 2560, h: 1440, hz: 165, orientation: "PortraitFlipped"),
                Monitor("AUS/27A1/SN:GONE", enabled: true, x: -1920, y: 0, w: 1920, h: 1080, hz: 240, primary: true)));
            h.Monitors.Monitors = new()
            {
                Mon.Off("NEW", new VideoMode(3840, 2160, 60), manufacturer: "SAM", product: "7123", name: "Odyssey"),
                Mon.Off("OFF", new VideoMode(1920, 1080, 60)),
                Mon.On("ON", 0, 0, 1920, 1080, 144, Orientation.Landscape, primary: true),
            };

            h.App.CaptureToCurrentMode();

            var expected = new[]
            {
                new MonitorSettings
                {
                    Id = MonitorId.Create("DEL", "A0F4", "ON", "DP1"), Name = "DELL U2720Q", Enabled = true,
                    X = 0, Y = 0, Width = 1920, Height = 1080, RefreshRate = 144, Orientation = Orientation.Landscape, Primary = true,
                },
                new MonitorSettings   // 關閉：保留原有解析度、更新率、座標與方向
                {
                    Id = MonitorId.Create("GSM", "5B7F", "OFF", "HDMI1"), Name = "LG ULTRAGEAR", Enabled = false,
                    X = 1920, Y = 0, Width = 2560, Height = 1440, RefreshRate = 165, Orientation = Orientation.PortraitFlipped, Primary = false,
                },
                new MonitorSettings   // 沒接上：保持不變
                {
                    Id = MonitorId.Create("AUS", "27A1", "GONE", ""), Name = "舊螢幕", Enabled = true,
                    X = -1920, Y = 0, Width = 1920, Height = 1080, RefreshRate = 240, Orientation = Orientation.Landscape, Primary = true,
                },
                new MonitorSettings   // 新的關閉螢幕：用原生解析度
                {
                    Id = MonitorId.Create("SAM", "7123", "NEW", "HDMI1"), Name = "Odyssey", Enabled = false,
                    X = 0, Y = 0, Width = 3840, Height = 2160, RefreshRate = 60, Orientation = Orientation.Landscape, Primary = false,
                },
            };
            Assert.Equal(expected, h.App.Config.CurrentModeConfig!.Monitors);
            Assert.Equal(expected, new Harness(h.Store.Text).App.Config.CurrentModeConfig!.Monitors);   // 已存檔
            Assert.Contains(("已將目前螢幕狀態存入「遊玩」。", NotificationKind.Info), h.Notifier.Shown);
        }

        [Fact]
        public void 新接上的關閉螢幕讀不到原生解析度時用支援的最大解析度()
        {
            var h = new Harness(Cfg());
            h.Monitors.Monitors = new()
            {
                Harness.DefaultMonitor,
                Mon.Off("X", native: null) with
                {
                    SupportedModes = new[] { new VideoMode(1280, 720, 60), new VideoMode(1920, 1080, 75), new VideoMode(1920, 1080, 60) },
                },
            };

            h.App.CaptureToCurrentMode();

            var off = h.App.Config.CurrentModeConfig!.Monitors.Single(m => m.Id.Serial == "X");
            Assert.False(off.Enabled);
            Assert.Equal((1920, 1080, 75), (off.Width, off.Height, off.RefreshRate));
        }

        [Fact]
        public void 擷取存入目前模式_其他模式不受影響()
        {
            var h = new Harness(Cfg(currentMode: 2));

            h.App.CaptureToCurrentMode();

            Assert.Equal("省電", h.App.CurrentModeName);
            Assert.Single(h.App.Config.Modes.Single(m => m.Id == 2).Monitors);
            Assert.Equal(3, h.App.Config.Modes.Single(m => m.Id == 1).Monitors.Count);
            Assert.Contains(("已將目前螢幕狀態存入「省電」。", NotificationKind.Info), h.Notifier.Shown);
        }

        [Fact]
        public void 偵測螢幕失敗時不改變模式並通知()
        {
            var h = new Harness(Cfg());
            string before = h.Store.Text!;
            h.Monitors.Fail = true;

            h.App.CaptureToCurrentMode();

            Assert.Equal(before, h.Store.Text);
            Assert.Contains(("無法偵測螢幕：偵測失敗", NotificationKind.Error), h.Notifier.Shown);
        }

        [Fact]
        public void 切換中不能擷取()
        {
            var h = new Harness(Cfg());
            h.App.ManualSwitch(2);
            Assert.True(h.App.IsSwitching);
            string before = h.Store.Text!;

            h.App.CaptureToCurrentMode();

            Assert.Equal(before, h.Store.Text);
            Assert.Contains(("正在切換中，請稍候。", NotificationKind.Info), h.Notifier.Shown);
        }

        // ---- 螢幕識別（ADR-0003）----

        [Fact]
        public void 有序號時以廠牌型號序號識別_沒有序號時以廠牌型號接孔識別()
        {
            var h = new Harness(cfg: null, monitors: new[]
            {
                Mon.On("ABC123", 0, 0, 1920, 1080, 60, primary: true, connector: "DP1"),
                Mon.On(null!, 1920, 0, 1920, 1080, 60, connector: "HDMI2"),
                Mon.On("   ", 3840, 0, 1920, 1080, 60, connector: "DP3"),
            });

            Assert.Equal(new[] { "DEL/A0F4/SN:ABC123", "DEL/A0F4/PORT:HDMI2", "DEL/A0F4/PORT:DP3" },
                h.App.Config.CurrentModeConfig!.Monitors.Select(m => m.Id.ToString()));
            Assert.Contains("Monitor=DEL/A0F4/PORT:HDMI2\r\n", h.Store.Text);
        }

        [Fact]
        public void 同型號兩台螢幕以序號區分()
        {
            var h = new Harness(cfg: null, monitors: new[]
            {
                Mon.On("AAA", 0, 0, 1920, 1080, 60, primary: true),
                Mon.On("BBB", 1920, 0, 1920, 1080, 60),
            });

            Assert.Equal(2, h.App.Config.CurrentModeConfig!.Monitors.Count);
        }

        [Fact]
        public void 有序號的螢幕換接孔後仍是同一台()
        {
            var h = new Harness(cfg: null, monitors: new[] { Mon.On("AAA", 0, 0, 1920, 1080, 60, primary: true, connector: "DP1") });
            h.Monitors.Monitors = new() { Mon.On("AAA", 0, 0, 2560, 1440, 144, primary: true, connector: "HDMI1") };

            h.App.CaptureToCurrentMode();

            var m = Assert.Single(h.App.Config.CurrentModeConfig!.Monitors);
            Assert.Equal(2560, m.Width);
        }

        [Fact]
        public void 沒有序號的螢幕換接孔後視為另一台()
        {
            var h = new Harness(cfg: null, monitors: new[] { Mon.On(null!, 0, 0, 1920, 1080, 60, primary: true, connector: "DP1") });
            h.Monitors.Monitors = new() { Mon.On(null!, 0, 0, 1920, 1080, 60, primary: true, connector: "HDMI1") };

            h.App.CaptureToCurrentMode();

            Assert.Equal(new[] { "DEL/A0F4/PORT:DP1", "DEL/A0F4/PORT:HDMI1" },
                h.App.Config.CurrentModeConfig!.Monitors.Select(m => m.Id.ToString()));
        }

        [Fact]
        public void 手動編輯cfg時廠牌型號大小寫不影響比對()
        {
            var h = new Harness(Cfg(Monitor("del/a0f4/SN:SN0001", enabled: false, x: 0, y: 0, w: 1280, h: 720, hz: 60)));

            h.App.CaptureToCurrentMode();

            var m = Assert.Single(h.App.Config.CurrentModeConfig!.Monitors);
            Assert.True(m.Enabled);
            Assert.Equal("DEL/A0F4/SN:SN0001", m.Id.ToString());
        }

        // ---- 測試用 cfg ----

        /// <summary>兩個模式：1「遊玩」含給定的螢幕設定（預設三台）、2「省電」沒有螢幕設定。</summary>
        static string Cfg(params string[] monitorSections) => Cfg(1, monitorSections);

        static string Cfg(int currentMode, params string[] monitorSections)
        {
            if (monitorSections.Length == 0)
            {
                monitorSections = new[]
                {
                    Monitor("DEL/A0F4/SN:SN0001", enabled: true, x: 0, y: 0, w: 1920, h: 1080, hz: 60, primary: true),
                    Monitor("GSM/5B7F/SN:B", enabled: false, x: 1920, y: 0, w: 1920, h: 1080, hz: 60),
                    Monitor("AUS/27A1/PORT:DP9", enabled: true, x: -1920, y: 0, w: 1920, h: 1080, hz: 60),
                };
            }
            var lines = new System.Collections.Generic.List<string>
            {
                "[General]", "CurrentMode=" + currentMode,
                "[Mode.1]", "Name=遊玩",
            };
            for (int i = 0; i < monitorSections.Length; i++)
                lines.Add("[Mode.1.Monitor." + (i + 1) + "]\r\n" + monitorSections[i]);
            lines.Add("[Mode.2]");
            lines.Add("Name=省電");
            return Harness.Cfg(lines.ToArray());
        }

        static string Monitor(string id, bool enabled, int x, int y, int w, int h, int hz,
            string orientation = "Landscape", bool primary = false) =>
            string.Join("\r\n",
                "Monitor=" + id, "Name=舊螢幕", "Enabled=" + (enabled ? 1 : 0), "X=" + x, "Y=" + y,
                "Width=" + w, "Height=" + h, "RefreshRate=" + hz, "Orientation=" + orientation, "Primary=" + (primary ? 1 : 0));
    }
}
