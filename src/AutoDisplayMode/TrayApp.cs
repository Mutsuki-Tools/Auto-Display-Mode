using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
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

        void ShowHelp()
        {
            AppConfig cfg = app.Config;
            string name = app.CurrentModeName;
            string conditions = cfg.SwitchConditions.Count == 0
                ? "・（尚未設定任何切換條件）\n"
                : string.Concat(cfg.SwitchConditions.Select((s, i) => "・" + (i + 1) + ". " + s.Describe(cfg) + "\n"));
            string help =
                "【模式】目前：" + name + "\n" +
                "・右鍵選單列出所有模式，目前模式打勾；點一下即手動切換，依該模式記錄的螢幕設定套用。\n" +
                "・模式中記錄但沒接上的螢幕會略過；接上但沒記錄的螢幕依模式的設定維持現狀或關閉。\n" +
                "・套用後沒有任何螢幕會開著時拒絕切換；套用失敗時維持原本的模式，自動切換暫停 1 分鐘。\n" +
                "・手動切換進入模式時，自動切換依模式的設定維持、開啟或關閉。\n" +
                "・在設定視窗的「模式」分頁新增、刪除、重新命名、複製模式，並設定圖示顏色、手動切換進入時的自動切換與未記錄的螢幕。\n" +
                "・拖拉清單中的模式或按「上移」「下移」調整順序（右鍵選單依這個順序列出）；套用後 cfg 的模式編號依新順序重新編排，切換條件跟著改。\n" +
                "・刪除模式時，來源模式或目標模式是它的切換條件會一併刪除；最後一個模式與目前模式不能刪除。\n\n" +
                "【螢幕配置】\n" +
                "・在設定視窗的「螢幕配置」分頁選擇模式，畫布顯示目前接上的螢幕（此模式中關閉的以灰色排在下方），插拔螢幕時即時更新。\n" +
                "・拖拉螢幕擺放位置，靠近其他螢幕的邊會自動貼齊（按住 Alt 不貼齊）；也可輸入座標，以主螢幕左上角為 (0,0)。\n" +
                "・選取螢幕後設定開關、主螢幕、方向，以及該螢幕支援的解析度與更新率。\n" +
                "・螢幕重疊時無法套用；螢幕之間有空隙時顯示警告（游標無法跨越空隙），仍可套用。\n" +
                "・此模式沒有記錄的接上螢幕在畫布上以橘色虛線框標示「此模式未設定」，套用時依模式的設定維持現狀或關閉；修改它的設定就會加入此模式。\n" +
                "・此模式記錄、但現在沒接上的螢幕列在畫布下方的「未接上的螢幕」清單中，可以刪除，或「對應到…」一台接上但此模式未設定的螢幕（換上同型號新螢幕時沿用舊設定）。\n" +
                "・「預覽套用」立即套用編輯中的螢幕設定，15 秒內沒有按「保留變更」就還原；預覽不改變目前模式，保留後仍要按「套用」或「確定」才會存檔。\n\n" +
                "【自動切換】目前：" + OnOff(cfg.AutoSwitch) + "（可從右鍵選單開關）\n" +
                "・只檢查來源模式等於目前模式的切換條件，由上往下第一條成立的就切換。\n" +
                "・閒置：所選的使用來源全部都沒有使用，持續指定的分鐘或小時。\n" +
                "・偵測到使用：所選的使用來源任一個出現使用就成立。\n" +
                "・使用來源可複選「鍵盤滑鼠」與「聲音」；聲音要連續播放達聲音持續秒數才算使用（通知音這類短暫聲音不算）。\n" +
                "・只有目前模式有「偵測到使用＋鍵盤滑鼠」的切換條件時才掛滑鼠與鍵盤鉤子，其他模式完全不掛。\n" +
                "・每次切換完成後所有閒置計時歸零，並有 10 秒緩衝期。\n" +
                "・設定視窗開著時暫停自動切換。\n" +
                "・在設定視窗的「切換條件」分頁新增、編輯、刪除切換條件，拖拉列首或按「上移」「下移」調整順序。\n" +
                "・來源模式與目標模式不能相同、至少要選一個使用來源、閒置時間必須是正整數，否則無法套用。\n" +
                "目前的切換條件：\n" +
                conditions + "\n" +
                "【一般設定】雙擊工作列圖示或從右鍵選單選「設定…」修改\n" +
                "・滑鼠移動門檻：" + cfg.MouseMoveThreshold + " 像素\n" +
                "・聲音音量門檻：" + cfg.AudioThreshold.ToString(CultureInfo.InvariantCulture) + "\n" +
                "・聲音持續秒數：" + cfg.AudioSeconds + " 秒\n" +
                "・偵測到使用時忽略遠端連線軟體送出的鍵盤滑鼠輸入：" + OnOff(cfg.IgnoreRemoteInput) + "\n" +
                "・自動切換時跳出通知：" + OnOff(cfg.ShowNotifications) + "\n" +
                "・開機登入時自動開啟：" + OnOff(cfg.StartWithWindows) + "\n\n" +
                "【擷取】\n" +
                "・右鍵選單「將目前螢幕狀態存入「" + name + "」…」會把目前每台螢幕的狀態存進目前模式（設定視窗開著時無法使用）。\n" +
                "・在「模式」分頁可「以目前螢幕狀態覆蓋此模式」或「以目前螢幕狀態新增模式」，按「套用」或「確定」後才存檔。\n" +
                "・存入的模式隨即成為目前模式，並重新開始計時。\n\n" +
                "【圖示】\n" +
                "・顏色：目前模式的顏色\n" +
                "・實心：自動切換開啟　空心：自動切換關閉\n\n" +
                "【cfg】\n" +
                "・位置：" + app.ConfigLocation + "\n" +
                "・從右鍵選單「用記事本開啟 cfg」修改並存檔後，選「重新載入 cfg」即生效（設定視窗開著時無法重新載入）。";

            MessageBox.Show(help, AppTitle + " - 說明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void ExitApp()
        {
            settings?.Close();   // 放棄設定視窗中未套用的修改
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
