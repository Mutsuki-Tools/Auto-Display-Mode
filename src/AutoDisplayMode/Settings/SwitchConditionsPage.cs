using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Settings
{
    /// <summary>
    /// 「切換條件」分頁：以表格新增、編輯、刪除切換條件，並以拖拉（或上移、下移）調整順序。
    /// 表格順序就是評估順序（<see cref="AppConfig.SwitchConditions"/> 的順序）。
    /// </summary>
    sealed class SwitchConditionsPage : SettingsPage
    {
        const int FromCol = 0, KindCol = 1, AmountCol = 2, UnitCol = 3, MouseCol = 4, AudioCol = 5, ToCol = 6;

        readonly AppCoordinator app;
        readonly DataGridView grid;
        readonly DataGridViewComboBoxColumn fromColumn;
        readonly DataGridViewComboBoxColumn toColumn;
        readonly Button removeButton;
        readonly Button upButton;
        readonly Button downButton;
        readonly Label errors;
        AppConfig draft = new();
        bool filling;   // 正在把副本顯示到表格上：儲存格改變不寫回副本
        TextBox? amountEditor;   // 正在編輯的閒置時間欄位（打字時即時檢查）

        // 拖拉排序
        int dragIndex = -1;
        Rectangle dragBox;
        int dropInsert = -1;   // 放開時要插入的位置（畫插入線用）

        public SwitchConditionsPage(AppCoordinator app) : base("切換條件")
        {
            this.app = app;

            fromColumn = ComboColumn("來源模式", 100);
            toColumn = ComboColumn("目標模式", 100);
            var kindColumn = ChoiceColumn("條件", 95,
                new Choice((int)SwitchConditionKind.Idle, "閒置"),
                new Choice((int)SwitchConditionKind.UsageDetected, "偵測到使用"));
            var amountColumn = new DataGridViewTextBoxColumn { HeaderText = "閒置時間", FillWeight = 60, MaxInputLength = 9 };
            amountColumn.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            var unitColumn = ChoiceColumn("單位", 55,
                new Choice((int)IdleUnit.Minutes, "分鐘"),
                new Choice((int)IdleUnit.Hours, "小時"));
            var mouseColumn = new DataGridViewCheckBoxColumn { HeaderText = "鍵盤滑鼠", FillWeight = 88 };
            var audioColumn = new DataGridViewCheckBoxColumn { HeaderText = "聲音", FillWeight = 45 };

            grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToOrderColumns = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing,
                SelectionMode = DataGridViewSelectionMode.RowHeaderSelect,
                MultiSelect = false,
                EditMode = DataGridViewEditMode.EditOnEnter,
                BackgroundColor = SystemColors.Window,
                AllowDrop = true,
                ShowCellToolTips = true,
                Margin = new Padding(0, 6, 0, 0),
            };
            grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.Columns.AddRange(fromColumn, kindColumn, amountColumn, unitColumn, mouseColumn, audioColumn, toColumn);
            foreach (DataGridViewColumn c in grid.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;

            grid.CurrentCellDirtyStateChanged += delegate
            {
                // 下拉選單與勾選框一改變就送出，讓條件類型的切換與錯誤提示立即反映
                if (grid.CurrentCell is DataGridViewComboBoxCell or DataGridViewCheckBoxCell && grid.IsCurrentCellDirty)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.CellValueChanged += (_, e) => OnCellChanged(e.RowIndex, e.ColumnIndex);
            grid.EditingControlShowing += OnEditingControlShowing;
            grid.CellEndEdit += (_, e) =>
            {
                DetachAmountEditor();
                // 打字時已寫回副本；按 Esc 取消編輯時儲存格恢復原值，副本也要跟著恢復
                if (e.ColumnIndex == AmountCol) OnCellChanged(e.RowIndex, AmountCol);
            };
            grid.CurrentCellChanged += delegate { UpdateButtons(); };
            grid.DataError += (_, e) => e.ThrowException = false;   // 不存在的值顯示空白，不跳出例外
            grid.MouseDown += OnGridMouseDown;
            grid.MouseMove += OnGridMouseMove;
            grid.DragOver += OnGridDragOver;
            grid.DragLeave += delegate { SetDropInsert(-1); };
            grid.DragDrop += OnGridDragDrop;
            grid.Paint += PaintDropLine;

            var addButton = RowButton("新增");
            removeButton = RowButton("刪除");
            upButton = RowButton("上移");
            downButton = RowButton("下移");
            addButton.Click += delegate { AddCondition(); };
            removeButton.Click += delegate { RemoveCondition(); };
            upButton.Click += delegate { MoveSelected(-1); };
            downButton.Click += delegate { MoveSelected(+1); };
            var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
            buttons.Controls.AddRange(new Control[] { addButton, removeButton, upButton, downButton });

            errors = new Label { AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(3, 6, 3, 0), Visible = false };

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label intro = Hint("處於來源模式時，條件成立就切換到目標模式。同一來源模式的條件由上往下檢查，第一條成立的就執行。");
            intro.Margin = new Padding(3, 0, 3, 0);
            root.Controls.Add(intro, 0, 0);
            root.Controls.Add(grid, 0, 1);
            root.Controls.Add(buttons, 0, 2);
            root.Controls.Add(errors, 0, 3);
            Label usage = Hint(
                "拖拉列首（每列最左邊的格子）或按「上移」「下移」調整順序。\n" +
                "閒置：所選的使用來源全部都沒有使用，持續指定的時間。\n" +
                "偵測到使用：所選的使用來源任一個出現使用就成立。");
            usage.Margin = new Padding(3, 6, 3, 0);
            root.Controls.Add(usage, 0, 4);
            Controls.Add(root);
        }

        public override string HelpText =>
            "【切換條件】\n" +
            "・一條切換條件是「來源模式 + 條件 → 目標模式」：處於來源模式時，條件成立就切換到目標模式。\n" +
            "・用「新增」「刪除」管理切換條件，直接在表格中修改來源模式、條件、使用來源與目標模式。\n" +
            "・只檢查來源模式等於目前模式的切換條件，由上往下第一條成立的就切換。拖拉列首或按「上移」「下移」調整順序。\n" +
            "・自動切換關閉時（右鍵選單「自動切換」）不會依切換條件切換。\n\n" +
            "【條件】\n" +
            "・閒置：所選的使用來源全部都沒有使用，持續指定的分鐘或小時。\n" +
            "・偵測到使用：所選的使用來源任一個出現使用就成立。\n" +
            "・使用來源可複選「鍵盤滑鼠」與「聲音」；聲音要連續播放達「聲音持續秒數」才算使用（通知音這類短暫聲音不算）。門檻在「一般設定」分頁調整。\n\n" +
            "【計時】\n" +
            "・每次切換完成後所有閒置計時歸零，並有 10 秒緩衝期。\n" +
            "・設定視窗開著時暫停自動切換。\n" +
            "・只有目前模式有「偵測到使用＋鍵盤滑鼠」的切換條件時才監聽鍵盤滑鼠輸入，其他模式完全不監聽（閒置計時只看 Windows 記錄的最後輸入時間）。\n\n" +
            "【無法套用】\n" +
            "・來源模式與目標模式相同、沒有選任何使用來源、閒置時間不是正整數。";

        protected override void OnLoad(AppConfig d)
        {
            draft = d;
            Fill(grid.CurrentCell?.RowIndex ?? 0);
        }

        public override void CommitEdits()
        {
            grid.EndEdit();   // 送出還在編輯的儲存格（例如閒置時間剛打的字）
        }

        int SelectedIndex => grid.CurrentCell?.RowIndex ?? -1;

        // ---- 顯示 ----

        /// <summary>依副本重建下拉選單與所有列，並選取第 <paramref name="select"/> 列。</summary>
        void Fill(int select)
        {
            filling = true;
            try
            {
                grid.CancelEdit();
                grid.Rows.Clear();

                // 下拉選單依副本中的模式重建（反映模式分頁的新增、刪除、改名）
                var modes = draft.Modes.Select(m => new Choice(m.Id, DisplayName(m))).ToList();
                foreach (SwitchCondition s in draft.SwitchConditions)
                {
                    foreach (int id in new[] { s.FromModeId, s.ToModeId })
                    {
                        if (modes.All(c => c.Value != id)) modes.Add(new Choice(id, "（不存在的模式 " + id + "）"));
                    }
                }
                foreach (DataGridViewComboBoxColumn c in new[] { fromColumn, toColumn })
                {
                    c.Items.Clear();
                    c.Items.AddRange(modes.Cast<object>().ToArray());
                }

                for (int i = 0; i < draft.SwitchConditions.Count; i++)
                {
                    grid.Rows.Add();
                    ShowRow(i);
                }
                if (grid.Rows.Count > 0)
                {
                    int r = Math.Max(0, Math.Min(select, grid.Rows.Count - 1));
                    grid.CurrentCell = grid.Rows[r].Cells[FromCol];
                }
            }
            finally
            {
                filling = false;
            }
            UpdateErrors();
            UpdateButtons();
        }

        /// <summary>把第 <paramref name="r"/> 條切換條件顯示到表格的第 <paramref name="r"/> 列。</summary>
        void ShowRow(int r)
        {
            SwitchCondition s = draft.SwitchConditions[r];
            DataGridViewRow row = grid.Rows[r];
            bool idle = s.Kind == SwitchConditionKind.Idle;
            bool was = filling;
            filling = true;
            try
            {
                Set(row.Cells[FromCol], s.FromModeId);
                Set(row.Cells[KindCol], (int)s.Kind);
                Set(row.Cells[MouseCol], s.Uses(UsageSources.MouseKeyboard));
                Set(row.Cells[AudioCol], s.Uses(UsageSources.Audio));
                Set(row.Cells[ToCol], s.ToModeId);

                // 偵測到使用沒有閒置時間：欄位清空並鎖住（副本仍保留數值，切回閒置時恢復）
                DataGridViewCell amount = row.Cells[AmountCol];
                string text = amount.Value as string ?? "";
                if (!idle) Set(amount, "");
                else if (text.Length == 0 || SwitchCondition.ParseIdleAmount(text) != s.IdleAmount)
                    Set(amount, s.IdleAmount.ToString());   // 只在數值不同時改寫，剛打的無效文字（例如「abc」）留在格子裡
                var unit = (DataGridViewComboBoxCell)row.Cells[UnitCol];
                Set(unit, idle ? (int)s.IdleUnit : null);
                amount.ReadOnly = unit.ReadOnly = !idle;
                unit.DisplayStyle = idle ? DataGridViewComboBoxDisplayStyle.DropDownButton : DataGridViewComboBoxDisplayStyle.Nothing;
                Color back = idle ? SystemColors.Window : SystemColors.Control;
                amount.Style.BackColor = unit.Style.BackColor = back;
                unit.ToolTipText = amount.ToolTipText = idle ? "" : "偵測到使用不需要閒置時間";

                row.ErrorText = s.Validate(draft) ?? "";
            }
            finally
            {
                filling = was;
            }
        }

        /// <summary>值不同時才改寫儲存格（正在編輯的儲存格值相同，不會被打斷）。</summary>
        static void Set(DataGridViewCell cell, object? value)
        {
            if (!Equals(cell.Value, value)) cell.Value = value;
        }

        /// <summary>表格下方列出所有無效的切換條件與原因（套用時也會擋下）。</summary>
        void UpdateErrors()
        {
            List<string> lines = draft.InvalidSwitchConditions().ToList();
            errors.Text = lines.Count == 0 ? "" : string.Join("\n", lines) + "\n修正後才能套用。";
            errors.Visible = lines.Count > 0;
        }

        void UpdateButtons()
        {
            int i = SelectedIndex;
            removeButton.Enabled = i >= 0;
            upButton.Enabled = i > 0;
            downButton.Enabled = i >= 0 && i < grid.Rows.Count - 1;
        }

        // ---- 編輯 ----

        void OnCellChanged(int r, int col)
        {
            if (filling || r < 0 || r >= draft.SwitchConditions.Count) return;
            object? value = grid.Rows[r].Cells[col].Value;
            SwitchCondition s = draft.SwitchConditions[r];
            switch (col)
            {
                case FromCol when value is int from: s = s with { FromModeId = from }; break;
                case ToCol when value is int to: s = s with { ToModeId = to }; break;
                case KindCol when value is int kind: s = s with { Kind = (SwitchConditionKind)kind }; break;
                case UnitCol when value is int unit: s = s with { IdleUnit = (IdleUnit)unit }; break;
                case AmountCol: SetIdleAmount(r, value as string ?? "", redraw: true); return;
                case MouseCol: s = s with { Sources = Toggle(s.Sources, UsageSources.MouseKeyboard, value is true) }; break;
                case AudioCol: s = s with { Sources = Toggle(s.Sources, UsageSources.Audio, value is true) }; break;
                default: return;
            }
            Replace(r, s);
        }

        /// <summary>
        /// 以新的切換條件替換副本中的第 <paramref name="r"/> 條（record 不可變），並更新錯誤提示。
        /// <paramref name="redraw"/> 為 false 時只更新錯誤圖示，不改寫儲存格（正在編輯的格子不被打斷）。
        /// </summary>
        void Replace(int r, SwitchCondition s, bool redraw = true)
        {
            if (s == draft.SwitchConditions[r]) return;
            draft.SwitchConditions[r] = s;
            MarkEdited();
            if (redraw) ShowRow(r);
            else grid.Rows[r].ErrorText = s.Validate(draft) ?? "";
            UpdateErrors();
        }

        /// <summary>把閒置時間的文字寫回第 <paramref name="r"/> 條（只用於閒置條件）。</summary>
        void SetIdleAmount(int r, string text, bool redraw)
        {
            SwitchCondition s = draft.SwitchConditions[r];
            if (s.Kind == SwitchConditionKind.Idle) Replace(r, s with { IdleAmount = SwitchCondition.ParseIdleAmount(text) }, redraw);
        }

        static UsageSources Toggle(UsageSources sources, UsageSources flag, bool on) => on ? sources | flag : sources & ~flag;

        /// <summary>閒置時間打字時就寫回副本，錯誤提示即時更新（不必先離開儲存格）。</summary>
        void OnEditingControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
        {
            DetachAmountEditor();
            if (grid.CurrentCell?.ColumnIndex == AmountCol && e.Control is TextBox box)
            {
                amountEditor = box;
                box.TextChanged += OnAmountTyped;
            }
        }

        void DetachAmountEditor()
        {
            if (amountEditor != null) amountEditor.TextChanged -= OnAmountTyped;
            amountEditor = null;
        }

        void OnAmountTyped(object? sender, EventArgs e)
        {
            int r = SelectedIndex;
            if (filling || amountEditor == null || r < 0 || r >= draft.SwitchConditions.Count) return;
            SetIdleAmount(r, amountEditor.Text, redraw: false);
        }

        // ---- 新增、刪除、調整順序 ----

        void AddCondition()
        {
            if (draft.SwitchConditionAddBlocker() is string reason)
            {
                MessageBox.Show(FindForm(), "無法新增切換條件：" + reason + "。\n請先在「模式」分頁新增模式。", FindForm()?.Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            grid.EndEdit();
            // 來源模式預設為選取中那一列的來源模式，沒有選取時用目前模式
            int i = SelectedIndex;
            int from = i >= 0 ? draft.SwitchConditions[i].FromModeId : app.Config.CurrentModeId;
            draft.AddSwitchCondition(from);
            MarkEdited();
            Fill(draft.SwitchConditions.Count - 1);
            grid.Focus();
        }

        void RemoveCondition()
        {
            int i = SelectedIndex;
            if (i < 0) return;
            grid.CancelEdit();
            draft.SwitchConditions.RemoveAt(i);
            MarkEdited();
            Fill(i);
        }

        void MoveSelected(int delta)
        {
            int i = SelectedIndex;
            int to = i + delta;
            if (i < 0 || to < 0 || to >= draft.SwitchConditions.Count) return;
            MoveCondition(i, to);
        }

        void MoveCondition(int from, int to)
        {
            grid.EndEdit();
            int col = grid.CurrentCell?.ColumnIndex ?? FromCol;
            draft.MoveSwitchCondition(from, to);
            MarkEdited();
            Fill(to);
            if (col >= 0) grid.CurrentCell = grid.Rows[to].Cells[col];
        }

        // ---- 拖拉排序（從列首拖拉） ----

        void OnGridMouseDown(object? sender, MouseEventArgs e)
        {
            DataGridView.HitTestInfo hit = grid.HitTest(e.X, e.Y);
            dragIndex = e.Button == MouseButtons.Left && hit.Type == DataGridViewHitTestType.RowHeader ? hit.RowIndex : -1;
            Size size = SystemInformation.DragSize;
            dragBox = new Rectangle(e.X - size.Width / 2, e.Y - size.Height / 2, size.Width, size.Height);
        }

        void OnGridMouseMove(object? sender, MouseEventArgs e)
        {
            if (dragIndex < 0 || (e.Button & MouseButtons.Left) == 0 || dragBox.Contains(e.Location)) return;
            int from = dragIndex;
            dragIndex = -1;
            grid.EndEdit();
            grid.DoDragDrop(new DragRow(from), DragDropEffects.Move);
            SetDropInsert(-1);
        }

        void OnGridDragOver(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetData(typeof(DragRow)) is not DragRow)
            {
                e.Effect = DragDropEffects.None;
                return;
            }
            e.Effect = DragDropEffects.Move;
            SetDropInsert(InsertIndexAt(grid.PointToClient(new Point(e.X, e.Y))));
        }

        void OnGridDragDrop(object? sender, DragEventArgs e)
        {
            SetDropInsert(-1);
            if (e.Data?.GetData(typeof(DragRow)) is not DragRow drag) return;
            int insert = InsertIndexAt(grid.PointToClient(new Point(e.X, e.Y)));
            int to = insert > drag.Index ? insert - 1 : insert;   // 先移除原本的列，後面的索引往前一格
            if (to != drag.Index) MoveCondition(drag.Index, to);
        }

        /// <summary>滑鼠位置對應的插入位置（0～列數）：在某列的上半部插在它前面，下半部插在它後面。</summary>
        int InsertIndexAt(Point p)
        {
            if (grid.Rows.Count == 0) return 0;
            DataGridView.HitTestInfo hit = grid.HitTest(p.X, p.Y);
            if (hit.RowIndex >= 0)
            {
                Rectangle bounds = grid.GetRowDisplayRectangle(hit.RowIndex, false);
                return hit.RowIndex + (p.Y > bounds.Top + bounds.Height / 2 ? 1 : 0);
            }
            return p.Y < grid.ColumnHeadersHeight ? 0 : grid.Rows.Count;
        }

        void SetDropInsert(int insert)
        {
            if (dropInsert == insert) return;
            dropInsert = insert;
            grid.Invalidate();
        }

        void PaintDropLine(object? sender, PaintEventArgs e)
        {
            if (dropInsert < 0 || grid.Rows.Count == 0) return;
            int y = dropInsert < grid.Rows.Count
                ? grid.GetRowDisplayRectangle(dropInsert, false).Top
                : grid.GetRowDisplayRectangle(grid.Rows.Count - 1, false).Bottom;
            if (y <= 0 && dropInsert > 0) return;   // 捲出可視範圍
            int thickness = Math.Max(2, Font.Height / 8);
            using var brush = new SolidBrush(SystemColors.Highlight);
            e.Graphics.FillRectangle(brush, 0, Math.Max(grid.ColumnHeadersHeight, y) - thickness / 2, grid.ClientSize.Width, thickness);
        }

        /// <summary>拖拉中的列（以專用型別包裝，避免接受從別處拖進來的資料）。</summary>
        sealed class DragRow
        {
            public DragRow(int index) => Index = index;
            public int Index { get; }
        }

        // ---- 版面 ----

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            // 表格的列高與列首寬度不會隨 DPI 縮放，依字型高度設定
            int height = Font.Height + 10;
            grid.RowTemplate.Height = height;
            foreach (DataGridViewRow row in grid.Rows) row.Height = height;
            grid.RowHeadersWidth = Font.Height * 5 / 2;   // 容納目前列的箭頭與錯誤圖示
        }

        /// <summary>下拉選單的一個選項：值與顯示的文字。</summary>
        sealed class Choice
        {
            public Choice(int value, string text)
            {
                Value = value;
                Text = text;
            }

            public int Value { get; }
            public string Text { get; }
            public override string ToString() => Text;
        }

        /// <summary>下拉選單欄：選項為 <see cref="Choice"/>，儲存格的值是選項的 <see cref="Choice.Value"/>。</summary>
        static DataGridViewComboBoxColumn ComboColumn(string header, float weight) => new()
        {
            HeaderText = header,
            FillWeight = weight,
            ValueMember = nameof(Choice.Value),
            DisplayMember = nameof(Choice.Text),
            ValueType = typeof(int),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FlatStyle = FlatStyle.Flat,
        };

        /// <summary>選項固定的下拉選單欄（條件類型、單位）。</summary>
        static DataGridViewComboBoxColumn ChoiceColumn(string header, float weight, params Choice[] choices)
        {
            DataGridViewComboBoxColumn c = ComboColumn(header, weight);
            c.Items.AddRange(choices.Cast<object>().ToArray());
            return c;
        }

        static Button RowButton(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(0, 0, 6, 0),
        };
    }
}
