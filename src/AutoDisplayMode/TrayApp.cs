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
        readonly ToolStripMenuItem miAuto;
        readonly ToolStripMenuItem miIdle;
        readonly ToolStripMenuItem miAudio;
        readonly ToolStripMenuItem miStartup;
        readonly ToolStripMenuItem miCapture;
        readonly List<ToolStripMenuItem> idleItems = new();
        readonly Icon iconHomeAuto, iconHomeManual, iconAwayAuto, iconAwayManual;
        readonly System.Windows.Forms.Timer timer;

        public TrayApp()
        {
            if (SynchronizationContext.Current == null)
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            SynchronizationContext ui = SynchronizationContext.Current!;

            Color homeColor = Color.FromArgb(235, 130, 50);
            Color awayColor = Color.FromArgb(60, 170, 90);
            iconHomeAuto = MakeIcon(homeColor, true);
            iconHomeManual = MakeIcon(homeColor, false);
            iconAwayAuto = MakeIcon(awayColor, true);
            iconAwayManual = MakeIcon(awayColor, false);

            tray = new NotifyIcon();

            // ---- 核心與 Windows 轉接層 ----
            var clock = new StopwatchClock();
            signals = new WindowsUsageSignals(clock);
            var switcher = new BatDisplaySwitcher(ui);
            string dir = Path.GetDirectoryName(Application.ExecutablePath)!;
            app = new AppCoordinator(
                new FileConfigStore(Path.Combine(dir, "AutoDisplayMode.cfg")),
                switcher, new WindowsMonitorSystem(), signals, clock, new RegistryStartup(), new BalloonNotifier(tray, AppTitle));
            switcher.Finished += app.OnSwitchFinished;

            // ---- 右鍵選單 ----
            menu = new ContextMenuStrip();

            miStatus = new ToolStripMenuItem("") { Enabled = false };

            var miHome = new ToolStripMenuItem("切換成遊玩模式（將開啟自動模式）", null,
                delegate { app.ManualSwitch(Mode.Home, true); });
            var miAway = new ToolStripMenuItem("切換成省電模式（將關閉自動模式）", null,
                delegate { app.ManualSwitch(Mode.Away, false); });
            var miAwayAuto = new ToolStripMenuItem("切換成省電模式並開啟自動模式", null,
                delegate { app.ManualSwitch(Mode.Away, true); });

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
                miHome,
                miAway,
                miAwayAuto,
                new ToolStripSeparator(),
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
            bool home = app.CurrentMode == Mode.Home;
            string modeName = AppCoordinator.ModeName(app.CurrentMode);

            miStatus.Text = "目前：" + modeName + (app.IsSwitching ? "（切換中…）" : "");
            miAuto.Text = "切換自動模式 [目前：" + OnOff(cfg.AutoSwitch) + "]";
            miIdle.Text = "自動模式時閒置切換成省電模式的時間 [目前：" + cfg.IdleMinutes + " 分]";
            foreach (ToolStripMenuItem item in idleItems)
                item.Checked = (int)item.Tag == cfg.IdleMinutes;
            miAudio.Text = "自動模式偵測聲音 [目前：" + OnOff(cfg.AudioDetect) + "]";
            miStartup.Text = "開機時自動開啟此程式 [目前：" + OnOff(app.StartupEnabled) + "]";
            miCapture.Text = "將目前螢幕狀態存入「" + app.CurrentModeName + "」…";
            miCapture.Enabled = !app.IsSwitching;

            if (home) tray.Icon = cfg.AutoSwitch ? iconHomeAuto : iconHomeManual;
            else tray.Icon = cfg.AutoSwitch ? iconAwayAuto : iconAwayManual;

            string tip = AppTitle + "\n" + modeName + "｜自動模式：" + OnOff(cfg.AutoSwitch);
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        void ShowBalloon(string text, ToolTipIcon icon) => tray.ShowBalloonTip(4000, AppTitle, text, icon);

        static string OnOff(bool b) => b ? "開" : "關";

        void ShowHelp()
        {
            AppConfig cfg = app.Config;
            string help =
                "【模式】\n" +
                "・遊玩模式：執行 bat 並傳入參數，由cfg檔設定[當前為 " + cfg.HomeArgs + "]\n" +
                "・省電模式：執行 bat 並傳入參數，由cfg檔設定[當前為 " + cfg.AwayArgs + "]\n" +
                "・bat 路徑由cfg檔設定[當前為 " + cfg.BatPath + "]\n\n" +
                "【自動模式】由選單或cfg檔設定[當前為" + OnOff(cfg.AutoSwitch) + "]\n" +
                "・省電模式中，偵測到滑鼠移動或點擊，或電腦連續播放聲音，就切換成遊玩模式。\n" +
                "・遊玩模式中，沒有任何鍵盤滑鼠操作、也沒有持續播放聲音，超過閒置時間後切換成省電模式。\n" +
                "・閒置時間由選單或cfg檔設定[當前為" + cfg.IdleMinutes + "分鐘]\n" +
                "・聲音偵測由選單或cfg檔設定[當前為" + OnOff(cfg.AudioDetect) + "]\n" +
                "・聲音需連續播放的秒數由cfg檔設定[當前為" + cfg.AudioSeconds + "秒]\n" +
                "・聲音音量門檻由cfg檔設定[當前為" + cfg.AudioThreshold.ToString(CultureInfo.InvariantCulture) + "]\n" +
                "・滑鼠移動門檻由cfg檔設定[當前為" + cfg.MouseMoveThreshold + "像素]\n" +
                "・省電模式中忽略遠端連線軟體的滑鼠輸入，由cfg檔設定[當前為" + OnOff(cfg.IgnoreRemoteInput) + "]\n" +
                "・自動切換時跳出通知，由cfg檔設定[當前為" + OnOff(cfg.ShowNotifications) + "]\n" +
                "・手動選擇「切換成省電模式」會關閉自動模式，避免偵測到滑鼠動作被切回來；\n" +
                "  選擇「切換成遊玩模式」會重新開啟自動模式。\n" +
                "・「切換成省電模式並開啟自動模式」適合遠端時使用：遠端操作不會觸發切換，\n" +
                "  回家動到實體滑鼠就會自動切回遊玩模式。\n\n" +
                "【擷取】\n" +
                "・選單「將目前螢幕狀態存入「" + app.CurrentModeName + "」…」會把目前每台螢幕的狀態存進目前模式（cfg檔）。\n" +
                "・此版本切換螢幕仍透過 bat，存入的螢幕設定會在之後的版本用來切換。\n\n" +
                "【圖示】\n" +
                "・橘色：遊玩模式　綠色：省電模式\n" +
                "・實心：自動模式開啟　空心：自動模式關閉\n\n" +
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
    }
}
