using System.Linq;
using AutoDisplayMode.Core;
using Xunit;
using static AutoDisplayMode.Core.Tests.Harness;

namespace AutoDisplayMode.Core.Tests
{
    /// <summary>設定視窗的模式分頁：在副本中新增、刪除、重新命名、複製模式，以及在視窗中擷取，套用後寫入 cfg。</summary>
    public class ModesTabTests
    {
        static AppConfig Reread(Harness h) => new Harness(h.Store.Text).App.Config;

        // ---- 刪除 ----

        [Fact]
        public void 刪除模式時連帶刪除來源或目標是該模式的切換條件()
        {
            var h = Modes(Play,
                Sw.Idle(Play, Save, 5), Sw.Usage(Save, Play), Sw.Idle(Play, AllOff, 30), Sw.Usage(AllOff, Play));
            AppConfig draft = h.App.Config.Clone();

            var removed = draft.RemoveMode(Save);

            Assert.Equal(new[] { (Play, Save), (Save, Play) }, removed.Select(s => (s.FromModeId, s.ToModeId)));
            Assert.Null(h.App.ApplySettings(draft));
            var reread = Reread(h);
            // 編號依順序補齊：「全關」從 3 變成 2，切換條件跟著改
            Assert.Equal(new[] { (1, "遊玩"), (2, "全關") }, reread.Modes.Select(m => (m.Id, m.Name)));
            Assert.Equal(new[] { (1, 2), (2, 1) }, reread.SwitchConditions.Select(s => (s.FromModeId, s.ToModeId)));
        }

        // ---- 調整順序 ----

        [Fact]
        public void 調整模式順序後套用_依新順序重新編號_切換條件與目前模式跟著改()
        {
            var h = PlayAndSave(Play);   // 遊玩=1、省電=2、全關=3；「遊玩」閒置 5 分鐘→「省電」；「省電」偵測到使用→「遊玩」
            AppConfig draft = h.App.Config.Clone();

            draft.MoveMode(2, 0);   // 「全關」移到最前面
            Assert.Null(h.App.ApplySettings(draft));

            var reread = Reread(h);
            Assert.Equal(new[] { (1, "全關"), (2, "遊玩"), (3, "省電") }, reread.Modes.Select(m => (m.Id, m.Name)));
            Assert.Equal(new[] { (2, 3), (3, 2) }, reread.SwitchConditions.Select(s => (s.FromModeId, s.ToModeId)));
            Assert.Equal(2, reread.CurrentModeId);
            Assert.Equal((2, "遊玩"), (h.App.Config.CurrentModeId, h.App.CurrentModeName));

            // 設定視窗的副本也一起重新編號，繼續編輯、再次套用時與目前的設定一致
            Assert.Equal(new[] { (1, "全關"), (2, "遊玩"), (3, "省電") }, draft.Modes.Select(m => (m.Id, m.Name)));
            Assert.Equal(new[] { (2, 3), (3, 2) }, draft.SwitchConditions.Select(s => (s.FromModeId, s.ToModeId)));
            Assert.Null(h.App.ApplySettings(draft));
            Assert.Equal((2, "遊玩"), (h.App.Config.CurrentModeId, h.App.CurrentModeName));

            // 切換條件照常運作
            Assert.Equal(5 * 60, h.RunUntilSwitch(10 * 60));
            Assert.Equal("省電", Assert.Single(h.Monitors.AppliedTargets));
        }

        [Fact]
        public void 調整順序後擷取的模式仍成為目前模式()
        {
            var h = Modes(Play);
            AppConfig draft = h.App.Config.Clone();
            ModeConfig allOff = draft.FindMode(AllOff)!;
            var captured = h.App.CaptureForSettings(allOff.Monitors)!;
            allOff.Monitors.Clear();
            allOff.Monitors.AddRange(captured);

            draft.MoveMode(2, 0);
            Assert.Null(h.App.ApplySettings(draft, AllOff));

            Assert.Equal((1, "全關"), (h.App.Config.CurrentModeId, h.App.CurrentModeName));
        }

        [Fact]
        public void 寫入cfg失敗時不重新編號_副本與設定都維持原本的編號()
        {
            var h = PlayAndSave(Play);
            AppConfig draft = h.App.Config.Clone();
            draft.MoveMode(2, 0);
            h.Store.FailWrites = true;

            Assert.NotNull(h.App.ApplySettings(draft));

            Assert.Equal(new[] { (3, "全關"), (1, "遊玩"), (2, "省電") }, draft.Modes.Select(m => (m.Id, m.Name)));
            Assert.Equal(new[] { (1, 2), (2, 1) }, draft.SwitchConditions.Select(s => (s.FromModeId, s.ToModeId)));
            Assert.Equal(new[] { (1, "遊玩"), (2, "省電"), (3, "全關") }, h.App.Config.Modes.Select(m => (m.Id, m.Name)));
        }

        [Fact]
        public void 目前模式不可刪除_說明原因且套用時也擋下()
        {
            var h = Modes(Save);
            AppConfig draft = h.App.Config.Clone();

            Assert.Equal("不能刪除目前模式", draft.ModeRemovalBlocker(Save, h.App.Config.CurrentModeId));
            Assert.Null(draft.ModeRemovalBlocker(Play, h.App.Config.CurrentModeId));

            draft.RemoveMode(Save);
            Assert.Contains("不能刪除目前模式", h.App.ApplySettings(draft));
            Assert.Equal(3, h.App.Config.Modes.Count);
        }

        [Fact]
        public void 最後一個模式不可刪除_說明原因且套用時也擋下()
        {
            var h = Modes(Play);
            AppConfig draft = h.App.Config.Clone();
            draft.RemoveMode(Save);
            draft.RemoveMode(AllOff);

            Assert.Equal("至少要保留一個模式", draft.ModeRemovalBlocker(Play, currentModeId: 99));

            draft.RemoveMode(Play);
            Assert.Contains("至少要保留一個模式", h.App.ApplySettings(draft));
            Assert.Equal(3, h.App.Config.Modes.Count);
        }

        // ---- 新增與複製 ----

        [Fact]
        public void 新增模式配新的ID與未使用的顏色_名稱不重複()
        {
            var h = Modes(Play);
            AppConfig draft = h.App.Config.Clone();

            ModeConfig a = draft.AddMode();
            ModeConfig b = draft.AddMode();

            Assert.Equal((4, "新模式"), (a.Id, a.Name));
            Assert.Equal((5, "新模式 2"), (b.Id, b.Name));
            Assert.Empty(a.Monitors);
            Assert.Equal(draft.Modes.Count, draft.Modes.Select(m => m.Color).Distinct().Count());   // 顏色都不同
            Assert.Null(h.App.ApplySettings(draft));
            var reread = Reread(h);
            Assert.Equal(new[] { "遊玩", "省電", "全關", "新模式", "新模式 2" }, reread.Modes.Select(m => m.Name));
            Assert.Equal(a.Color, reread.FindMode(4)!.Color);
        }

        [Fact]
        public void 刪除最大ID的模式後新增_ID不與副本中的模式重複()
        {
            var h = Modes(Play);
            AppConfig draft = h.App.Config.Clone();
            draft.RemoveMode(AllOff);

            Assert.Equal(AllOff, draft.AddMode().Id);   // 被刪掉的模式與它的切換條件已不在副本中
        }

        [Fact]
        public void 複製模式產生獨立的螢幕設定_設定與原模式相同但ID顏色名稱不同()
        {
            var h = Modes(Play);
            AppConfig draft = h.App.Config.Clone();
            ModeConfig save = draft.FindMode(Save)!;
            save.ManualAutoSwitch = ManualAutoSwitch.Off;
            save.UnlistedMonitors = UnlistedMonitors.Off;

            ModeConfig copy = draft.DuplicateMode(Save);
            copy.Monitors[0] = copy.Monitors[0] with { Width = 640, Height = 480 };

            Assert.Equal((4, "省電 的複本"), (copy.Id, copy.Name));
            Assert.Equal("省電 的複本 2", draft.DuplicateMode(Save).Name);
            Assert.Equal((ManualAutoSwitch.Off, UnlistedMonitors.Off), (copy.ManualAutoSwitch, copy.UnlistedMonitors));
            Assert.NotEqual(save.Color, copy.Color);
            Assert.Null(h.App.ApplySettings(draft));

            var reread = Reread(h);
            Assert.Equal(1280, reread.FindMode(Save)!.Monitors.Single().Width);
            Assert.Equal(640, reread.FindMode(4)!.Monitors.Single().Width);
            Assert.Equal(ManualAutoSwitch.Off, reread.FindMode(4)!.ManualAutoSwitch);
        }

        // ---- 在設定視窗中擷取 ----

        static readonly DetectedMonitor Captured = Mon.On("SN0001", 0, 0, 2560, 1440, 144, primary: true);

        /// <summary>開著設定視窗，接上的螢幕改為 2560x1440，在副本中把目前螢幕狀態擷取進 <paramref name="modeId"/>。</summary>
        static AppConfig CaptureInto(Harness h, int modeId)
        {
            h.App.OnSettingsWindowOpened();
            AppConfig draft = h.App.Config.Clone();
            h.Monitors.Monitors = new() { Captured };
            ModeConfig mode = draft.FindMode(modeId)!;
            var result = h.App.CaptureForSettings(mode.Monitors);
            Assert.NotNull(result);
            mode.Monitors.Clear();
            mode.Monitors.AddRange(result!);
            return draft;
        }

        [Fact]
        public void 視窗內擷取只回傳結果_套用前不存檔也不改變目前模式()
        {
            var h = Modes(Play);
            int writes = h.Store.Writes;

            CaptureInto(h, Save);

            Assert.Equal(writes, h.Store.Writes);
            Assert.Equal(Play, h.App.Config.CurrentModeId);
            Assert.Equal(1280, h.App.Config.FindMode(Save)!.Monitors.Single().Width);
        }

        [Fact]
        public void 擷取到非目前模式並套用後_目前模式改為該模式並存檔()
        {
            var h = Modes(Play);
            AppConfig draft = CaptureInto(h, Save);
            int changes = 0;
            h.App.Changed += () => changes++;

            Assert.Null(h.App.ApplySettings(draft, capturedModeId: Save));

            Assert.Equal(Save, h.App.Config.CurrentModeId);
            Assert.True(changes > 0);   // 工作列圖示隨之更新
            var reread = Reread(h);
            Assert.Equal(Save, reread.CurrentModeId);
            Assert.Equal((2560, 1440, 144), reread.FindMode(Save)!.Monitors.Select(m => (m.Width, m.Height, m.RefreshRate)).Single());
        }

        [Fact]
        public void 擷取並套用後閒置計時歸零()
        {
            var h = Modes(Play, Sw.Idle(Save, AllOff, 5));
            h.Run(4 * 60);   // 在「遊玩」閒置 4 分鐘
            AppConfig draft = h.App.Config.Clone();
            h.Monitors.Monitors = new() { Captured };
            ModeConfig save = draft.FindMode(Save)!;
            var result = h.App.CaptureForSettings(save.Monitors)!;
            save.Monitors.Clear();
            save.Monitors.AddRange(result);

            Assert.Null(h.App.ApplySettings(draft, capturedModeId: Save));

            Assert.Equal(5 * 60, h.RunUntilSwitch(10 * 60));   // 從套用時重新計時，不是再 1 分鐘
        }

        [Fact]
        public void 沒有擷取時套用_目前模式仍以協調者為準()
        {
            var h = Modes(Play);
            AppConfig draft = h.App.Config.Clone();
            draft.CurrentModeId = Save;

            Assert.Null(h.App.ApplySettings(draft, capturedModeId: null));

            Assert.Equal(Play, h.App.Config.CurrentModeId);
        }

        [Fact]
        public void 擷取的模式在套用前被刪除時_目前模式維持不變()
        {
            var h = Modes(Play);
            AppConfig draft = CaptureInto(h, Save);
            draft.RemoveMode(Save);

            Assert.Null(h.App.ApplySettings(draft, capturedModeId: Save));

            Assert.Equal(Play, h.App.Config.CurrentModeId);
            Assert.DoesNotContain("省電", h.App.Config.Modes.Select(m => m.Name));   // 刪除後編號補齊，不能用編號判斷
        }

        [Fact]
        public void 擷取後從工作列手動切換_套用時擷取的模式不再成為目前模式()
        {
            var h = Modes(Play);
            AppConfig draft = CaptureInto(h, Save);
            h.App.ManualSwitch(AllOff);
            h.FinishSwitch();

            Assert.Null(h.App.ApplySettings(draft, capturedModeId: Save));

            Assert.Equal(AllOff, h.App.Config.CurrentModeId);   // 實際螢幕狀態是「全關」，不是擷取時的狀態
            Assert.Equal(2560, h.App.Config.FindMode(Save)!.Monitors.Single().Width);   // 擷取的螢幕設定仍會存入
        }

        [Fact]
        public void 擷取多個模式時_最後一次擷取的模式成為目前模式_每個擷取結果都存入()
        {
            var h = Modes(Play);
            AppConfig draft = CaptureInto(h, Save);
            ModeConfig allOff = draft.FindMode(AllOff)!;
            var result = h.App.CaptureForSettings(allOff.Monitors)!;
            allOff.Monitors.Clear();
            allOff.Monitors.AddRange(result);

            Assert.Null(h.App.ApplySettings(draft, capturedModeId: AllOff));

            Assert.Equal(AllOff, h.App.Config.CurrentModeId);
            var reread = Reread(h);
            Assert.Equal(2560, reread.FindMode(Save)!.Monitors.Single().Width);
            Assert.Equal(2560, reread.FindMode(AllOff)!.Monitors.Single().Width);
        }

        [Fact]
        public void 偵測螢幕失敗時視窗內擷取沒有結果並通知()
        {
            var h = Modes(Play);
            h.App.OnSettingsWindowOpened();
            h.Monitors.Fail = true;

            Assert.Null(h.App.CaptureForSettings(h.App.Config.FindMode(Save)!.Monitors));
            Assert.Contains(("無法偵測螢幕：偵測失敗", NotificationKind.Error), h.Notifier.Shown);
        }

        [Fact]
        public void 切換中不能在視窗內擷取_也不能套用()
        {
            var h = Modes(Play);
            AppConfig draft = CaptureInto(h, Save);
            h.App.ManualSwitch(AllOff);
            Assert.True(h.App.IsSwitching);

            Assert.Null(h.App.CaptureForSettings(draft.FindMode(Save)!.Monitors));
            Assert.Contains(("正在切換中，請稍候。", NotificationKind.Info), h.Notifier.Shown);

            Assert.Contains("正在切換中", h.App.ApplySettings(draft, capturedModeId: Save));
            Assert.Equal(1280, h.App.Config.FindMode(Save)!.Monitors.Single().Width);
        }

        // ---- 名稱 ----

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void 模式名稱不可空白(string name)
        {
            var h = Modes(Play);
            int writes = h.Store.Writes;
            AppConfig draft = h.App.Config.Clone();
            draft.FindMode(Save)!.Name = name;

            Assert.Equal("模式名稱不可空白", draft.ValidateModeName(Save, name));
            Assert.Contains("模式名稱不可空白", h.App.ApplySettings(draft));
            Assert.Equal("省電", h.App.Config.FindMode(Save)!.Name);
            Assert.Equal(writes, h.Store.Writes);
        }

        [Theory]
        [InlineData("遊玩")]
        [InlineData(" 遊玩 ")]
        public void 模式名稱不可重複(string name)
        {
            var h = Modes(Play);
            AppConfig draft = h.App.Config.Clone();
            draft.FindMode(Save)!.Name = name;

            Assert.Equal("已經有名為「遊玩」的模式", draft.ValidateModeName(Save, name));
            Assert.Contains("已經有名為「遊玩」的模式", h.App.ApplySettings(draft));
            Assert.Equal("省電", h.App.Config.FindMode(Save)!.Name);
        }

        [Fact]
        public void 重新命名不影響切換條件_名稱前後的空白會去掉()
        {
            var h = PlayAndSave(Play);
            AppConfig draft = h.App.Config.Clone();
            draft.FindMode(Save)!.Name = "  睡覺  ";

            Assert.Null(draft.ValidateModeName(Save, "  睡覺  "));
            Assert.Null(h.App.ApplySettings(draft));

            var reread = Reread(h);
            Assert.Equal("睡覺", reread.FindMode(Save)!.Name);
            Assert.Equal(new[] { (Play, Save), (Save, Play) }, reread.SwitchConditions.Select(s => (s.FromModeId, s.ToModeId)));
            Assert.Equal("「遊玩」閒置 5 分鐘（鍵盤滑鼠、聲音）→「睡覺」", h.App.Config.SwitchConditions[0].Describe(h.App.Config));
        }
    }
}
