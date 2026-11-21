using System.Linq;
using AutoDisplayMode.Core;
using Xunit;
using static AutoDisplayMode.Core.Tests.Harness;

namespace AutoDisplayMode.Core.Tests
{
    /// <summary>設定視窗的螢幕配置分頁：配置驗證、編輯螢幕設定、預覽套用與還原、螢幕插拔通知。</summary>
    public class LayoutTabTests
    {
        static AppConfig Reread(Harness h) => new Harness(h.Store.Text).App.Config;

        /// <summary>預設螢幕（<see cref="Harness.DefaultMonitor"/>）在 cfg 中沒有名稱，訊息以螢幕識別稱呼它。</summary>
        const string Main = "DEL/A0F4/SN:SN0001";

        /// <summary>模式中一台螢幕（與預設螢幕同型號、不同序號）的螢幕設定。</summary>
        static MonitorSettings Rec(string serial, int x, int y, int w = 1920, int h = 1080, bool on = true, bool primary = false,
            Orientation orientation = Orientation.Landscape, string name = "") => new()
            {
                Id = MonitorId.Create("DEL", "A0F4", serial, "DP1"), Name = name, Enabled = on, X = x, Y = y,
                Width = w, Height = h, RefreshRate = 60, Orientation = orientation, Primary = primary,
            };

        /// <summary>副本中模式 <paramref name="modeId"/> 的螢幕設定換成 <paramref name="monitors"/>。</summary>
        static AppConfig DraftWith(Harness h, int modeId, params MonitorSettings[] monitors)
        {
            AppConfig draft = h.App.Config.Clone();
            ModeConfig mode = draft.FindMode(modeId)!;
            mode.Monitors.Clear();
            mode.Monitors.AddRange(monitors);
            return draft;
        }

        // ---- 配置驗證 ----

        [Fact]
        public void 螢幕重疊時無法套用並說明原因_設定與cfg都不變()
        {
            var h = Modes(Play);
            string before = h.Store.Text!;
            AppConfig draft = DraftWith(h, Play, Rec("SN0001", 0, 0, primary: true), Rec("SN0002", 1000, 500, name: "副螢幕"));

            Assert.Equal("「遊玩」的螢幕配置有誤：「" + Main + "」與「副螢幕」重疊。", h.App.ApplySettings(draft));
            Assert.Equal(before, h.Store.Text);
            Assert.Single(h.App.Config.FindMode(Play)!.Monitors);
        }

        [Fact]
        public void 直向的螢幕寬高對調後才判斷重疊()
        {
            var h = Modes(Play);
            // 直向的 1920x1080 在桌面上寬 1080：右邊緣在 1920 + 1080 = 3000
            MonitorSettings portrait = Rec("SN0002", 1920, 0, orientation: Orientation.Portrait, name: "直向");

            Assert.Null(h.App.ApplySettings(DraftWith(h, Play,
                Rec("SN0001", 0, 0, primary: true), portrait, Rec("SN0003", 3000, 0, name: "右邊"))));
            Assert.Equal("「遊玩」的螢幕配置有誤：「直向」與「右邊」重疊。", h.App.ApplySettings(DraftWith(h, Play,
                Rec("SN0001", 0, 0, primary: true), portrait, Rec("SN0003", 2999, 0, name: "右邊"))));
        }

        public static TheoryData<string, string?> 主螢幕與開啟的規則 => new()
        {
            { "沒有主螢幕", "「遊玩」的螢幕配置有誤：沒有主螢幕。" },
            { "兩台主螢幕", "「遊玩」的螢幕配置有誤：主螢幕只能有一台。" },
            { "主螢幕不在原點", "「遊玩」的螢幕配置有誤：主螢幕「主」必須位於 (0,0)。" },
            { "關閉的螢幕標記為主螢幕", "「遊玩」的螢幕配置有誤：沒有主螢幕。" },
            { "全部關閉且未記錄的螢幕關閉", "「遊玩」的螢幕配置有誤：至少要開啟一台螢幕。" },
            { "全部關閉但未記錄的螢幕維持現狀", null },
            { "沒有記錄任何螢幕", null },
        };

        [Theory]
        [MemberData(nameof(主螢幕與開啟的規則))]
        public void 主螢幕必須唯一且位於原點_至少要開啟一台螢幕(string edit, string? reason)
        {
            var h = Modes(Play);
            MonitorSettings[] monitors = edit switch
            {
                "沒有主螢幕" => new[] { Rec("SN0001", 0, 0, name: "主"), Rec("SN0002", 1920, 0) },
                "兩台主螢幕" => new[] { Rec("SN0001", 0, 0, primary: true, name: "主"), Rec("SN0002", 1920, 0, primary: true) },
                "主螢幕不在原點" => new[] { Rec("SN0001", 100, 0, primary: true, name: "主"), Rec("SN0002", 2020, 0) },
                "關閉的螢幕標記為主螢幕" => new[] { Rec("SN0001", 0, 0, on: false, primary: true, name: "主"), Rec("SN0002", 1920, 0) },
                "沒有記錄任何螢幕" => new MonitorSettings[0],
                _ => new[] { Rec("SN0001", 0, 0, on: false, name: "主"), Rec("SN0002", 1920, 0, on: false) },
            };
            AppConfig draft = DraftWith(h, Play, monitors);
            draft.FindMode(Play)!.UnlistedMonitors = edit == "全部關閉但未記錄的螢幕維持現狀" ? UnlistedMonitors.Keep : UnlistedMonitors.Off;

            Assert.Equal(reason, h.App.ApplySettings(draft));
        }

        public static TheoryData<int, int, string?> 空隙 => new()
        {
            { 1920, 0, null },      // 右邊緊鄰
            { 1920, 1079, null },   // 右邊緊鄰，只有 1 像素相接
            { 0, 1080, null },      // 下方緊鄰
            { 1921, 0, "螢幕之間有空隙：「副螢幕」沒有與主螢幕相連，游標無法移過去。" },
            { 1920, 1080, "螢幕之間有空隙：「副螢幕」沒有與主螢幕相連，游標無法移過去。" },   // 只有角落相接
        };

        [Theory]
        [MemberData(nameof(空隙))]
        public void 螢幕之間有空隙時顯示警告但仍可存檔(int x, int y, string? warning)
        {
            var h = Modes(Play);
            AppConfig draft = DraftWith(h, Play, Rec("SN0001", 0, 0, primary: true), Rec("SN0002", x, y, name: "副螢幕"));

            Assert.Equal(warning, MonitorLayout.Check(draft.FindMode(Play)!).Warnings.SingleOrDefault());
            Assert.Null(h.App.ApplySettings(draft));
            Assert.Equal((x, y), Reread(h).FindMode(Play)!.Monitors.Select(m => (m.X, m.Y)).Last());
        }

        [Fact]
        public void 透過其他螢幕相連就不算空隙_關閉的螢幕不參與檢查()
        {
            var h = Modes(Play);
            ModeConfig mode = DraftWith(h, Play,
                Rec("SN0001", 0, 0, primary: true),
                Rec("SN0002", 1920, 0, name: "中"),
                Rec("SN0003", 3840, 0, name: "右"),
                Rec("SN0004", 9999, 9999, on: false, name: "關閉的")).FindMode(Play)!;

            Assert.Empty(MonitorLayout.Check(mode).Warnings);

            mode.Monitors[1] = mode.Monitors[1] with { Enabled = false };
            Assert.Equal("螢幕之間有空隙：「右」沒有與主螢幕相連，游標無法移過去。", Assert.Single(MonitorLayout.Check(mode).Warnings));
        }

        // ---- 編輯螢幕設定（畫布與欄位） ----

        /// <summary>副本中模式 <paramref name="modeId"/> 套用後，重新讀取 cfg 的每台螢幕：(序號, 開啟, X, Y, 主螢幕)。</summary>
        static (string, bool, int, int, bool)[] SavedLayout(Harness h, AppConfig draft, int modeId)
        {
            Assert.Null(h.App.ApplySettings(draft));
            return Reread(h).FindMode(modeId)!.Monitors.Select(m => (m.Id.Serial ?? "", m.Enabled, m.X, m.Y, m.Primary)).ToArray();
        }

        [Fact]
        public void 移動螢幕或輸入座標後套用即寫入cfg()
        {
            var h = Modes(Play);
            AppConfig draft = DraftWith(h, Play, Rec("SN0001", 0, 0, primary: true), Rec("SN0002", 1920, 0));
            ModeConfig mode = draft.FindMode(Play)!;

            mode.UpdateMonitor(mode.Monitors[1] with { X = -1920, Y = 200 });

            Assert.Equal(new[] { ("SN0001", true, 0, 0, true), ("SN0002", true, -1920, 200, false) }, SavedLayout(h, draft, Play));
        }

        [Fact]
        public void 設為主螢幕後座標改以它的左上角為原點()
        {
            var h = Modes(Play);
            AppConfig draft = DraftWith(h, Play, Rec("SN0001", 0, 0, primary: true), Rec("SN0002", 1920, 100), Rec("SN0003", 0, 0, on: false));
            ModeConfig mode = draft.FindMode(Play)!;

            mode.UpdateMonitor(mode.Monitors[1] with { Primary = true });

            // 關閉的螢幕座標不參與，維持原值
            Assert.Equal(new[] { ("SN0001", true, -1920, -100, false), ("SN0002", true, 0, 0, true), ("SN0003", false, 0, 0, false) },
                SavedLayout(h, draft, Play));
        }

        [Fact]
        public void 移動主螢幕時改為其他螢幕反向平移_主螢幕維持在原點()
        {
            var h = Modes(Play);
            AppConfig draft = DraftWith(h, Play, Rec("SN0001", 0, 0, primary: true), Rec("SN0002", 1920, 0));
            ModeConfig mode = draft.FindMode(Play)!;

            mode.UpdateMonitor(mode.Monitors[0] with { X = -100, Y = 50 });

            Assert.Equal(new[] { ("SN0001", true, 0, 0, true), ("SN0002", true, 2020, -50, false) }, SavedLayout(h, draft, Play));
        }

        [Fact]
        public void 關閉主螢幕時第一台開啟的螢幕成為主螢幕()
        {
            var h = Modes(Play);
            AppConfig draft = DraftWith(h, Play, Rec("SN0001", 0, 0, primary: true), Rec("SN0002", 0, 0, on: false), Rec("SN0003", 1920, 0));
            ModeConfig mode = draft.FindMode(Play)!;

            mode.SetEnabled(mode.Monitors[0], false);

            Assert.Equal(new[] { ("SN0001", false, 0, 0, false), ("SN0002", false, 0, 0, false), ("SN0003", true, 0, 0, true) },
                SavedLayout(h, draft, Play));
        }

        [Fact]
        public void 開啟關閉中的螢幕時_與其他螢幕重疊就放到最右邊()
        {
            var h = Modes(Play);
            AppConfig draft = DraftWith(h, Play, Rec("SN0001", 0, 0, primary: true), Rec("SN0002", -1280, 0, w: 1280, h: 720), Rec("SN0003", 0, 0, on: false));
            ModeConfig mode = draft.FindMode(Play)!;

            mode.SetEnabled(mode.Monitors[2], true);

            Assert.Equal(("SN0003", true, 1920, 0, false), SavedLayout(h, draft, Play)[2]);
        }

        [Fact]
        public void 開啟模式中唯一的螢幕時它成為主螢幕()
        {
            var h = Modes(Play);
            AppConfig draft = DraftWith(h, Play, Rec("SN0001", 300, 300, on: false));
            ModeConfig mode = draft.FindMode(Play)!;

            mode.SetEnabled(mode.Monitors[0], true);

            Assert.Equal(new[] { ("SN0001", true, 0, 0, true) }, SavedLayout(h, draft, Play));
        }

        [Fact]
        public void 編輯此模式未記錄的接上螢幕後_它加入模式的螢幕設定()
        {
            var second = Mon.On("SN0002", 1920, 0, 2560, 1440, 144, name: "副螢幕");
            var h = new Harness(Cfg("[General]", "CurrentMode=1", "[Mode.1]", "Name=遊玩",
                "[Mode.1.Monitor.1]", "Monitor=" + DefaultMonitor.Id, "Enabled=1", "X=0", "Y=0", "Width=1920", "Height=1080", "RefreshRate=60", "Primary=1"),
                monitors: new[] { DefaultMonitor, second });
            AppConfig draft = h.App.Config.Clone();
            ModeConfig mode = draft.FindMode(1)!;

            // 畫布顯示的是套用此模式時它會有的狀態（未記錄的螢幕維持現狀）
            MonitorSettings shown = mode.SettingsFor(second);
            Assert.Equal((true, 1920, 0, 2560, 1440, 144), (shown.Enabled, shown.X, shown.Y, shown.Width, shown.Height, shown.RefreshRate));
            Assert.Single(mode.Monitors);

            mode.UpdateMonitor(shown with { Y = 100 });

            Assert.Equal(new[] { ("SN0001", true, 0, 0, true), ("SN0002", true, 1920, 100, false) }, SavedLayout(h, draft, 1));
            Assert.Equal("副螢幕", Reread(h).FindMode(1)!.Monitors[1].Name);
        }

        [Fact]
        public void 未記錄的接上螢幕不顯示為主螢幕_與記錄的螢幕重疊時只警告()
        {
            // 目前 Windows 中第二台是主螢幕；模式只記錄第一台。套用時維持現狀的螢幕不搶主螢幕
            var other = Mon.On("SN0002", 500, 0, 1920, 1080, 60, primary: true, name: "副螢幕");
            var h = new Harness(Cfg("[General]", "CurrentMode=1", "[Mode.1]", "Name=遊玩",
                "[Mode.1.Monitor.1]", "Monitor=" + DefaultMonitor.Id, "Name=主", "Enabled=1", "X=0", "Y=0", "Width=1920", "Height=1080", "RefreshRate=60", "Primary=1"),
                monitors: new[] { DefaultMonitor, other });
            AppConfig draft = h.App.Config.Clone();
            ModeConfig mode = draft.FindMode(1)!;

            Assert.False(mode.SettingsFor(other).Primary);
            Assert.Empty(MonitorLayout.Check(mode).Warnings);
            Assert.Equal("「副螢幕」沒有記錄在此模式中，套用時維持現狀，會與「主」重疊。",
                Assert.Single(MonitorLayout.Check(mode, h.Monitors.Monitors).Warnings));
            Assert.Null(h.App.ApplySettings(draft));   // 是否重疊要看套用當時的狀態，所以不擋存檔
        }

        // ---- 預覽套用 ----

        /// <summary>一台螢幕要套用的狀態：(序號, 開啟, X, Y, 寬, 高, 主螢幕)。</summary>
        static (string, bool, int, int, int, int, bool)[] States(System.Collections.Generic.IReadOnlyList<MonitorSettings> states) =>
            states.Select(s => (s.Id.Serial ?? "", s.Enabled, s.X, s.Y, s.Width, s.Height, s.Primary)).ToArray();

        /// <summary>接上兩台螢幕：主螢幕 1920x1080 與右邊的 1280x720；目前模式「遊玩」只記錄主螢幕。</summary>
        static Harness TwoMonitors(out AppConfig draft)
        {
            var h = new Harness(
                Cfg("[General]", "CurrentMode=1", "AutoSwitch=1",
                    "[Mode.1]", "Name=遊玩", "[Mode.1.Monitor.1]", "Monitor=" + DefaultMonitor.Id, "Enabled=1", "X=0", "Y=0",
                    "Width=1920", "Height=1080", "RefreshRate=60", "Primary=1",
                    "[Mode.2]", "Name=省電"),
                monitors: new[] { DefaultMonitor, Mon.On("SN0002", 1920, 0, 1280, 720, 60) });
            draft = h.App.Config.Clone();
            return h;
        }

        [Fact]
        public void 預覽套用編輯中的螢幕配置_不改變目前模式也不存檔()
        {
            var h = TwoMonitors(out AppConfig draft);
            int writes = h.Store.Writes;
            int applied = 0;
            h.App.PreviewApplied += () => applied++;
            ModeConfig save = draft.FindMode(2)!;
            save.UpdateMonitor(Rec("SN0002", 0, 0, w: 1280, h: 720, primary: true));
            save.UnlistedMonitors = UnlistedMonitors.Off;

            Assert.Null(h.App.PreviewLayout(save));

            Assert.Equal(new[] { ("SN0001", false, 0, 0, 1920, 1080, false), ("SN0002", true, 0, 0, 1280, 720, true) }, States(h.LastApplied));
            Assert.True(h.App.IsSwitching);
            Assert.Equal(0, applied);

            h.FinishSwitch();

            Assert.Equal(1, applied);
            Assert.True(h.App.PreviewAwaitingConfirm);
            Assert.Equal(1, h.App.Config.CurrentModeId);
            Assert.Empty(h.App.Config.FindMode(2)!.Monitors);
            Assert.Equal(writes, h.Store.Writes);
        }

        [Fact]
        public void 未按保留就還原為預覽前的螢幕狀態()
        {
            var h = TwoMonitors(out AppConfig draft);
            ModeConfig play = draft.FindMode(1)!;
            play.SetEnabled(play.SettingsFor(h.Monitors.Monitors[1]), false);
            Assert.Null(h.App.PreviewLayout(play));
            h.FinishSwitch();

            h.App.RevertPreview();

            Assert.Equal(new[] { ("SN0001", true, 0, 0, 1920, 1080, true), ("SN0002", true, 1920, 0, 1280, 720, false) }, States(h.LastApplied));
            Assert.False(h.App.PreviewAwaitingConfirm);
            h.FinishSwitch();
            Assert.Equal(2, h.Monitors.Applied.Count);
            Assert.Equal(1, h.App.Config.CurrentModeId);
            Assert.DoesNotContain(h.Notifier.Shown, n => n.Kind == NotificationKind.Error);
        }

        [Fact]
        public void 按保留後不再還原()
        {
            var h = TwoMonitors(out AppConfig draft);
            Assert.Null(h.App.PreviewLayout(draft.FindMode(1)!));
            h.FinishSwitch();

            h.App.KeepPreview();
            h.App.RevertPreview();

            Assert.False(h.App.PreviewAwaitingConfirm);
            Assert.Single(h.Monitors.Applied);
        }

        [Fact]
        public void 等待確認時從工作列手動切換_預覽作廢不再還原()
        {
            var h = TwoMonitors(out AppConfig draft);
            Assert.Null(h.App.PreviewLayout(draft.FindMode(1)!));
            h.FinishSwitch();

            h.App.ManualSwitch(1);
            Assert.False(h.App.PreviewAwaitingConfirm);
            h.FinishSwitch();
            h.App.RevertPreview();

            Assert.Equal(2, h.Monitors.Applied.Count);
        }

        [Fact]
        public void 預覽失敗時通知原因_不等待確認()
        {
            var h = TwoMonitors(out AppConfig draft);
            int applied = 0;
            h.App.PreviewApplied += () => applied++;
            Assert.Null(h.App.PreviewLayout(draft.FindMode(1)!));

            h.FinishSwitch("螢幕不支援這個顯示模式");

            Assert.Equal(0, applied);
            Assert.False(h.App.PreviewAwaitingConfirm);
            Assert.Equal(("無法預覽螢幕配置：\n螢幕不支援這個顯示模式", NotificationKind.Error), h.Notifier.Shown.Last());
            h.App.RevertPreview();
            Assert.Single(h.Monitors.Applied);
        }

        public static TheoryData<string, string> 無法預覽 => new()
        {
            { "切換中", "正在切換中，請稍候再預覽。" },
            { "重疊", "螢幕配置有誤：「" + Main + "」與「副螢幕」重疊。" },
            { "沒有螢幕開著", "螢幕配置有誤：至少要開啟一台螢幕。" },
        };

        [Theory]
        [MemberData(nameof(無法預覽))]
        public void 無法預覽時說明原因且不套用(string situation, string reason)
        {
            var h = TwoMonitors(out AppConfig draft);
            ModeConfig play = draft.FindMode(1)!;
            switch (situation)
            {
                case "切換中":
                    h.App.ManualSwitch(2);
                    break;
                case "重疊":
                    play.Monitors.Add(Rec("SN0002", 100, 0, name: "副螢幕"));
                    break;
                default:
                    play.UnlistedMonitors = UnlistedMonitors.Off;
                    play.Monitors[0] = play.Monitors[0] with { Enabled = false, Primary = false };
                    break;
            }
            int before = h.Monitors.Applied.Count;

            Assert.Equal(reason, h.App.PreviewLayout(play));
            Assert.Equal(before, h.Monitors.Applied.Count);
        }

        // ---- 螢幕插拔 ----

        [Fact]
        public void 螢幕插拔時通知設定視窗_切換中延後到切換完成才通知一次()
        {
            var h = TwoMonitors(out _);
            int changed = 0;
            h.App.MonitorsChanged += () => changed++;

            h.Monitors.RaiseChanged();
            Assert.Equal(1, changed);

            // 程式自己套用螢幕設定時也會觸發：切換中先不通知（偵測到的可能是過渡狀態），完成後通知一次
            h.App.ManualSwitch(1);
            h.Monitors.RaiseChanged();
            h.Monitors.RaiseChanged();
            Assert.Equal(1, changed);
            h.FinishSwitch();
            Assert.Equal(2, changed);

            // 切換中沒有變化也一樣：完成時螢幕狀態已改變
            h.App.ManualSwitch(1);
            h.FinishSwitch("失敗");
            Assert.Equal(3, changed);
        }
    }
}
