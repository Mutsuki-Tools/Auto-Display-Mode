// AutoDisplayMode - 省電螢幕自動切換工具
// 編譯：執行同資料夾的 build.bat（使用 Windows 內建的 .NET Framework 4.x 編譯器）
// 注意：本檔需以 C# 5 語法撰寫，才能用 Windows 內建的 csc.exe 編譯

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoDisplayMode
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "AutoDisplayMode_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("省電螢幕工具已經在執行中，請到工作列右下角的圖示操作。",
                        "省電螢幕工具", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    MessageBox.Show("發生錯誤：\n" + e.Exception.Message, "省電螢幕工具",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                Application.Run(new TrayApp());
                GC.KeepAlive(mutex);
            }
        }
    }

    // =====================================================================
    //  設定檔
    // =====================================================================
    class AppConfig
    {
        public string FilePath;

        public string BatPath = @"C:\multimonitortool-x64\monitormode.bat";
        public string HomeArgs = "home";
        public string AwayArgs = "away";

        public bool AutoMode = true;
        public int IdleMinutes = 10;
        public string LastMode = "home";

        public bool AudioDetect = true;
        public int AudioSeconds = 3;
        public double AudioThreshold = 0.005;
        public int MouseMoveThreshold = 10;
        public bool IgnoreRemoteInput = true;
        public bool ShowNotifications = true;

        public static AppConfig Load(string path)
        {
            AppConfig c = new AppConfig();
            c.FilePath = path;

            if (!File.Exists(path))
            {
                c.Save();
                return c;
            }

            try
            {
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "batpath": c.BatPath = val.Trim('"'); break;
                        case "homeargs": c.HomeArgs = val; break;
                        case "awayargs": c.AwayArgs = val; break;
                        case "automode": c.AutoMode = ParseBool(val, c.AutoMode); break;
                        case "idleminutes": c.IdleMinutes = ParseInt(val, c.IdleMinutes); break;
                        case "lastmode": c.LastMode = val.ToLowerInvariant(); break;
                        case "audiodetect": c.AudioDetect = ParseBool(val, c.AudioDetect); break;
                        case "audioseconds": c.AudioSeconds = ParseInt(val, c.AudioSeconds); break;
                        case "audiothreshold": c.AudioThreshold = ParseDouble(val, c.AudioThreshold); break;
                        case "mousemovethreshold": c.MouseMoveThreshold = ParseInt(val, c.MouseMoveThreshold); break;
                        case "ignoreremoteinput": c.IgnoreRemoteInput = ParseBool(val, c.IgnoreRemoteInput); break;
                        case "shownotifications": c.ShowNotifications = ParseBool(val, c.ShowNotifications); break;
                    }
                }
            }
            catch
            {
                // 讀取失敗就用預設值
            }

            if (c.IdleMinutes < 1) c.IdleMinutes = 10;
            if (c.AudioSeconds < 1) c.AudioSeconds = 3;
            if (c.MouseMoveThreshold < 1) c.MouseMoveThreshold = 1;
            if (c.LastMode != "away") c.LastMode = "home";
            return c;
        }

        public void Save()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# 省電螢幕工具 設定檔");
            sb.AppendLine("# 修改並存檔後，從右鍵選單選「重新載入cfg檔」即可生效。");
            sb.AppendLine();
            sb.AppendLine("# 螢幕切換 bat 的完整路徑");
            sb.AppendLine("BatPath=" + BatPath);
            sb.AppendLine("# 切換成遊玩模式 / 省電模式時傳給 bat 的參數");
            sb.AppendLine("HomeArgs=" + HomeArgs);
            sb.AppendLine("AwayArgs=" + AwayArgs);
            sb.AppendLine();
            sb.AppendLine("# 以下三項也可以從右鍵選單調整");
            sb.AppendLine("AutoMode=" + (AutoMode ? "1" : "0"));
            sb.AppendLine("IdleMinutes=" + IdleMinutes.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("LastMode=" + LastMode);
            sb.AppendLine();
            sb.AppendLine("# 聲音偵測（1=開啟，也可以從右鍵選單調整），連續有聲音超過幾秒才算使用中，音量門檻（0~1）");
            sb.AppendLine("AudioDetect=" + (AudioDetect ? "1" : "0"));
            sb.AppendLine("AudioSeconds=" + AudioSeconds.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("AudioThreshold=" + AudioThreshold.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# 進階：省電模式中，滑鼠要移動超過幾個像素才算有動靜（過濾桌面震動造成的微小位移）");
            sb.AppendLine("MouseMoveThreshold=" + MouseMoveThreshold.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("# 進階：1 = 省電模式中忽略遠端連線軟體送出的滑鼠輸入，避免遠端操作時被切回遊玩模式");
            sb.AppendLine("IgnoreRemoteInput=" + (IgnoreRemoteInput ? "1" : "0"));
            sb.AppendLine("# 自動切換時是否跳出通知");
            sb.AppendLine("ShowNotifications=" + (ShowNotifications ? "1" : "0"));

            try
            {
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(true));
            }
            catch
            {
                // 資料夾沒有寫入權限等情況，忽略
            }
        }

        // 只更新指定的項目，其他內容（包括你在記事本裡改過但還沒重新載入的部分）保持不變
        public void SaveKeys(params string[] keys)
        {
            if (!File.Exists(FilePath))
            {
                Save();
                return;
            }

            try
            {
                List<string> lines = new List<string>(File.ReadAllLines(FilePath, Encoding.UTF8));
                foreach (string key in keys)
                {
                    string value = GetValueString(key);
                    bool found = false;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        string t = lines[i].Trim();
                        if (t.StartsWith("#") || t.StartsWith(";")) continue;
                        int eq = t.IndexOf('=');
                        if (eq <= 0) continue;
                        if (string.Equals(t.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                        {
                            lines[i] = key + "=" + value;
                            found = true;
                            break;
                        }
                    }
                    if (!found) lines.Add(key + "=" + value);
                }
                File.WriteAllLines(FilePath, lines.ToArray(), new UTF8Encoding(true));
            }
            catch
            {
                // 忽略
            }
        }

        string GetValueString(string key)
        {
            switch (key.ToLowerInvariant())
            {
                case "automode": return AutoMode ? "1" : "0";
                case "idleminutes": return IdleMinutes.ToString(CultureInfo.InvariantCulture);
                case "lastmode": return LastMode;
                case "audiodetect": return AudioDetect ? "1" : "0";
                default: return "";
            }
        }

        static bool ParseBool(string s, bool def)
        {
            s = s.Trim().ToLowerInvariant();
            if (s == "1" || s == "true" || s == "yes" || s == "on") return true;
            if (s == "0" || s == "false" || s == "no" || s == "off") return false;
            return def;
        }

        static int ParseInt(string s, int def)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        static double ParseDouble(string s, double def)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
        }
    }

    // =====================================================================
    //  工作列程式本體
    // =====================================================================
    class TrayApp : ApplicationContext
    {
        const string AppTitle = "省電螢幕工具";
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "AutoDisplayMode";
        const int WM_MOUSEMOVE = 0x0200;
        const uint LLMHF_INJECTED = 0x01;
        const uint LLMHF_LOWER_IL_INJECTED = 0x02;
        static readonly int[] IdleChoices = new int[] { 5, 10, 30, 60 };

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        AppConfig cfg;
        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly ToolStripMenuItem miStatus;
        readonly ToolStripMenuItem miAuto;
        readonly ToolStripMenuItem miIdle;
        readonly ToolStripMenuItem miAudio;
        readonly ToolStripMenuItem miStartup;
        readonly List<ToolStripMenuItem> idleItems = new List<ToolStripMenuItem>();
        readonly Icon iconHomeAuto, iconHomeManual, iconAwayAuto, iconAwayManual;
        readonly System.Windows.Forms.Timer timer;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly SynchronizationContext ui;
        readonly AudioMonitor audio;
        readonly MouseHook hook;

        string mode;              // "home" 或 "away"
        bool busy;                // bat 執行中
        long lastActivityMs;      // 最後一次偵測到使用中的時間
        long ignoreUntilMs;       // 在這個時間之前忽略所有偵測（切換螢幕後的緩衝）
        bool mouseActivity;       // 上一秒內是否有實體滑鼠動作（省電模式用）
        int audioStreak;          // 連續偵測到聲音的秒數
        bool isRemoteSession;     // 是否為 Windows 遠端桌面 (RDP) 工作階段
        bool hasAnchor;
        int anchorX, anchorY;
        uint lastInputTick;       // 遊玩模式用：Windows 最後輸入時間
        int hookAgeTicks;

        public TrayApp()
        {
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            cfg = AppConfig.Load(Path.Combine(dir, "AutoDisplayMode.cfg"));
            mode = cfg.LastMode;

            if (SynchronizationContext.Current == null)
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            ui = SynchronizationContext.Current;

            Color homeColor = Color.FromArgb(235, 130, 50);
            Color awayColor = Color.FromArgb(60, 170, 90);
            iconHomeAuto = MakeIcon(homeColor, true);
            iconHomeManual = MakeIcon(homeColor, false);
            iconAwayAuto = MakeIcon(awayColor, true);
            iconAwayManual = MakeIcon(awayColor, false);

            // ---- 右鍵選單 ----
            menu = new ContextMenuStrip();

            miStatus = new ToolStripMenuItem("");
            miStatus.Enabled = false;

            ToolStripMenuItem miPlay = new ToolStripMenuItem("切換成遊玩模式（將開啟自動模式）", null,
                delegate { ManualSwitch("home", true); });
            ToolStripMenuItem miSave = new ToolStripMenuItem("切換成省電模式（將關閉自動模式）", null,
                delegate { ManualSwitch("away", false); });
            ToolStripMenuItem miSaveAuto = new ToolStripMenuItem("切換成省電模式並開啟自動模式", null,
                delegate { ManualSwitch("away", true); });

            miAuto = new ToolStripMenuItem("", null, delegate { SetAuto(!cfg.AutoMode); });

            miIdle = new ToolStripMenuItem("");
            foreach (int m in IdleChoices)
            {
                int minutes = m;
                ToolStripMenuItem item = new ToolStripMenuItem(minutes + " 分鐘", null,
                    delegate { SetIdle(minutes); });
                item.Tag = minutes;
                idleItems.Add(item);
                miIdle.DropDownItems.Add(item);
            }

            miAudio = new ToolStripMenuItem("", null, delegate { SetAudioDetect(!cfg.AudioDetect); });

            ToolStripMenuItem miHelp = new ToolStripMenuItem("說明", null, delegate { ShowHelp(); });
            ToolStripMenuItem miOpenCfg = new ToolStripMenuItem("以文字文件開啟cfg檔", null, delegate { OpenConfig(); });
            ToolStripMenuItem miReload = new ToolStripMenuItem("重新載入cfg檔", null, delegate { ReloadConfig(); });
            miStartup = new ToolStripMenuItem("", null, delegate
            {
                SetStartup(!IsStartupEnabled());
                UpdateUi();
            });
            ToolStripMenuItem miExit = new ToolStripMenuItem("關閉程式", null, delegate { ExitApp(); });

            menu.Items.AddRange(new ToolStripItem[]
            {
                miStatus,
                new ToolStripSeparator(),
                miPlay,
                miSave,
                miSaveAuto,
                new ToolStripSeparator(),
                miAuto,
                miIdle,
                miAudio,
                new ToolStripSeparator(),
                miHelp,
                miOpenCfg,
                miReload,
                miStartup,
                new ToolStripSeparator(),
                miExit
            });
            menu.Opening += delegate { UpdateUi(); };

            tray = new NotifyIcon();
            tray.ContextMenuStrip = menu;
            tray.Visible = true;

            // ---- 偵測器 ----
            try { audio = new AudioMonitor(); }
            catch { audio = null; }
            hook = new MouseHook(OnMouse);

            lastActivityMs = clock.ElapsedMilliseconds;
            ignoreUntilMs = lastActivityMs + 5000;
            lastInputTick = ReadLastInputTick();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += delegate { OnTick(); };
            timer.Start();

            UpdateUi();

            // 啟動通知（等訊息迴圈開始後再顯示）
            ui.Post(delegate { ShowBalloon("省電螢幕工具已啟動。\n" + StatusSummary(), ToolTipIcon.Info); }, null);
        }

        // ------------------------------------------------------------
        //  每秒檢查一次
        // ------------------------------------------------------------
        void OnTick()
        {
            long now = clock.ElapsedMilliseconds;
            isRemoteSession = SystemInformation.TerminalServerSession;

            // 聲音：連續超過 AudioSeconds 秒才算
            bool sound = false;
            if (audio != null && cfg.AudioDetect)
            {
                try { sound = audio.GetPeak() > cfg.AudioThreshold; }
                catch { sound = false; }
            }
            audioStreak = sound ? audioStreak + 1 : 0;
            bool activity = audioStreak > cfg.AudioSeconds;

            if (mode == "away")
            {
                // 省電模式：只看實體滑鼠（低階滑鼠鉤子，可分辨遠端軟體送出的輸入）
                if (mouseActivity) activity = true;
            }
            else
            {
                // 遊玩模式：看 Windows 最後輸入時間（不掛鉤子，不影響遊戲滑鼠延遲）
                uint t = ReadLastInputTick();
                if (t != lastInputTick)
                {
                    lastInputTick = t;
                    activity = true;
                }
            }
            mouseActivity = false;
            if (activity) lastActivityMs = now;

            UpdateHook();

            if (!cfg.AutoMode || busy || now < ignoreUntilMs) return;

            if (mode == "away")
            {
                if (activity) SwitchTo("home", "偵測到使用中，已自動切換成遊玩模式。");
            }
            else if (now - lastActivityMs >= cfg.IdleMinutes * 60000L)
            {
                SwitchTo("away", "閒置 " + cfg.IdleMinutes + " 分鐘，已自動切換成省電模式。");
            }
        }

        // 只在「自動模式 + 省電模式」時掛滑鼠鉤子，遊玩模式完全不掛
        void UpdateHook()
        {
            bool need = cfg.AutoMode && mode == "away" && !busy;
            if (need)
            {
                if (!hook.IsInstalled)
                {
                    hook.Install();
                    hasAnchor = false;
                    hookAgeTicks = 0;
                }
                else if (++hookAgeTicks >= 600)
                {
                    // Windows 可能在系統忙碌時默默移除鉤子，每 10 分鐘重新掛一次保險
                    hook.Uninstall();
                    hook.Install();
                    hookAgeTicks = 0;
                }
            }
            else if (hook.IsInstalled)
            {
                hook.Uninstall();
            }
        }

        void OnMouse(int msg, int x, int y, uint flags)
        {
            if (busy || clock.ElapsedMilliseconds < ignoreUntilMs)
            {
                hasAnchor = false;
                return;
            }

            if (cfg.IgnoreRemoteInput &&
                (isRemoteSession || (flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0))
                return;

            if (msg == WM_MOUSEMOVE)
            {
                if (!hasAnchor)
                {
                    anchorX = x;
                    anchorY = y;
                    hasAnchor = true;
                    return;
                }
                long dx = x - anchorX;
                long dy = y - anchorY;
                long th = cfg.MouseMoveThreshold;
                if (dx * dx + dy * dy >= th * th)
                {
                    mouseActivity = true;
                    anchorX = x;
                    anchorY = y;
                }
            }
            else
            {
                // 點擊、滾輪
                mouseActivity = true;
            }
        }

        // ------------------------------------------------------------
        //  切換模式
        // ------------------------------------------------------------
        void SwitchTo(string target, string notifyText)
        {
            long now = clock.ElapsedMilliseconds;
            string bat = cfg.BatPath;

            if (string.IsNullOrEmpty(bat) || !File.Exists(bat))
            {
                ignoreUntilMs = now + 60000;
                ShowBalloon("找不到 bat 檔：\n" + bat + "\n請關閉程式後修改 AutoDisplayMode.cfg 的 BatPath。",
                    ToolTipIcon.Warning);
                return;
            }

            mode = target;
            cfg.LastMode = target;
            cfg.SaveKeys("LastMode");

            busy = true;
            mouseActivity = false;
            audioStreak = 0;
            hasAnchor = false;
            lastActivityMs = now;
            ignoreUntilMs = now + 120000;
            UpdateHook();
            UpdateUi();

            if (notifyText != null && cfg.ShowNotifications)
                ShowBalloon(notifyText, ToolTipIcon.Info);

            string args = target == "home" ? cfg.HomeArgs : cfg.AwayArgs;
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + bat + "\" " + args + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.WorkingDirectory = Path.GetDirectoryName(bat);

            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try
                {
                    using (Process p = Process.Start(psi))
                    {
                        if (!p.WaitForExit(60000))
                        {
                            try { p.Kill(); } catch { }
                            error = "bat 執行超過 60 秒沒有結束，已強制停止。";
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = "無法執行 bat：" + ex.Message;
                }
                ui.Post(delegate { OnSwitchDone(error); }, null);
            });
        }

        void OnSwitchDone(string error)
        {
            long now = clock.ElapsedMilliseconds;
            busy = false;
            ignoreUntilMs = now + 10000;   // 螢幕重新排列時游標可能被系統移動，緩衝 10 秒
            lastActivityMs = now;
            lastInputTick = ReadLastInputTick();
            hasAnchor = false;
            mouseActivity = false;
            audioStreak = 0;
            if (audio != null) audio.RequestRefresh();
            UpdateUi();

            if (error != null) ShowBalloon(error, ToolTipIcon.Error);
        }

        void ManualSwitch(string target, bool autoOn)
        {
            if (busy)
            {
                ShowBalloon("正在切換中，請稍候。", ToolTipIcon.Info);
                return;
            }
            cfg.AutoMode = autoOn;
            cfg.SaveKeys("AutoMode");
            SwitchTo(target, null);
            UpdateUi();
        }

        void SetAuto(bool on)
        {
            long now = clock.ElapsedMilliseconds;
            cfg.AutoMode = on;
            cfg.SaveKeys("AutoMode");
            lastActivityMs = now;
            ignoreUntilMs = Math.Max(ignoreUntilMs, now + 3000);
            mouseActivity = false;
            audioStreak = 0;
            hasAnchor = false;
            lastInputTick = ReadLastInputTick();
            UpdateHook();
            UpdateUi();
        }

        void SetIdle(int minutes)
        {
            cfg.IdleMinutes = minutes;
            cfg.SaveKeys("IdleMinutes");
            UpdateUi();
        }

        void SetAudioDetect(bool on)
        {
            cfg.AudioDetect = on;
            cfg.SaveKeys("AudioDetect");
            audioStreak = 0;
            UpdateUi();
        }

        // ------------------------------------------------------------
        //  設定檔
        // ------------------------------------------------------------
        void OpenConfig()
        {
            if (!File.Exists(cfg.FilePath)) cfg.Save();
            try
            {
                Process.Start("notepad.exe", "\"" + cfg.FilePath + "\"");
            }
            catch (Exception ex)
            {
                ShowBalloon("無法開啟設定檔：" + ex.Message, ToolTipIcon.Error);
            }
        }

        void ReloadConfig()
        {
            cfg = AppConfig.Load(cfg.FilePath);
            cfg.LastMode = mode;   // 目前實際的螢幕狀態以程式記錄為準，不因重新載入而切換

            long now = clock.ElapsedMilliseconds;
            lastActivityMs = now;
            mouseActivity = false;
            audioStreak = 0;
            hasAnchor = false;
            lastInputTick = ReadLastInputTick();
            if (audio != null) audio.RequestRefresh();
            UpdateHook();
            UpdateUi();

            string msg = "已重新載入設定檔。\n" + StatusSummary();
            if (string.IsNullOrEmpty(cfg.BatPath) || !File.Exists(cfg.BatPath))
            {
                msg += "\n注意：找不到 bat 檔，請檢查 BatPath。";
                ShowBalloon(msg, ToolTipIcon.Warning);
            }
            else
            {
                ShowBalloon(msg, ToolTipIcon.Info);
            }
        }

        // ------------------------------------------------------------
        //  開機自動啟動（寫入目前使用者的登錄檔 Run）
        // ------------------------------------------------------------
        static string StartupCommand()
        {
            return "\"" + Application.ExecutablePath + "\"";
        }

        static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (k == null) return false;
                    string v = k.GetValue(RunValueName) as string;
                    return v != null && string.Equals(v, StartupCommand(), StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        void SetStartup(bool enable)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (enable) k.SetValue(RunValueName, StartupCommand());
                    else k.DeleteValue(RunValueName, false);
                }
            }
            catch (Exception ex)
            {
                ShowBalloon("無法修改開機啟動設定：" + ex.Message, ToolTipIcon.Error);
            }
        }

        // ------------------------------------------------------------
        //  介面
        // ------------------------------------------------------------
        void UpdateUi()
        {
            bool home = mode == "home";
            string modeName = home ? "遊玩模式" : "省電模式";

            miStatus.Text = "目前：" + modeName + (busy ? "（切換中…）" : "");
            miAuto.Text = "切換自動模式 [目前：" + (cfg.AutoMode ? "開" : "關") + "]";
            miIdle.Text = "自動模式時閒置切換成省電模式的時間 [目前：" + cfg.IdleMinutes + " 分]";
            foreach (ToolStripMenuItem item in idleItems)
                item.Checked = (int)item.Tag == cfg.IdleMinutes;
            miAudio.Text = "自動模式偵測聲音 [目前：" + (cfg.AudioDetect ? "開" : "關") + "]";
            miStartup.Text = "開機時自動開啟此程式 [目前：" + (IsStartupEnabled() ? "開" : "關") + "]";

            if (home) tray.Icon = cfg.AutoMode ? iconHomeAuto : iconHomeManual;
            else tray.Icon = cfg.AutoMode ? iconAwayAuto : iconAwayManual;

            string tip = AppTitle + "\n" + modeName + "｜自動模式：" + (cfg.AutoMode ? "開" : "關");
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        void ShowBalloon(string text, ToolTipIcon icon)
        {
            tray.ShowBalloonTip(4000, AppTitle, text, icon);
        }

        string StatusSummary()
        {
            return "目前：" + (mode == "home" ? "遊玩模式" : "省電模式") +
                   "｜自動模式：" + (cfg.AutoMode ? "開" : "關") + "\n" +
                   "閒置 " + cfg.IdleMinutes + " 分鐘切換省電｜聲音偵測：" + (cfg.AudioDetect ? "開" : "關");
        }

        static string OnOff(bool b)
        {
            return b ? "開" : "關";
        }

        void ShowHelp()
        {
            string help =
                "【模式】\n" +
                "・遊玩模式：執行 bat 並傳入參數，由cfg檔設定[當前為 " + cfg.HomeArgs + "]\n" +
                "・省電模式：執行 bat 並傳入參數，由cfg檔設定[當前為 " + cfg.AwayArgs + "]\n" +
                "・bat 路徑由cfg檔設定[當前為 " + cfg.BatPath + "]\n\n" +
                "【自動模式】由選單或cfg檔設定[當前為" + OnOff(cfg.AutoMode) + "]\n" +
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
                "【圖示】\n" +
                "・橘色：遊玩模式　綠色：省電模式\n" +
                "・實心：自動模式開啟　空心：自動模式關閉\n\n" +
                "【設定檔】\n" +
                "・位置：" + cfg.FilePath + "\n" +
                "・從選單「以文字文件開啟cfg檔」修改並存檔後，選「重新載入cfg檔」即生效。";

            MessageBox.Show(help, AppTitle + " - 說明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void ExitApp()
        {
            timer.Stop();
            hook.Uninstall();
            if (audio != null) audio.Dispose();
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }

        static uint ReadLastInputTick()
        {
            LASTINPUTINFO lii = new LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            return GetLastInputInfo(ref lii) ? lii.dwTime : 0;
        }

        // 畫一個小螢幕圖示：實心 = 自動模式開，空心 = 自動模式關
        static Icon MakeIcon(Color color, bool filled)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    Rectangle screen = new Rectangle(3, 4, 26, 18);
                    if (filled)
                    {
                        using (SolidBrush b = new SolidBrush(color)) g.FillRectangle(b, screen);
                    }
                    using (Pen p = new Pen(color, 3f))
                    {
                        g.DrawRectangle(p, screen);
                        g.DrawLine(p, 16, 23, 16, 27);
                        g.DrawLine(p, 9, 28, 23, 28);
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
    }

    // =====================================================================
    //  低階滑鼠鉤子（只在省電模式掛上）
    // =====================================================================
    class MouseHook
    {
        public delegate void MouseCallback(int message, int x, int y, uint flags);
        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        const int WH_MOUSE_LL = 14;

        readonly HookProc proc;          // 保持參考，避免被 GC 回收
        readonly MouseCallback callback;
        IntPtr handle = IntPtr.Zero;

        public MouseHook(MouseCallback cb)
        {
            callback = cb;
            proc = HookCallback;
        }

        public bool IsInstalled
        {
            get { return handle != IntPtr.Zero; }
        }

        public void Install()
        {
            if (handle != IntPtr.Zero) return;
            handle = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
        }

        public void Uninstall()
        {
            if (handle == IntPtr.Zero) return;
            UnhookWindowsHookEx(handle);
            handle = IntPtr.Zero;
        }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    // MSLLHOOKSTRUCT: pt.x(0) pt.y(4) mouseData(8) flags(12)
                    int x = Marshal.ReadInt32(lParam, 0);
                    int y = Marshal.ReadInt32(lParam, 4);
                    uint flags = (uint)Marshal.ReadInt32(lParam, 12);
                    callback(wParam.ToInt32(), x, y, flags);
                }
                catch
                {
                    // 鉤子裡絕對不能丟例外
                }
            }
            return CallNextHookEx(handle, nCode, wParam, lParam);
        }
    }

    // =====================================================================
    //  聲音偵測（Windows Core Audio 音量表，讀取所有播放裝置的峰值）
    // =====================================================================
    class AudioMonitor : IDisposable
    {
        const int eRender = 0;
        const int DEVICE_STATE_ACTIVE = 1;
        const int CLSCTX_ALL = 23;

        IMMDeviceEnumerator enumerator;
        readonly List<IAudioMeterInformation> meters = new List<IAudioMeterInformation>();
        bool refreshRequested = true;
        int ticks;

        public AudioMonitor()
        {
            enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorCom());
        }

        public void RequestRefresh()
        {
            refreshRequested = true;
        }

        public float GetPeak()
        {
            // 每 30 秒重新列舉一次播放裝置（耳機插拔、螢幕切換會改變裝置）
            if (refreshRequested || ++ticks >= 30)
            {
                Refresh();
                ticks = 0;
                refreshRequested = false;
            }

            float max = 0f;
            for (int i = 0; i < meters.Count; i++)
            {
                float p;
                if (meters[i].GetPeakValue(out p) != 0)
                {
                    refreshRequested = true;
                    continue;
                }
                if (p > max) max = p;
            }
            return max;
        }

        void Refresh()
        {
            ReleaseMeters();
            if (enumerator == null) return;

            IMMDeviceCollection col;
            if (enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out col) != 0 || col == null) return;

            try
            {
                uint count;
                if (col.GetCount(out count) != 0) return;

                for (uint i = 0; i < count; i++)
                {
                    IMMDevice dev;
                    if (col.Item(i, out dev) != 0 || dev == null) continue;
                    try
                    {
                        Guid iid = typeof(IAudioMeterInformation).GUID;
                        object o;
                        if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o) == 0 && o != null)
                            meters.Add((IAudioMeterInformation)o);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(dev);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(col);
            }
        }

        void ReleaseMeters()
        {
            foreach (IAudioMeterInformation m in meters)
            {
                try { Marshal.ReleaseComObject(m); } catch { }
            }
            meters.Clear();
        }

        public void Dispose()
        {
            ReleaseMeters();
            if (enumerator != null)
            {
                Marshal.ReleaseComObject(enumerator);
                enumerator = null;
            }
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
        [PreserveSig] int GetMeteringChannelCount(out int count);
        [PreserveSig] int GetChannelsPeakValues(int count, [Out, MarshalAs(UnmanagedType.LPArray)] float[] peaks);
        [PreserveSig] int QueryHardwareSupport(out int mask);
    }
}