using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AutoDisplayMode.Core;
using Orientation = AutoDisplayMode.Core.Orientation;

namespace AutoDisplayMode.Settings
{
    /// <summary>
    /// 「螢幕配置」分頁：選擇模式，在畫布上擺放目前接上的螢幕，並編輯選取螢幕的開關、主螢幕、座標、解析度、更新率與方向。
    /// 重疊時無法套用、有空隙時警告；「預覽套用」經協調者立即套用，15 秒內未保留就還原。插拔螢幕時重新偵測。
    /// </summary>
    sealed class LayoutPage : SettingsPage
    {
        static readonly string[] OrientationNames = { "橫向", "直向", "橫向（翻轉）", "直向（翻轉）" };

        readonly AppCoordinator app;
        readonly ComboBox modeChoice;
        readonly LayoutCanvas canvas;
        readonly Label monitorName;
        readonly CheckBox enabled;
        readonly CheckBox primary;
        readonly NumericUpDown x;
        readonly NumericUpDown y;
        readonly ComboBox resolution;
        readonly ComboBox refresh;
        readonly ComboBox orientation;
        readonly TableLayoutPanel fields;
        readonly Label detectError;
        readonly Label errors;
        readonly Label warnings;
        readonly Button previewButton;
        AppConfig draft = new();
        IReadOnlyList<DetectedMonitor> detected = Array.Empty<DetectedMonitor>();
        bool showing;            // 正在把資料顯示到欄位上：欄位改變不寫回副本
        bool redetectAfterDrag;  // 拖拉中插拔了螢幕：放開後再重新偵測
        bool previewing;         // 已按「預覽套用」，等待協調者套用完成

        /// <summary>解析度下拉選單的項目（未旋轉的寬高）。</summary>
        sealed record Resolution(int Width, int Height)
        {
            public override string ToString() => Width + " x " + Height;
        }

        /// <summary>更新率下拉選單的項目。</summary>
        sealed record Rate(int Hz)
        {
            public override string ToString() => Hz + " Hz";
        }

        public LayoutPage(AppCoordinator app) : base("螢幕配置")
        {
            this.app = app;

            // ---- 上方：模式 ----
            modeChoice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Anchor = AnchorStyles.Left };
            modeChoice.SelectedIndexChanged += delegate { if (!showing) RefreshAll(); };
            var top = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
            top.Controls.Add(new Label { Text = "要編輯的模式：", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 3, 0) });
            top.Controls.Add(modeChoice);

            // ---- 左邊：畫布 ----
            canvas = new LayoutCanvas { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0) };
            canvas.SelectionChanged += ShowSelected;
            canvas.Moved += OnCanvasMoved;

            // ---- 右邊：選取的螢幕 ----
            monitorName = BoldLabel("", new Padding(3, 0, 3, 6));
            enabled = new CheckBox { Text = "開啟", AutoSize = true };
            enabled.CheckedChanged += delegate { if (!showing && Shown is MonitorSettings s) Edit(m => m.SetEnabled(s, enabled.Checked)); };
            primary = new CheckBox { Text = "主螢幕", AutoSize = true };
            primary.CheckedChanged += delegate
            {
                if (!showing && primary.Checked && Shown is MonitorSettings s) Edit(m => m.UpdateMonitor(s with { Primary = true }));
            };
            x = Coordinate();
            y = Coordinate();
            x.ValueChanged += delegate { OnCoordinateChanged(); };
            y.ValueChanged += delegate { OnCoordinateChanged(); };
            resolution = Choice(120);
            resolution.SelectedIndexChanged += delegate { OnResolutionChanged(); };
            refresh = Choice(120);
            refresh.SelectedIndexChanged += delegate
            {
                if (!showing && refresh.SelectedItem is Rate r && Shown is MonitorSettings s) Edit(m => m.UpdateMonitor(s with { RefreshRate = r.Hz }));
            };
            orientation = Choice(120);
            orientation.Items.AddRange(OrientationNames);
            orientation.SelectedIndexChanged += delegate
            {
                if (!showing && orientation.SelectedIndex >= 0 && Shown is MonitorSettings s)
                    Edit(m => m.UpdateMonitor(s with { Orientation = (Orientation)orientation.SelectedIndex }));
            };

            fields = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3,
                Margin = new Padding(0),
            };
            // 與一般設定分頁相同：欄位放在自動寬度的欄（不會被壓窄），第三欄吸收剩下的寬度，跨三欄的文字在欄寬內換行
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            void Wide(Control c)
            {
                fields.Controls.Add(c);
                fields.SetColumnSpan(c, 3);
            }
            void Row(string caption, Control field)
            {
                fields.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left });
                field.Anchor = AnchorStyles.Left;
                fields.Controls.Add(field);
                fields.Controls.Add(new Label { AutoSize = true, Width = 0 });
            }
            Wide(monitorName);
            Wide(enabled);
            Wide(primary);
            Row("X：", x);
            Row("Y：", y);
            Row("解析度：", resolution);
            Row("更新率：", refresh);
            Row("方向：", orientation);
            Wide(Hint("座標以主螢幕左上角為 (0,0)。拖拉時靠近其他螢幕的邊會自動貼齊（按住 Alt 不貼齊）。"));

            var details = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0) };
            details.Controls.Add(fields);

            // ---- 下方：驗證結果與預覽 ----
            detectError = Message(Color.Firebrick);
            errors = Message(Color.Firebrick);
            warnings = Message(Color.DarkGoldenrod);
            previewButton = new Button { Text = "預覽套用", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(12, 6, 0, 0) };
            previewButton.Click += delegate { Preview(); };
            Label previewHint = Hint("「預覽套用」立即套用這個模式目前編輯中的螢幕設定，15 秒內沒有按「保留變更」就還原。保留後仍要按「套用」或「確定」才會存檔。");
            previewHint.Anchor = AnchorStyles.Left;
            previewHint.Margin = new Padding(3, 6, 3, 0);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            void Span(Control c, int row)
            {
                root.Controls.Add(c, 0, row);
                root.SetColumnSpan(c, 2);
            }
            Span(top, 0);
            root.Controls.Add(canvas, 0, 1);
            root.Controls.Add(details, 1, 1);
            Span(detectError, 2);
            Span(errors, 3);
            Span(warnings, 4);
            root.Controls.Add(previewHint, 0, 5);
            root.Controls.Add(previewButton, 1, 5);
            Controls.Add(root);

            app.MonitorsChanged += OnMonitorsChanged;
            app.PreviewApplied += OnPreviewApplied;
            app.Changed += OnAppChanged;
        }

        ModeConfig? SelectedMode => modeChoice.SelectedItem is ModeItem item ? draft.FindMode(item.Id) : null;

        DetectedMonitor? SelectedMonitor => canvas.Selected is MonitorId id ? detected.FirstOrDefault(d => d.Id == id) : null;

        /// <summary>選取的螢幕在選取模式中顯示的螢幕設定；沒有選取時為 null。</summary>
        MonitorSettings? Shown => SelectedMode is ModeConfig m && SelectedMonitor is DetectedMonitor d ? m.SettingsFor(d) : null;

        /// <summary>模式下拉選單的項目（顯示名稱在建立時決定，切換分頁時重建）。</summary>
        sealed record ModeItem(int Id, string Name)
        {
            public override string ToString() => Name;
        }

        protected override void OnLoad(AppConfig d)
        {
            draft = d;
            int keep = (modeChoice.SelectedItem as ModeItem)?.Id ?? app.Config.CurrentModeId;
            modeChoice.BeginUpdate();
            modeChoice.Items.Clear();
            foreach (ModeConfig m in d.Modes) modeChoice.Items.Add(new ModeItem(m.Id, DisplayName(m)));
            int index = d.Modes.FindIndex(m => m.Id == keep);
            modeChoice.SelectedIndex = index >= 0 ? index : (d.Modes.Count > 0 ? 0 : -1);
            modeChoice.EndUpdate();
            Detect();
            RefreshAll();
        }

        public override void CommitEdits()
        {
            // 讀取 Value 會確認數字欄位中剛打的字（觸發 ValueChanged 寫回副本）
            _ = x.Value;
            _ = y.Value;
        }

        // ------------------------------------------------------------
        //  偵測與顯示
        // ------------------------------------------------------------

        void Detect()
        {
            detected = app.DetectMonitors(out string? error) ?? Array.Empty<DetectedMonitor>();
            detectError.Text = error ?? "";
            detectError.Visible = error != null;
        }

        void OnMonitorsChanged()
        {
            if (IsDisposed) return;
            if (canvas.IsDragging)
            {
                redetectAfterDrag = true;
                return;
            }
            Detect();
            RefreshAll();
        }

        /// <summary>重畫畫布、選取的螢幕欄位與驗證結果。</summary>
        void RefreshAll()
        {
            ModeConfig? mode = SelectedMode;
            List<LayoutCanvas.Item> items = mode == null
                ? new List<LayoutCanvas.Item>()
                : detected.Select(d => new LayoutCanvas.Item(d.Id, MonitorLabel(d), mode.SettingsFor(d))).ToList();
            canvas.SetItems(items);
            if (!items.Exists(i => i.Id == canvas.Selected))
                canvas.Selected = (items.Find(i => i.Shown.Enabled && i.Shown.Primary) ?? items.FirstOrDefault())?.Id;
            ShowSelected();
            ShowIssues();
        }

        /// <summary>畫布與欄位上螢幕的名稱：同名的螢幕（例如兩台同型號）加上接孔區分。</summary>
        string MonitorLabel(DetectedMonitor d)
        {
            string name = d.Name.Trim().Length > 0 ? d.Name.Trim() : d.Id.ToString();
            return detected.Count(o => o.Name == d.Name) > 1 ? name + "（" + d.Connector + "）" : name;
        }

        void ShowSelected()
        {
            DetectedMonitor? d = SelectedMonitor;
            MonitorSettings? s = Shown;
            showing = true;
            try
            {
                fields.Enabled = s != null;
                if (d == null || s == null)
                {
                    monitorName.Text = detected.Count == 0 ? "（沒有偵測到螢幕）" : "（請在左邊選取螢幕）";
                    return;
                }
                monitorName.Text = MonitorLabel(d);
                enabled.Checked = s.Enabled;
                primary.Checked = s.Enabled && s.Primary;
                primary.Enabled = s.Enabled && !s.Primary;   // 改設別台為主螢幕來取代
                x.Value = Clamp(s.X, x);
                y.Value = Clamp(s.Y, y);
                x.Enabled = y.Enabled = s.Enabled && !s.Primary;   // 主螢幕固定在 (0,0)

                resolution.Items.Clear();
                foreach (Resolution r in Resolutions(d, s)) resolution.Items.Add(r);
                resolution.SelectedItem = new Resolution(s.Width, s.Height);
                ShowRates(d, s);
                orientation.SelectedIndex = (int)s.Orientation;
            }
            finally
            {
                showing = false;
            }
        }

        void ShowRates(DetectedMonitor d, MonitorSettings s)
        {
            refresh.Items.Clear();
            foreach (int hz in Rates(d, s.Width, s.Height, s.RefreshRate)) refresh.Items.Add(new Rate(hz));
            refresh.SelectedItem = new Rate(s.RefreshRate);
        }

        void ShowIssues()
        {
            LayoutReport report = SelectedMode is ModeConfig m ? MonitorLayout.Check(m, detected) : new LayoutReport(Array.Empty<string>(), Array.Empty<string>());
            errors.Text = string.Concat(report.Errors.Select(e => "無法套用：" + e + "。\n")).TrimEnd('\n');
            errors.Visible = report.Errors.Count > 0;
            warnings.Text = string.Join("\n", report.Warnings);
            warnings.Visible = report.Warnings.Count > 0;
        }

        /// <summary>
        /// 螢幕支援的解析度（由大到小）。模式記錄的解析度不在清單中時也列出：
        /// Windows 只提供開啟中螢幕的完整清單，關閉中的螢幕只知道原生解析度，記錄的值是擷取時螢幕實際使用的。
        /// </summary>
        static IEnumerable<Resolution> Resolutions(DetectedMonitor d, MonitorSettings s)
        {
            var list = d.SupportedModes.Select(v => new Resolution(v.Width, v.Height)).Distinct().ToList();
            var recorded = new Resolution(s.Width, s.Height);
            if (s.Width > 0 && s.Height > 0 && !list.Contains(recorded)) list.Add(recorded);
            return list.OrderByDescending(r => r.Width * r.Height).ThenByDescending(r => r.Width);
        }

        /// <summary>
        /// 螢幕在 <paramref name="width"/> x <paramref name="height"/> 下支援的更新率（由高到低）；
        /// <paramref name="recorded"/>（模式記錄的更新率，0 表示不加）不在清單中時也列出，理由同上。
        /// </summary>
        static IEnumerable<int> Rates(DetectedMonitor d, int width, int height, int recorded)
        {
            var list = d.SupportedModes.Where(v => v.Width == width && v.Height == height).Select(v => v.RefreshRate).Distinct().ToList();
            if (recorded > 0 && !list.Contains(recorded)) list.Add(recorded);
            return list.OrderByDescending(hz => hz);
        }

        // ------------------------------------------------------------
        //  編輯
        // ------------------------------------------------------------

        /// <summary>修改選取的模式，然後重新顯示。</summary>
        void Edit(Action<ModeConfig> change)
        {
            if (SelectedMode is not ModeConfig m) return;
            change(m);
            MarkEdited();
            RefreshAll();
        }

        void OnCanvasMoved(MonitorId id, int newX, int newY)
        {
            DetectedMonitor? d = detected.FirstOrDefault(m => m.Id == id);
            if (d != null && SelectedMode is ModeConfig mode)
            {
                MonitorSettings s = mode.SettingsFor(d);
                if (s.X != newX || s.Y != newY) Edit(m => m.UpdateMonitor(s with { X = newX, Y = newY }));
            }
            if (redetectAfterDrag)
            {
                redetectAfterDrag = false;
                Detect();
                RefreshAll();
            }
        }

        void OnCoordinateChanged()
        {
            if (showing || Shown is not MonitorSettings s) return;
            int nx = (int)x.Value, ny = (int)y.Value;
            if (nx != s.X || ny != s.Y) Edit(m => m.UpdateMonitor(s with { X = nx, Y = ny }));
        }

        void OnResolutionChanged()
        {
            if (showing || resolution.SelectedItem is not Resolution r || Shown is not MonitorSettings s || SelectedMonitor is not DetectedMonitor d) return;
            // 更新率：新解析度也支援目前的更新率就保留，否則用最高的
            List<int> rates = Rates(d, r.Width, r.Height, 0).ToList();
            int hz = rates.Count == 0 || rates.Contains(s.RefreshRate) ? s.RefreshRate : rates[0];
            Edit(m => m.UpdateMonitor(s with { Width = r.Width, Height = r.Height, RefreshRate = hz }));
        }

        // ------------------------------------------------------------
        //  預覽套用
        // ------------------------------------------------------------

        void Preview()
        {
            CommitEdits();
            if (SelectedMode is not ModeConfig m) return;
            string? error = app.PreviewLayout(m);
            if (error != null)
            {
                MessageBox.Show(FindForm(), "無法預覽套用：\n" + error, FindForm()?.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            previewing = true;
        }

        void OnPreviewApplied()
        {
            if (!previewing || IsDisposed) return;
            previewing = false;
            // 在協調者處理完套用結果之後才跳出（對話框有自己的訊息迴圈）
            BeginInvoke(new Action(() =>
            {
                using var dialog = new KeepChangesDialog(app, FindForm()?.Text ?? "");
                dialog.ShowDialog(FindForm());
            }));
        }

        void OnAppChanged()
        {
            if (IsDisposed) return;
            previewButton.Enabled = !app.IsSwitching;
            if (previewing && !app.IsSwitching && !app.PreviewAwaitingConfirm) previewing = false;   // 預覽失敗（協調者已通知）
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                app.MonitorsChanged -= OnMonitorsChanged;
                app.PreviewApplied -= OnPreviewApplied;
                app.Changed -= OnAppChanged;
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------
        //  版面
        // ------------------------------------------------------------

        static decimal Clamp(decimal v, NumericUpDown n) => Math.Min(n.Maximum, Math.Max(n.Minimum, v));

        /// <summary>
        /// 數字欄位：Margin 不隨 DPI 縮放。NumericUpDown 本身是 ContainerControl，設定視窗依 DPI 縮放時它的 Margin
        /// 會被重複放大（144 DPI 時 3 像素變成數十像素，列高被撐開），大小則只縮放一次、不受影響。
        /// </summary>
        sealed class NumberBox : NumericUpDown
        {
            protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
            {
                Padding margin = Margin;
                base.ScaleControl(factor, specified);
                Margin = margin;
            }
        }

        NumericUpDown Coordinate()
        {
            var n = new NumberBox { Minimum = -100_000, Maximum = 100_000, Width = 90, TextAlign = HorizontalAlignment.Right };
            n.TextChanged += delegate { if (!showing) MarkEdited(); };   // 打字時就啟用「套用」（值在確認時才寫回副本）
            return n;
        }

        static ComboBox Choice(int width) => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };

        static Label Message(Color color) => new()
        {
            AutoSize = true,
            ForeColor = color,
            Margin = new Padding(3, 6, 3, 0),
            Visible = false,
        };
    }

    /// <summary>預覽套用後的確認：「要保留這些變更嗎？」，15 秒內沒有按「保留變更」就還原為預覽前的螢幕狀態。</summary>
    sealed class KeepChangesDialog : Form
    {
        const int Seconds = 15;

        readonly AppCoordinator app;
        readonly Label message;
        readonly Timer timer = new() { Interval = 1000 };
        int remaining = Seconds;
        bool kept;

        public KeepChangesDialog(AppCoordinator app, string title)
        {
            this.app = app;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;   // 設定視窗可能在剛關閉的螢幕上，確認視窗一定要看得到
            StartPosition = FormStartPosition.Manual;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            message = new Label { AutoSize = true, Margin = new Padding(3, 3, 3, 12) };
            // 不用 MinimumSize 撐寬按鈕（依 DPI 縮放時會被放大兩次），改用內距
            var keep = new Button { Text = "保留變更", AutoSize = true, Padding = new Padding(12, 2, 12, 2) };
            var revert = new Button { Text = "還原", AutoSize = true, Padding = new Padding(24, 2, 24, 2) };
            keep.Click += delegate
            {
                kept = true;
                Close();
            };
            revert.Click += delegate { Close(); };
            AcceptButton = revert;   // 畫面看不清楚時按 Enter 或 Esc 都是還原
            CancelButton = revert;

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0) };
            buttons.Controls.AddRange(new Control[] { revert, keep });
            var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
            table.Controls.Add(message);
            table.Controls.Add(buttons);
            Controls.Add(table);

            Font = SystemFonts.MessageBoxFont;
            ResumeLayout(false);
            PerformLayout();

            ShowRemaining();
            timer.Tick += delegate
            {
                if (--remaining <= 0) Close();
                else ShowRemaining();
            };
            app.Changed += OnAppChanged;
        }

        void ShowRemaining() => message.Text = "要保留這些變更嗎？\n" + remaining + " 秒後自動還原為預覽前的螢幕狀態。";

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 置中在主螢幕（預覽後設定視窗所在的螢幕可能已關閉）
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            timer.Start();
        }

        /// <summary>預覽在等待確認時作廢（例如從工作列手動切換）：不必再確認。</summary>
        void OnAppChanged()
        {
            if (!app.PreviewAwaitingConfirm && Visible) Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            app.Changed -= OnAppChanged;
            if (kept) app.KeepPreview();
            else app.RevertPreview();   // 預覽已作廢時不動作
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
