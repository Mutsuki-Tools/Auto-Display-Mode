using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using AutoDisplayMode.Core;
using AutoDisplayMode.Settings;
using AutoDisplayMode.Windows;

namespace AutoDisplayMode
{
    /// <summary>介面層：工作列圖示與右鍵選單。邏輯都在 <see cref="AppCoordinator"/>。</summary>
    sealed class TrayApp : ApplicationContext
    {
        const string AppTitle = "Auto Display Mode";

        readonly AppCoordinator app;
        readonly WindowsUsageSignals signals;
        readonly WindowsMonitorSystem monitors;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly ToolStripMenuItem miStatus;
        readonly ToolStripSeparator modesEnd;
        readonly List<ToolStripMenuItem> modeItems = new();
        readonly ToolStripMenuItem miAuto;
        readonly ToolStripMenuItem miCapture;
        readonly ToolStripMenuItem miReload;
        readonly Dictionary<(int Color, bool Filled), Icon> icons = new();
        readonly System.Windows.Forms.Timer timer;
        readonly int swatchSize;   // 選單中顏色方塊的邊長（依 DPI 放大）
        SettingsForm? settings;    // 只允許開一個設定視窗；沒開時為 null

        public TrayApp()
        {
            if (SynchronizationContext.Current == null)
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            SynchronizationContext ui = SynchronizationContext.Current!;

            tray = new NotifyIcon();

            // ---- 核心與 Windows 轉接層 ----
            var clock = new StopwatchClock();
            signals = new WindowsUsageSignals(clock);
            monitors = new WindowsMonitorSystem(ui);
            string dir = Path.GetDirectoryName(Application.ExecutablePath)!;
            app = new AppCoordinator(
                new FileConfigStore(Path.Combine(dir, "AutoDisplayMode.cfg")),
                monitors, signals, clock, new RegistryStartup(), new BalloonNotifier(tray, AppTitle));
            monitors.ApplyFinished += app.OnSwitchFinished;

            // ---- 右鍵選單 ----
            menu = new ContextMenuStrip();
            swatchSize = ScaleForDpi(16);
            menu.ImageScalingSize = new Size(swatchSize, swatchSize);

            miStatus = new ToolStripMenuItem("") { Enabled = false };
            modesEnd = new ToolStripSeparator();   // 模式清單放在 miStatus 後的分隔線與這條分隔線之間

            miAuto = new ToolStripMenuItem("自動切換", null, delegate { app.SetAutoSwitch(!app.Config.AutoSwitch); });

            var miSettings = new ToolStripMenuItem("設定…", null, delegate { ShowSettings(); });
            miSettings.Font = new Font(miSettings.Font, FontStyle.Bold);   // 雙擊圖示的預設動作
            miCapture = new ToolStripMenuItem("", null, delegate { ConfirmCapture(); });

            var miOpenCfg = new ToolStripMenuItem("用記事本開啟 cfg", null, delegate { OpenConfig(); });
            miReload = new ToolStripMenuItem("重新載入 cfg", null, delegate { app.ReloadConfig(); });
            var miHelp = new ToolStripMenuItem("說明", null, delegate { ShowHelp(); });
            var miExit = new ToolStripMenuItem("關閉程式", null, delegate { ExitApp(); });

            menu.Items.AddRange(new ToolStripItem[]
            {
                miStatus,
                new ToolStripSeparator(),
                modesEnd,
                miAuto,
                new ToolStripSeparator(),
                miSettings,
                miCapture,
                new ToolStripSeparator(),
                miOpenCfg,
                miReload,
                miHelp,
                new ToolStripSeparator(),
                miExit,
            });
            menu.Opening += delegate { UpdateUi(); };

            tray.ContextMenuStrip = menu;
            tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
            tray.Visible = true;
            app.Changed += UpdateUi;

            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += delegate { app.Tick(); };

            UpdateUi();

            // 等訊息迴圈開始後再啟動（啟動通知需要訊息迴圈）
            ui.Post(_ =>
            {
                app.Start();
                timer.Start();
            }, null);
        }

        // ------------------------------------------------------------
        //  設定視窗
        // ------------------------------------------------------------

        /// <summary>開啟設定視窗；已經開著時還原（如果最小化）並帶到前景，不開第二個。</summary>
        void ShowSettings()
        {
            if (settings != null)
            {
                settings.BringToFrontAndRestore();
                return;
            }

            var form = new SettingsForm(app, tray.Icon, AppTitle);
            form.FormClosed += delegate
            {
                form.Dispose();
                settings = null;
                app.OnSettingsWindowClosed();
            };
            settings = form;
            app.OnSettingsWindowOpened();
            form.BringToFrontAndRestore();
        }

        // ------------------------------------------------------------
        //  設定檔
        // ------------------------------------------------------------
        void OpenConfig()
        {
            app.EnsureConfigFileExists();
            try
            {
                Process.Start("notepad.exe", "\"" + app.ConfigLocation + "\"");
            }
            catch (Exception ex)
            {
                ShowBalloon("無法開啟設定檔：" + ex.Message, ToolTipIcon.Error);
            }
        }

        // ------------------------------------------------------------
        //  擷取
        // ------------------------------------------------------------
        void ConfirmCapture()
        {
            string name = app.CurrentModeName;
            DialogResult r = MessageBox.Show(
                "要把目前的螢幕狀態（每台螢幕的開關、座標、解析度、更新率、方向與主螢幕）存入「" + name + "」嗎？\n\n" +
                "「" + name + "」原本記錄的螢幕設定會被覆蓋；目前沒接上的螢幕維持原本的設定。",
                AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (r == DialogResult.Yes) app.CaptureToCurrentMode();
        }

        // ------------------------------------------------------------
        //  介面
        // ------------------------------------------------------------
        void UpdateUi()
        {
            AppConfig cfg = app.Config;
            string modeName = app.CurrentModeName;
            string? target = app.SwitchTargetName;

            miStatus.Text = "目前：" + modeName + (target != null ? "（正在切換到「" + target + "」…）" : "");
            UpdateModeItems();
            miAuto.Checked = cfg.AutoSwitch;
            miCapture.Text = "將目前螢幕狀態存入「" + modeName.Replace("&", "&&") + "」…";
            miCapture.Enabled = !app.IsSwitching && !app.SettingsWindowOpen;   // 設定視窗開著時，擷取會被視窗的副本蓋掉
            miReload.Enabled = !app.SettingsWindowOpen;                        // 避免與設定視窗互相覆蓋

            tray.Icon = IconFor(cfg.CurrentModeConfig?.Color ?? ModeConfig.Palette[0], cfg.AutoSwitch);

            string tip = AppTitle + "\n" + modeName + (target != null ? "（切換中）" : "") + "｜自動切換：" + OnOff(cfg.AutoSwitch);
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        /// <summary>重建模式清單：目前模式打勾，切換中的目標模式加上提示。</summary>
        void UpdateModeItems()
        {
            foreach (ToolStripMenuItem item in modeItems)
            {
                menu.Items.Remove(item);
                item.Image?.Dispose();
                item.Dispose();
            }
            modeItems.Clear();

            AppConfig cfg = app.Config;
            int index = menu.Items.IndexOf(modesEnd);
            foreach (ModeConfig mode in cfg.Modes)
            {
                int id = mode.Id;
                string text = mode.Name.Replace("&", "&&");
                if (id == app.SwitchTargetId) text += "（切換中…）";
                var item = new ToolStripMenuItem(text, ColorSwatch(mode.Color, swatchSize), delegate { app.ManualSwitch(id); })
                {
                    Checked = id == cfg.CurrentModeId,
                };
                modeItems.Add(item);
                menu.Items.Insert(index++, item);
            }
        }

        void ShowBalloon(string text, ToolTipIcon icon) => tray.ShowBalloonTip(4000, AppTitle, text, icon);

        static string OnOff(bool b) => b ? "開" : "關";

        /// <summary>右鍵選單的「說明」：總說明與右鍵選單的用法。各分頁的操作說明在設定視窗的「說明」（F1）。</summary>
        void ShowHelp()
        {
            AppConfig cfg = app.Config;
            string name = app.CurrentModeName;
            string help =
                "依你是否正在使用電腦，在多組螢幕配置之間自動或手動切換。\n" +
                "・模式：一組有名稱的螢幕設定（每台螢幕的開關、位置、解析度等），例如「遊玩」「省電」。\n" +
                "・切換條件：處於某個模式時，閒置一段時間或偵測到使用，就切換到另一個模式。勾選「強制」的切換條件即使自動切換關閉、設定視窗開著，也照樣生效。\n" +
                "・目前模式：" + name + "　自動切換：" + OnOff(cfg.AutoSwitch) + "\n\n" +
                "【右鍵選單】\n" +
                "・最上方的「目前：…」顯示目前模式（切換中時顯示正在切換到哪個模式）。\n" +
                "・模式清單：目前模式打勾，點一下即手動切換到該模式。\n" +
                "・自動切換：開關依切換條件自動切換。關閉時只有勾選「強制」的切換條件照樣生效。\n" +
                "・設定…：開啟設定視窗（雙擊工作列圖示也可以），管理模式、螢幕配置、切換條件與一般設定。各分頁的用法按設定視窗左下角的「說明」或 F1。\n" +
                "・將目前螢幕狀態存入「" + name + "」…：把目前每台螢幕的狀態存進目前模式，並重新開始計時（設定視窗開著時無法使用）。\n" +
                "・用記事本開啟 cfg：直接編輯設定檔；存檔後選「重新載入 cfg」即生效（設定視窗開著時無法重新載入）。\n" +
                "・說明：顯示這段說明。\n" +
                "・關閉程式：結束常駐。\n\n" +
                "【圖示】\n" +
                "・顏色：目前模式的顏色\n" +
                "・實心：自動切換開啟　空心：自動切換關閉\n\n" +
                "【cfg】\n" +
                "・位置：" + app.ConfigLocation;

            MessageBox.Show(help, AppTitle + " - 說明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void ExitApp()
        {
            settings?.CloseDiscardingChanges();   // 放棄設定視窗中未套用的修改，不確認
            timer.Stop();
            signals.Dispose();
            monitors.Dispose();
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }

        static Color ToColor(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

        Icon IconFor(int rgb, bool filled)
        {
            if (!icons.TryGetValue((rgb, filled), out Icon? icon))
            {
                icon = MakeIcon(ToColor(rgb), filled);
                icons[(rgb, filled)] = icon;
            }
            return icon;
        }

        // 畫一個小螢幕圖示：實心 = 自動切換開，空心 = 自動切換關
        static Icon MakeIcon(Color color, bool filled)
        {
            using var bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                var screen = new Rectangle(3, 4, 26, 18);
                if (filled)
                {
                    using var b = new SolidBrush(color);
                    g.FillRectangle(b, screen);
                }
                using var p = new Pen(color, 3f);
                g.DrawRectangle(p, screen);
                g.DrawLine(p, 16, 23, 16, 27);
                g.DrawLine(p, 9, 28, 23, 28);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        /// <summary>選單中模式名稱前的顏色方塊（邊長 <paramref name="size"/>，四周留 1/8 的空白）。</summary>
        static Image ColorSwatch(int rgb, int size)
        {
            var bmp = new Bitmap(size, size);
            using Graphics g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            using var b = new SolidBrush(ToColor(rgb));
            int margin = size / 8;
            g.FillRectangle(b, margin, margin, size - 2 * margin, size - 2 * margin);
            return bmp;
        }

        /// <summary>把 96 DPI 時的像素數依系統 DPI 放大。</summary>
        static int ScaleForDpi(int pixels)
        {
            using Graphics g = Graphics.FromHwnd(IntPtr.Zero);
            return (int)Math.Round(pixels * g.DpiX / 96f);
        }
    }
}
