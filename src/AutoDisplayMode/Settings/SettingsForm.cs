using System;
using System.Drawing;
using System.Windows.Forms;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Settings
{
    /// <summary>
    /// 設定視窗：各分頁修改暫存在視窗內的設定副本，按「套用」或「確定」才交給協調者寫入 cfg，按「取消」則放棄。
    /// 左下角「說明」（或 F1）顯示目前分頁的說明（<see cref="SettingsPage.HelpText"/>）。
    /// 開啟與關閉時通知協調者（暫停／恢復自動切換）由 <see cref="TrayApp"/> 負責。
    /// </summary>
    sealed class SettingsForm : Form
    {
        readonly AppCoordinator app;
        readonly SettingsPage[] pages;
        readonly ModesPage modesPage;   // 套用時提供在視窗中擷取的模式
        readonly Button applyButton;
        readonly AppConfig draft;
        readonly TabControl tabs;
        readonly string appTitle;
        bool showingHelp;

        public SettingsForm(AppCoordinator app, Icon icon, string appTitle)
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

            var ok = DialogButton("確定");
            var cancel = DialogButton("取消");
            applyButton = DialogButton("套用");
            applyButton.Enabled = false;
            ok.Click += delegate { if (Apply()) Close(); };
            cancel.Click += delegate { Close(); };
            applyButton.Click += delegate { Apply(); };
            AcceptButton = ok;
            CancelButton = cancel;

            modesPage = new ModesPage(app);
            pages = new SettingsPage[] { modesPage, new LayoutPage(app), new SwitchConditionsPage(app), new GeneralPage() };

            tabs = new TabControl { Dock = DockStyle.Fill };
            foreach (SettingsPage page in pages)
            {
                page.Load(draft);
                page.Edited += delegate { applyButton.Enabled = true; };
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
            buttons.Controls.AddRange(new Control[] { applyButton, cancel, ok });   // 由右到左：套用、取消、確定

            // 左下角「說明」：顯示目前分頁的說明（F1 也可以，見 OnHelpRequested）
            var help = DialogButton("說明");
            help.Margin = new Padding(0, 3, 6, 3);
            help.Anchor = AnchorStyles.Left;
            help.Click += delegate { ShowHelp(); };

            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, Padding = new Padding(6) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.Controls.Add(help, 0, 0);
            bottom.Controls.Add(buttons, 1, 0);

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
            applyButton.Enabled = false;
            foreach (SettingsPage page in pages) page.OnApplied();
            // 套用後模式依順序重新編號（副本也一樣）：重新顯示目前的分頁，其他分頁切換過去時會重新顯示
            (tabs.SelectedTab as SettingsPage)?.Load(draft);
            return true;
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
