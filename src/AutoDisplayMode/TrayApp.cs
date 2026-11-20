using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using AutoDisplayMode.Core;
using AutoDisplayMode.Windows;

namespace AutoDisplayMode
{
    /// <summary>介面層：工作列圖示與右鍵選單。邏輯都在 <see cref="AppCoordinator"/>。</summary>
    sealed class TrayApp : ApplicationContext
    {
        const string AppTitle = "省電螢幕工具";
        static readonly int[] IdleChoices = { 5, 10, 30, 60 };

        readonly AppCoordinator app;
        readonly WindowsUsageSignals signals;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly ToolStripMenuItem miStatus;
        readonly ToolStripSeparator modesEnd;
        readonly List<ToolStripMenuItem> modeItems = new();
        readonly ToolStripMenuItem miAuto;
        readonly ToolStripMenuItem miIdle;
        readonly ToolStripMenuItem miAudio;
        readonly ToolStripMenuItem miStartup;
        readonly ToolStripMenuItem miCapture;
        readonly List<ToolStripMenuItem> idleItems = new();
        readonly Dictionary<(int Color, bool Filled), Icon> icons = new();
        readonly System.Windows.Forms.Timer timer;

        public TrayApp()
        {
            if (SynchronizationContext.Current == null)
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            SynchronizationContext ui = SynchronizationContext.Current!;

            tray = new NotifyIcon();

            // ---- 核心與 Windows 轉接層 ----
            var clock = new StopwatchClock();
            signals = new WindowsUsageSignals(clock);
            var monitors = new WindowsMonitorSystem(ui);
            string dir = Path.GetDirectoryName(Application.ExecutablePath)!;
            app = new AppCoordinator(
                new FileConfigStore(Path.Combine(dir, "AutoDisplayMode.cfg")),
                monitors, signals, clock, new RegistryStartup(), new BalloonNotifier(tray, AppTitle));
            monitors.ApplyFinished += app.OnSwitchFinished;

            // ---- 右鍵選單 ----
            menu = new ContextMenuStrip();

            miStatus = new ToolStripMenuItem("") { Enabled = false };
            modesEnd = new ToolStripSeparator();   // 模式清單放在 miStatus 後的分隔線與這條分隔線之間

            miAuto = new ToolStripMenuItem("", null, delegate { app.SetAutoSwitch(!app.Config.AutoSwitch); });

            miIdle = new ToolStripMenuItem("");
            foreach (int minutes in IdleChoices)
            {
                var item = new ToolStripMenuItem(minutes + " 分鐘", null, delegate { app.SetIdleMinutes(minutes); })
                {
                    Tag = minutes,
                };
                idleItems.Add(item);
                miIdle.DropDownItems.Add(item);
            }

            miAudio = new ToolStripMenuItem("", null, delegate { app.SetAudioDetect(!app.Config.AudioDetect); });

            miCapture = new ToolStripMenuItem("", null, delegate { ConfirmCapture(); });

            var miHelp = new ToolStripMenuItem("說明", null, delegate { ShowHelp(); });
            var miOpenCfg = new ToolStripMenuItem("以文字文件開啟cfg檔", null, delegate { OpenConfig(); });
            var miReload = new ToolStripMenuItem("重新載入cfg檔", null, delegate { app.ReloadConfig(); });
            miStartup = new ToolStripMenuItem("", null, delegate { app.ToggleStartup(); });
            var miExit = new ToolStripMenuItem("關閉程式", null, delegate { ExitApp(); });

            menu.Items.AddRange(new ToolStripItem[]
            {
                miStatus,
                new ToolStripSeparator(),
                modesEnd,
                miAuto,
                miIdle,
                miAudio,
                new ToolStripSeparator(),
                miCapture,
                new ToolStripSeparator(),
                miHelp,
                miOpenCfg,
                miReload,
                miStartup,
                new ToolStripSeparator(),
                miExit,
            });
            menu.Opening += delegate { UpdateUi(); };

            tray.ContextMenuStrip = menu;
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
            miAuto.Text = "自動切換 [目前：" + OnOff(cfg.AutoSwitch) + "]";
            miIdle.Text = LegacySwitchText("閒置切換的時間") + " [目前：" + cfg.IdleMinutes + " 分]";
            foreach (ToolStripMenuItem item in idleItems)
                item.Checked = (int)item.Tag == cfg.IdleMinutes;
            miAudio.Text = "自動切換偵測聲音 [目前：" + OnOff(cfg.AudioDetect) + "]";
            miStartup.Text = "開機時自動開啟此程式 [目前：" + OnOff(app.StartupEnabled) + "]";
            miCapture.Text = "將目前螢幕狀態存入「" + modeName + "」…";
            miCapture.Enabled = !app.IsSwitching;

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
                var item = new ToolStripMenuItem(text, ColorSwatch(mode.Color), delegate { app.ManualSwitch(id); })
                {
                    Checked = id == cfg.CurrentModeId,
                };
                modeItems.Add(item);
                menu.Items.Insert(index++, item);
            }
        }

        /// <summary>舊版切換條件的選單文字：作用中時帶出模式名稱。</summary>
        string LegacySwitchText(string what)
        {
            AppConfig cfg = app.Config;
            if (!app.LegacySwitchActive) return "自動切換" + what;
            return "「" + cfg.FindMode(cfg.HomeModeId)!.Name + "」" + what;
        }

        void ShowBalloon(string text, ToolTipIcon icon) => tray.ShowBalloonTip(4000, AppTitle, text, icon);

        static string OnOff(bool b) => b ? "開" : "關";

        void ShowHelp()
        {
            AppConfig cfg = app.Config;
            string help =
                "【模式】\n" +
                "・右鍵選單列出所有模式，目前模式打勾；點一下即手動切換，依該模式記錄的螢幕設定套用。\n" +
                "・模式中記錄但沒接上的螢幕會略過；接上但沒記錄的螢幕依模式的 UnlistedMonitors 設定維持現狀或關閉。\n" +
                "・套用後沒有任何螢幕會開著時拒絕切換；套用失敗時維持原本的模式，自動切換暫停 1 分鐘。\n" +
                "・手動切換進入模式時，自動切換依模式的 ManualAutoSwitch 設定維持、開啟或關閉。\n" +
                "・模式目前只能在cfg檔中新增與修改。\n\n" +
                "【自動切換】由選單或cfg檔設定[當前為" + OnOff(cfg.AutoSwitch) + "]\n" +
                "・" + app.LegacySwitchSummary + "\n" +
                "・扮演「遊玩」與「省電」的模式由cfg檔的 HomeMode／AwayMode 設定；處於其他模式時不自動切換。\n" +
                "・「省電」模式中，偵測到實體滑鼠移動或點擊，或電腦連續播放聲音，就切換到「遊玩」模式。\n" +
                "・「遊玩」模式中，沒有任何鍵盤滑鼠操作、也沒有持續播放聲音，超過閒置時間後切換到「省電」模式。\n" +
                "・閒置時間由選單或cfg檔設定[當前為" + cfg.IdleMinutes + "分鐘]\n" +
                "・聲音偵測由選單或cfg檔設定[當前為" + OnOff(cfg.AudioDetect) + "]\n" +
                "・聲音需連續播放的秒數由cfg檔設定[當前為" + cfg.AudioSeconds + "秒]\n" +
                "・聲音音量門檻由cfg檔設定[當前為" + cfg.AudioThreshold.ToString(CultureInfo.InvariantCulture) + "]\n" +
                "・滑鼠移動門檻由cfg檔設定[當前為" + cfg.MouseMoveThreshold + "像素]\n" +
                "・「省電」模式中忽略遠端連線軟體的滑鼠輸入，由cfg檔設定[當前為" + OnOff(cfg.IgnoreRemoteInput) + "]\n" +
                "・自動切換時跳出通知，由cfg檔設定[當前為" + OnOff(cfg.ShowNotifications) + "]\n\n" +
                "【擷取】\n" +
                "・選單「將目前螢幕狀態存入「" + app.CurrentModeName + "」…」會把目前每台螢幕的狀態存進目前模式（cfg檔）。\n\n" +
                "【圖示】\n" +
                "・顏色：目前模式的顏色（cfg檔各模式的 Color）\n" +
                "・實心：自動切換開啟　空心：自動切換關閉\n\n" +
                "【設定檔】\n" +
                "・位置：" + app.ConfigLocation + "\n" +
                "・從選單「以文字文件開啟cfg檔」修改並存檔後，選「重新載入cfg檔」即生效。";

            MessageBox.Show(help, AppTitle + " - 說明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void ExitApp()
        {
            timer.Stop();
            signals.Dispose();
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

        /// <summary>選單中模式名稱前的顏色方塊。</summary>
        static Image ColorSwatch(int rgb)
        {
            var bmp = new Bitmap(16, 16);
            using Graphics g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            using var b = new SolidBrush(ToColor(rgb));
            g.FillRectangle(b, 2, 2, 12, 12);
            return bmp;
        }
    }
}
