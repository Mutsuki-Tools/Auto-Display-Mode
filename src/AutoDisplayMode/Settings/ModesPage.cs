using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Settings
{
    /// <summary>
    /// 「模式」分頁：新增、刪除、重新命名、複製模式，設定圖示顏色、手動切換進入時的自動切換、未記錄的螢幕，
    /// 以及在視窗中擷取（結果先存進副本，套用時存入的模式成為目前模式）。
    /// </summary>
    sealed class ModesPage : SettingsPage
    {
        readonly AppCoordinator app;
        readonly ListBox list;
        readonly TextBox name;
        readonly Label nameError;
        readonly FlowLayoutPanel colors;
        readonly ComboBox manualAutoSwitch;
        readonly ComboBox unlisted;
        readonly Label monitorsSummary;
        readonly Label captureNote;
        readonly Button removeButton;
        readonly Button duplicateButton;
        readonly Panel details;
        readonly TableLayoutPanel detailsTable;
        AppConfig draft = new();
        bool showing;   // 正在把選取的模式顯示到欄位上：欄位改變不寫回副本

        public ModesPage(AppCoordinator app) : base("模式")
        {
            this.app = app;

            // ---- 左邊：模式清單與按鈕 ----
            list = new ListBox
            {
                Dock = DockStyle.Fill,
                DrawMode = DrawMode.OwnerDrawFixed,
                IntegralHeight = false,
            };
            list.DrawItem += DrawModeItem;
            list.SelectedIndexChanged += delegate { ShowSelected(); };

            var addButton = ListButton("新增");
            duplicateButton = ListButton("複製");
            removeButton = ListButton("刪除");
            addButton.Click += delegate { AddMode(); };
            duplicateButton.Click += delegate { DuplicateMode(); };
            removeButton.Click += delegate { RemoveMode(); };
            var captureNewButton = ListButton("以目前螢幕狀態新增模式");
            captureNewButton.Click += delegate { CaptureToNewMode(); };

            // 左欄：清單在上，下方一列三個等寬按鈕，再下方一個整列寬的按鈕
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Margin = new Padding(0, 0, 12, 0) };
            for (int i = 0; i < 3; i++) left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Controls.Add(list, 0, 0);
            left.SetColumnSpan(list, 3);
            left.Controls.Add(addButton, 0, 1);
            left.Controls.Add(duplicateButton, 1, 1);
            left.Controls.Add(removeButton, 2, 1);
            left.Controls.Add(captureNewButton, 0, 2);
            left.SetColumnSpan(captureNewButton, 3);
            addButton.Margin = new Padding(0, 6, 3, 0);
            duplicateButton.Margin = new Padding(3, 6, 3, 0);
            removeButton.Margin = new Padding(3, 6, 0, 0);

            // ---- 右邊：選取的模式 ----
            name = new TextBox { Width = 200, Anchor = AnchorStyles.Left };
            name.TextChanged += delegate { OnNameChanged(); };
            nameError = new Label { AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(3, 3, 3, 0), Visible = false };

            colors = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0) };
            foreach (int rgb in ModeConfig.Palette)
            {
                int c = rgb;
                Button swatch = Swatch(c);
                swatch.Click += delegate { SetColor(c); };
                colors.Controls.Add(swatch);
            }
            var otherColor = new Button { Text = "其他…", AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            otherColor.Click += delegate { PickOtherColor(); };
            colors.Controls.Add(otherColor);

            manualAutoSwitch = Choice("維持", "開啟", "關閉");
            manualAutoSwitch.SelectedIndexChanged += delegate
            {
                if (Selected is ModeConfig m && !showing) { m.ManualAutoSwitch = (ManualAutoSwitch)manualAutoSwitch.SelectedIndex; MarkEdited(); }
            };
            unlisted = Choice("維持現狀", "關閉");
            unlisted.SelectedIndexChanged += delegate
            {
                if (Selected is ModeConfig m && !showing) { m.UnlistedMonitors = (UnlistedMonitors)unlisted.SelectedIndex; MarkEdited(); }
            };

            monitorsSummary = new Label { AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            var captureButton = new Button { Text = "以目前螢幕狀態覆蓋此模式", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 0) };
            captureButton.Click += delegate { CaptureToSelectedMode(); };
            captureNote = new Label { AutoSize = true, ForeColor = Color.SeaGreen, Margin = new Padding(3, 3, 3, 0), Visible = false };

            // 右欄可捲動；內層表格寬度跟著右欄（Dock = Top），不會出現水平捲軸
            detailsTable = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                Margin = new Padding(0),
            };
            detailsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            void Add(Control c) => detailsTable.Controls.Add(c);
            Add(Section("名稱", first: true));
            Add(Wrap(name));
            Add(nameError);
            Add(Section("圖示顏色"));
            Add(colors);
            Add(Section("手動切換進入此模式時，自動切換要"));
            Add(Wrap(manualAutoSwitch));
            Add(SmallHint("例如手動切到省電時選「關閉」，就不會一動滑鼠又被切回來。"));
            Add(Section("未記錄的螢幕"));
            Add(Wrap(unlisted));
            Add(SmallHint("套用此模式時，接上但此模式沒有記錄的螢幕要維持現狀或關閉。"));
            Add(Section("螢幕設定"));
            Add(monitorsSummary);
            Add(captureButton);
            Add(captureNote);
            Add(SmallHint("擷取的結果按「套用」或「確定」後才存檔，存入的模式隨即成為目前模式。"));

            details = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0) };
            details.Controls.Add(detailsTable);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(left, 0, 0);
            root.Controls.Add(details, 1, 0);
            Controls.Add(root);

            app.Changed += OnAppChanged;
        }

        /// <summary>在視窗中最後一次擷取存入的模式（套用時傳給協調者）；沒有擷取時為 null。</summary>
        public int? CapturedModeId { get; private set; }

        /// <summary>在視窗中擷取過、尚未存檔的所有模式（顯示「已擷取」標示用）。</summary>
        readonly HashSet<int> capturedModes = new();

        void MarkCaptured(int modeId)
        {
            CapturedModeId = modeId;
            capturedModes.Add(modeId);
        }

        ModeConfig? Selected => list.SelectedItem is int id ? draft.FindMode(id) : null;

        protected override void OnLoad(AppConfig d)
        {
            draft = d;
            int? keep = list.SelectedItem as int?;
            RefreshList(keep ?? app.Config.CurrentModeId);
        }

        public override void OnApplied()
        {
            CapturedModeId = null;
            capturedModes.Clear();
            ShowSelected();
        }

        /// <summary>重建清單並選取 <paramref name="selectId"/>（不存在時選第一個）。</summary>
        void RefreshList(int selectId)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (ModeConfig m in draft.Modes) list.Items.Add(m.Id);
            int index = list.Items.IndexOf(selectId);
            list.SelectedIndex = index >= 0 ? index : (list.Items.Count > 0 ? 0 : -1);
            list.EndUpdate();
            ShowSelected();
        }

        /// <summary>把選取的模式顯示到右邊的欄位。</summary>
        void ShowSelected()
        {
            ModeConfig? m = Selected;
            showing = true;
            try
            {
                details.Enabled = duplicateButton.Enabled = removeButton.Enabled = m != null;
                if (m == null) return;
                if (name.Text != m.Name) name.Text = m.Name;
                manualAutoSwitch.SelectedIndex = (int)m.ManualAutoSwitch;
                unlisted.SelectedIndex = (int)m.UnlistedMonitors;
            }
            finally
            {
                showing = false;
            }
            UpdateNameError();
            UpdateColors();
            UpdateMonitors();
        }

        void OnNameChanged()
        {
            if (showing || Selected is not ModeConfig m) return;
            m.Name = name.Text;
            MarkEdited();
            UpdateNameError();
            list.Invalidate();
        }

        void UpdateNameError()
        {
            ModeConfig? m = Selected;
            string? error = m == null ? null : draft.ValidateModeName(m.Id, m.Name);
            nameError.Text = error ?? "";
            nameError.Visible = error != null;
        }

        void SetColor(int rgb)
        {
            if (Selected is not ModeConfig m) return;
            m.Color = rgb;
            MarkEdited();
            UpdateColors();
            list.Invalidate();
        }

        void PickOtherColor()
        {
            if (Selected is not ModeConfig m) return;
            using var dialog = new ColorDialog
            {
                Color = ToColor(m.Color),
                FullOpen = true,
                CustomColors = ModeConfig.Palette.Select(ToBgr).ToArray(),
            };
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
                SetColor((dialog.Color.R << 16) | (dialog.Color.G << 8) | dialog.Color.B);
        }

        /// <summary>選取中的顏色方塊加粗外框。</summary>
        void UpdateColors()
        {
            int color = Selected?.Color ?? ModeConfig.NoColor;
            foreach (Control c in colors.Controls)
            {
                if (c is Button b && b.Tag is int rgb)
                {
                    bool on = rgb == color;
                    b.FlatAppearance.BorderSize = on ? 3 : 1;
                    b.FlatAppearance.BorderColor = on ? SystemColors.ControlText : SystemColors.ControlDark;
                }
            }
        }

        void UpdateMonitors()
        {
            ModeConfig? m = Selected;
            if (m == null) return;
            monitorsSummary.Text = m.Monitors.Count == 0
                ? "（沒有記錄任何螢幕）"
                : "記錄了 " + m.Monitors.Count + " 台螢幕：" + string.Join("、", m.Monitors.Select(Describe));
            captureNote.Text = "已擷取目前螢幕狀態（尚未存檔）。";
            captureNote.Visible = capturedModes.Contains(m.Id);
        }

        static string Describe(MonitorSettings s)
        {
            string label = s.Name.Length > 0 ? s.Name : s.Id.ToString();
            return label + (s.Enabled ? "（" + s.Width + "x" + s.Height + "）" : "（關閉）");
        }

        // ---- 新增、複製、刪除 ----

        void AddMode()
        {
            ModeConfig m = draft.AddMode();
            MarkEdited();
            RefreshList(m.Id);
            FocusName();
        }

        void DuplicateMode()
        {
            if (Selected is not ModeConfig m) return;
            ModeConfig copy = draft.DuplicateMode(m.Id);
            MarkEdited();
            RefreshList(copy.Id);
            FocusName();
        }

        void RemoveMode()
        {
            if (Selected is not ModeConfig m) return;
            string label = DisplayName(m);

            // 目前模式讀協調者的即時值：設定視窗開著時仍可從工作列手動切換
            if (draft.ModeRemovalBlocker(m.Id, app.Config.CurrentModeId) is string reason)
            {
                MessageBox.Show(FindForm(), "無法刪除「" + label + "」：" + reason + "。", FindForm()?.Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<SwitchCondition> affected = draft.ConditionsReferencing(m.Id);
            string text = "要刪除模式「" + label + "」嗎？";
            if (affected.Count > 0)
                text += "\n\n以下切換條件的來源模式或目標模式是「" + label + "」，會一併刪除：\n" +
                        string.Concat(affected.Select(s => "・" + s.Describe(draft) + "\n"));
            text += "\n按「套用」或「確定」後才會存檔。";
            if (MessageBox.Show(FindForm(), text, FindForm()?.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

            int index = list.SelectedIndex;
            draft.RemoveMode(m.Id);
            if (CapturedModeId == m.Id) CapturedModeId = null;
            capturedModes.Remove(m.Id);
            MarkEdited();
            RefreshList(draft.Modes[Math.Min(index, draft.Modes.Count - 1)].Id);
        }

        // ---- 擷取 ----

        void CaptureToSelectedMode()
        {
            if (Selected is not ModeConfig m) return;
            string label = DisplayName(m);
            DialogResult r = MessageBox.Show(FindForm(),
                "要以目前的螢幕狀態（每台螢幕的開關、座標、解析度、更新率、方向與主螢幕）覆蓋「" + label + "」的螢幕設定嗎？\n\n" +
                "目前沒接上的螢幕維持原本的設定。按「套用」或「確定」後才會存檔，「" + label + "」也會成為目前模式。",
                FindForm()?.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return;

            List<MonitorSettings>? captured = app.CaptureForSettings(m.Monitors);   // 失敗時協調者會通知
            if (captured == null) return;
            m.Monitors.Clear();
            m.Monitors.AddRange(captured);
            MarkCaptured(m.Id);
            MarkEdited();
            ShowSelected();
        }

        void CaptureToNewMode()
        {
            List<MonitorSettings>? captured = app.CaptureForSettings(Array.Empty<MonitorSettings>());
            if (captured == null) return;
            ModeConfig m = draft.AddMode();
            m.Monitors.AddRange(captured);
            MarkCaptured(m.Id);
            MarkEdited();
            RefreshList(m.Id);
            FocusName();
        }

        void FocusName()
        {
            name.Focus();
            name.SelectAll();
        }

        // ---- 目前模式的即時標示 ----

        void OnAppChanged()
        {
            if (IsDisposed) return;
            list.Invalidate();   // 目前模式可能從工作列改變
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) app.Changed -= OnAppChanged;
            base.Dispose(disposing);
        }

        void DrawModeItem(object? sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index < 0 || list.Items[e.Index] is not int id || draft.FindMode(id) is not ModeConfig m) return;

            int size = e.Bounds.Height - 6;
            var swatch = new Rectangle(e.Bounds.Left + 3, e.Bounds.Top + 3, size, size);
            using (var brush = new SolidBrush(ToColor(m.Color))) e.Graphics.FillRectangle(brush, swatch);
            e.Graphics.DrawRectangle(SystemPens.ControlDark, swatch);

            string text = DisplayName(m) + (id == app.Config.CurrentModeId ? "（目前）" : "");
            var textBounds = new Rectangle(swatch.Right + 6, e.Bounds.Top, e.Bounds.Right - swatch.Right - 6, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, text, e.Font ?? Font, textBounds, e.ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        }

        static Color ToColor(int rgb) =>
            rgb == ModeConfig.NoColor ? SystemColors.Control : Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

        /// <summary>ColorDialog 的自訂色以 0x00BBGGRR 表示。</summary>
        static int ToBgr(int rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

        // ---- 版面 ----

        // 說明文字由表格限制在欄寬內自動換行（與一般設定分頁相同）。不要在 Resize 中設定標籤的 MaximumSize：
        // 設定視窗依 DPI 縮放的過程中會觸發 Resize，改 MaximumSize 會讓縮放重跑，邊距被放大好幾次。

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            list.ItemHeight = Font.Height + 8;
        }

        /// <summary>左欄的按鈕：填滿所在的格子。</summary>
        static Button ListButton(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 6, 0, 0),
        };

        /// <summary>
        /// 以自動大小的面板包住固定寬度的欄位：表格建立時欄寬還很小，直接放進表格的欄位會被壓窄，之後也不會恢復。
        /// </summary>
        static Control Wrap(Control c)
        {
            var panel = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
            panel.Controls.Add(c);
            return panel;
        }

        /// <summary>灰色的說明文字（與上方欄位的間距較小）。</summary>
        static Label SmallHint(string text)
        {
            Label h = Hint(text);
            h.Margin = new Padding(3, 2, 3, 0);
            return h;
        }

        static Button Swatch(int rgb)
        {
            var b = new Button
            {
                Tag = rgb,
                Size = new Size(24, 24),
                FlatStyle = FlatStyle.Flat,
                BackColor = ToColor(rgb),
                Margin = new Padding(3, 2, 3, 2),
                UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.MouseOverBackColor = b.BackColor;
            b.FlatAppearance.MouseDownBackColor = b.BackColor;
            return b;
        }

        static ComboBox Choice(params string[] items)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, Anchor = AnchorStyles.Left };
            c.Items.AddRange(items);
            return c;
        }

        /// <summary>粗體的欄位標題；<paramref name="first"/> 為第一個時上方不留間距。</summary>
        Label Section(string text, bool first = false) => BoldLabel(text, new Padding(3, first ? 0 : 14, 3, 4));
    }
}
