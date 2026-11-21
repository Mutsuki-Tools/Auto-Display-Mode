using System;
using System.Drawing;
using System.Windows.Forms;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Settings
{
    /// <summary>
    /// 設定視窗：各分頁修改暫存在視窗內的設定副本，按「套用」或「套用並關閉」才交給協調者寫入 cfg，按「取消」則放棄。
    /// 左下角「說明」（或 F1）顯示目前分頁的說明（<see cref="SettingsPage.HelpText"/>），右邊在有未套用的變更時顯示提醒；
    /// 有未套用的變更時，按「取消」或 ✕ 先確認是否放棄。
    /// 開啟與關閉時通知協調者（暫停／恢復沒勾強制的切換條件）由 <see cref="TrayApp"/> 負責。
    /// </summary>
    sealed class SettingsForm : Form
    {
        readonly AppCoordinator app;
        readonly SettingsPage[] pages;
        readonly ModesPage modesPage;   // 套用時提供在視窗中擷取的模式
        readonly Button applyButton;
        readonly Label pendingLabel;    // 「有尚未套用的變更」
        readonly AppConfig draft;
        readonly TabControl tabs;
        readonly string appTitle;
        bool showingHelp;
        bool discardOnClose;   // 關閉時不確認（程式結束時）

        public SettingsForm(AppCoordinator app, IVirtualDisplayDriverInstaller driver, Icon icon, string appTitle)
        {
            this.app = app;
            this.appTitle = appTitle;
            draft = app.Config.Clone();

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = appTitle + " - 設定";
            Icon = icon;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            ClientSize = new Size(640, 580);   // 模式分頁不需捲動
            MinimumSize = new Size(480, 360);

            var ok = DialogButton("套用並關閉");
            var cancel = DialogButton("取消");
            applyButton = DialogButton("套用");
            applyButton.Enabled = false;
            ok.Click += delegate { if (Apply()) Close(); };
            cancel.Click += delegate { Close(); };
            applyButton.Click += delegate { Apply(); };
            AcceptButton = ok;
            CancelButton = cancel;

            modesPage = new ModesPage(app);
            pages = new SettingsPage[] { modesPage, new LayoutPage(app), new SwitchConditionsPage(app), new GeneralPage(driver) };

            tabs = new TabControl { Dock = DockStyle.Fill };
            foreach (SettingsPage page in pages)
            {
                page.Load(draft);
                page.Edited += delegate { SetPending(true); };
                tabs.TabPages.Add(page);
            }
            // 離開分頁前送出還沒確認的輸入；切換分頁時重新顯示副本，反映其他分頁的修改（例如模式改名後，下拉選單顯示新名稱）
            tabs.Deselecting += (_, e) => (e.TabPage as SettingsPage)?.CommitEdits();
            tabs.Selected += (_, e) => (e.TabPage as SettingsPage)?.Load(draft);

            var buttons = new FlowLayoutPanel
            {
                Anchor = AnchorStyles.Right,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,   // 在表格的自動寬度欄中不換行
                Margin = new Padding(0),
            };
            buttons.Controls.AddRange(new Control[] { ok, applyButton, cancel });   // 由右到左加入，畫面上由左到右是：取消、套用、套用並關閉
            // Tab 鍵也由左到右
            cancel.TabIndex = 0;
            applyButton.TabIndex = 1;
            ok.TabIndex = 2;

            // 左下角「說明」：顯示目前分頁的說明（F1 也可以，見 OnHelpRequested）
            var help = DialogButton("說明");
            help.Margin = new Padding(0, 3, 6, 3);
            help.Anchor = AnchorStyles.Left;
            help.Click += delegate { ShowHelp(); };

            // 「說明」右邊：有未套用的變更時顯示，套用成功後消失
            pendingLabel = new Label
            {
                Text = "有尚未套用的變更",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                ForeColor = Color.Firebrick,
                Margin = new Padding(6, 0, 6, 0),
                Visible = false,
            };

            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(6) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(help, 0, 0);
            bottom.Controls.Add(pendingLabel, 1, 0);
            bottom.Controls.Add(buttons, 2, 0);

            Controls.Add(tabs);
            Controls.Add(bottom);

            Font = SystemFonts.MessageBoxFont;   // 最後才設定，讓分頁收到字型改變
            ResumeLayout(false);
            PerformLayout();
        }

        /// <summary>
        /// 把副本交給協調者寫入 cfg 並立即生效。沒有修改時直接視為成功；失敗時顯示原因並保留修改。
        /// </summary>
        bool Apply()
        {
            foreach (SettingsPage page in pages) page.CommitEdits();
            if (!applyButton.Enabled) return true;

            string? error = app.ApplySettings(draft, modesPage.CapturedModeId);
            if (error != null)
            {
                MessageBox.Show(this, "無法套用設定：\n" + error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            SetPending(false);
            foreach (SettingsPage page in pages) page.OnApplied();
            // 套用後模式依順序重新編號（副本也一樣）：重新顯示目前的分頁，其他分頁切換過去時會重新顯示
            (tabs.SelectedTab as SettingsPage)?.Load(draft);
            return true;
        }

        /// <summary>有沒有未套用的變更：「套用」按鈕與「有尚未套用的變更」提醒一起切換。</summary>
        void SetPending(bool pending)
        {
            applyButton.Enabled = pending;
            pendingLabel.Visible = pending;
        }

        /// <summary>程式結束時關閉：放棄未套用的變更，不確認。</summary>
        public void CloseDiscardingChanges()
        {
            discardOnClose = true;
            Close();
        }

        /// <summary>
        /// 使用者按「取消」、✕ 或 Esc 關閉時，有未套用的變更就先確認；選「否」留在設定視窗。
        /// 「套用並關閉」會先套用（成功後沒有未套用的變更），Windows 關機與程式結束時不確認。
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !discardOnClose && applyButton.Enabled)
            {
                DialogResult r = MessageBox.Show(this, "要放棄尚未套用的變更嗎？", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) e.Cancel = true;
            }
            base.OnFormClosing(e);
        }

        /// <summary>顯示目前分頁的說明（「說明」按鈕與 F1）。</summary>
        void ShowHelp()
        {
            if (showingHelp || tabs.SelectedTab is not SettingsPage page) return;   // 說明開著時再按 F1 不疊出第二個
            showingHelp = true;
            try
            {
                MessageBox.Show(this, page.HelpText, appTitle + " - 說明：" + page.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                showingHelp = false;
            }
        }

        /// <summary>F1：顯示目前分頁的說明（焦點在任何控制項上都會傳到這裡）。</summary>
        protected override void OnHelpRequested(HelpEventArgs hevent)
        {
            hevent.Handled = true;   // 已處理，Windows 不再做預設的說明處理
            ShowHelp();
        }

        /// <summary>帶到前景：最小化時先還原。</summary>
        public void BringToFrontAndRestore()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
        }

        static Button DialogButton(string text) => new()
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(80, 0),
            Margin = new Padding(6, 3, 0, 3),
        };
    }
}
