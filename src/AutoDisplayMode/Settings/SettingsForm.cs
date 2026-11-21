using System;
using System.Drawing;
using System.Windows.Forms;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Settings
{
    /// <summary>
    /// 設定視窗：各分頁修改暫存在視窗內的設定副本，按「套用」或「確定」才交給協調者寫入 cfg，按「取消」則放棄。
    /// 開啟與關閉時通知協調者（暫停／恢復自動切換）由 <see cref="TrayApp"/> 負責。
    /// </summary>
    sealed class SettingsForm : Form
    {
        readonly AppCoordinator app;
        readonly SettingsPage[] pages;
        readonly Button applyButton;
        readonly AppConfig draft;

        public SettingsForm(AppCoordinator app, Icon icon, string appTitle)
        {
            this.app = app;
            draft = app.Config.Clone();

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = appTitle + " - 設定";
            Icon = icon;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            ClientSize = new Size(600, 500);
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

            // 票 06～08 在這裡加入其他分頁
            pages = new SettingsPage[] { new GeneralPage() };

            var tabs = new TabControl { Dock = DockStyle.Fill };
            foreach (SettingsPage page in pages)
            {
                page.Load(draft);
                page.Edited += delegate { applyButton.Enabled = true; };
                tabs.TabPages.Add(page);
            }

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Padding = new Padding(6),
            };
            buttons.Controls.AddRange(new Control[] { applyButton, cancel, ok });   // 由右到左：套用、取消、確定

            Controls.Add(tabs);
            Controls.Add(buttons);

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

            string? error = app.ApplySettings(draft);
            if (error != null)
            {
                MessageBox.Show(this, "無法套用設定：\n" + error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            applyButton.Enabled = false;
            return true;
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
