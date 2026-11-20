using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AutoDisplayMode.Core
{
    /// <summary>
    /// cfg 編解碼：INI 分段格式（程式整份重寫並附中文註解），以及舊版「鍵=值」格式的偵測與轉移。
    /// </summary>
    internal static class ConfigFile
    {
        const string NewLine = "\r\n";

        const string GeneralSection = "General";
        const string LegacySection = "LegacySwitch";
        const string ModePrefix = "Mode.";
        const string MonitorInfix = ".Monitor.";

        // ------------------------------------------------------------
        //  舊格式
        // ------------------------------------------------------------

        /// <summary>舊格式：檔案沒有任何分段，但有設定內容。</summary>
        public static bool IsLegacy(string text)
        {
            bool hasContent = false;
            foreach (string raw in SplitLines(text))
            {
                string line = raw.Trim();
                if (line.Length == 0 || IsComment(line)) continue;
                if (line.StartsWith("[")) return false;
                hasContent = true;
            }
            return hasContent;
        }

        /// <summary>
        /// 讀取舊格式 cfg 並轉成新設定（尚無模式）。轉移自動切換、滑鼠移動門檻、聲音音量門檻、
        /// 聲音持續秒數、忽略遠端輸入、通知；bat 路徑與參數、閒置分鐘、聲音偵測、遊玩/省電模式
        /// 暫時移到「舊版切換」分段（此階段切換仍透過 bat），其餘捨棄。
        /// </summary>
        public static AppConfig MigrateLegacy(string text)
        {
            var c = new AppConfig();
            foreach (var (key, val) in KeyValues(SplitLines(text)))
            {
                switch (key)
                {
                    case "automode": c.AutoSwitch = ParseBool(val, c.AutoSwitch); break;
                    case "audioseconds": c.AudioSeconds = ParseInt(val, c.AudioSeconds); break;
                    case "audiothreshold": c.AudioThreshold = ParseDouble(val, c.AudioThreshold); break;
                    case "mousemovethreshold": c.MouseMoveThreshold = ParseInt(val, c.MouseMoveThreshold); break;
                    case "ignoreremoteinput": c.IgnoreRemoteInput = ParseBool(val, c.IgnoreRemoteInput); break;
                    case "shownotifications": c.ShowNotifications = ParseBool(val, c.ShowNotifications); break;
                    default: ReadLegacyKey(c, key, val); break;
                }
            }
            Normalize(c);
            return c;
        }

        // ------------------------------------------------------------
        //  新格式：讀取
        // ------------------------------------------------------------

        public static AppConfig Parse(string text)
        {
            var c = new AppConfig();
            int? currentModeId = null;

            foreach (var (name, values) in Sections(text))
            {
                if (Is(name, GeneralSection))
                {
                    foreach (var (key, val) in values) ReadGeneralKey(c, key, val, ref currentModeId);
                }
                else if (Is(name, LegacySection))
                {
                    foreach (var (key, val) in values) ReadLegacyKey(c, key, val);
                }
                else if (TryParseMonitorSection(name, out int modeId))
                {
                    ModeConfig? mode = c.Modes.Find(m => m.Id == modeId);
                    MonitorSettings? monitor = ReadMonitor(values);
                    if (mode != null && monitor != null && !mode.Monitors.Exists(m => m.Id == monitor.Id))
                        mode.Monitors.Add(monitor);
                }
                else if (TryParseModeSection(name, out modeId))
                {
                    if (c.Modes.Exists(m => m.Id == modeId)) continue;   // 重複的模式 ID：只認第一個
                    string modeName = values.LastOrDefault(kv => kv.Key == "name").Value?.Trim() ?? "";
                    c.Modes.Add(new ModeConfig(modeId, modeName.Length > 0 ? modeName : "模式 " + modeId));
                }
            }

            c.CurrentModeId = currentModeId is int id && c.Modes.Exists(m => m.Id == id)
                ? id
                : c.Modes.FirstOrDefault()?.Id ?? 0;
            Normalize(c);
            return c;
        }

        static void ReadGeneralKey(AppConfig c, string key, string val, ref int? currentModeId)
        {
            switch (key)
            {
                case "autoswitch": c.AutoSwitch = ParseBool(val, c.AutoSwitch); break;
                case "currentmode":
                    if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)) currentModeId = id;
                    break;
                case "mousemovethreshold": c.MouseMoveThreshold = ParseInt(val, c.MouseMoveThreshold); break;
                case "audiothreshold": c.AudioThreshold = ParseDouble(val, c.AudioThreshold); break;
                case "audioseconds": c.AudioSeconds = ParseInt(val, c.AudioSeconds); break;
                case "ignoreremoteinput": c.IgnoreRemoteInput = ParseBool(val, c.IgnoreRemoteInput); break;
                case "shownotifications": c.ShowNotifications = ParseBool(val, c.ShowNotifications); break;
                case "startwithwindows": c.StartWithWindows = ParseBool(val, c.StartWithWindows); break;
            }
        }

        static void ReadLegacyKey(AppConfig c, string key, string val)
        {
            switch (key)
            {
                case "batpath": c.BatPath = val.Trim('"'); break;
                case "homeargs": c.HomeArgs = val; break;
                case "awayargs": c.AwayArgs = val; break;
                case "idleminutes": c.IdleMinutes = ParseInt(val, c.IdleMinutes); break;
                case "audiodetect": c.AudioDetect = ParseBool(val, c.AudioDetect); break;
                case "lastmode": c.LastMode = AppConfig.ParseMode(val); break;
            }
        }

        static MonitorSettings? ReadMonitor(List<(string Key, string Value)> values)
        {
            MonitorId? id = null;
            var m = new MonitorSettings();
            foreach (var (key, val) in values)
            {
                switch (key)
                {
                    case "monitor": id = MonitorId.Parse(val); break;
                    case "name": m = m with { Name = val }; break;
                    case "enabled": m = m with { Enabled = ParseBool(val, m.Enabled) }; break;
                    case "x": m = m with { X = ParseInt(val, m.X) }; break;
                    case "y": m = m with { Y = ParseInt(val, m.Y) }; break;
                    case "width": m = m with { Width = ParseInt(val, m.Width) }; break;
                    case "height": m = m with { Height = ParseInt(val, m.Height) }; break;
                    case "refreshrate": m = m with { RefreshRate = ParseInt(val, m.RefreshRate) }; break;
                    case "orientation": m = m with { Orientation = ParseOrientation(val, m.Orientation) }; break;
                    case "primary": m = m with { Primary = ParseBool(val, m.Primary) }; break;
                }
            }
            return id == null ? null : m with { Id = id };
        }

        static bool TryParseModeSection(string name, out int modeId)
        {
            modeId = 0;
            return name.StartsWith(ModePrefix, StringComparison.OrdinalIgnoreCase)
                && TryParseId(name.Substring(ModePrefix.Length), out modeId);
        }

        static bool TryParseMonitorSection(string name, out int modeId)
        {
            modeId = 0;
            if (!name.StartsWith(ModePrefix, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = name.Substring(ModePrefix.Length);
            int i = rest.IndexOf(MonitorInfix, StringComparison.OrdinalIgnoreCase);
            return i > 0
                && TryParseId(rest.Substring(0, i), out modeId)
                && TryParseId(rest.Substring(i + MonitorInfix.Length), out _);
        }

        static bool TryParseId(string s, out int id) =>
            int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;

        static bool Is(string sectionName, string expected) =>
            string.Equals(sectionName, expected, StringComparison.OrdinalIgnoreCase);

        /// <summary>依序列出每個分段的名稱與其中的「鍵=值」（鍵轉小寫）。分段之前的內容忽略。</summary>
        static IEnumerable<(string Name, List<(string Key, string Value)> Values)> Sections(string text)
        {
            string? name = null;
            var values = new List<(string Key, string Value)>();
            foreach (string raw in SplitLines(text))
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    if (name != null) yield return (name, values);
                    name = line.Substring(1, line.Length - 2).Trim();
                    values = new List<(string Key, string Value)>();
                }
                else if (name != null && TryKeyValue(line, out string key, out string val))
                {
                    values.Add((key, val));
                }
            }
            if (name != null) yield return (name, values);
        }

        public static void Normalize(AppConfig c)
        {
            if (c.IdleMinutes < 1) c.IdleMinutes = 10;
            if (c.AudioSeconds < 1) c.AudioSeconds = 3;
            if (c.MouseMoveThreshold < 1) c.MouseMoveThreshold = 1;
        }

        // ------------------------------------------------------------
        //  新格式：寫出
        // ------------------------------------------------------------

        /// <summary>整份寫出（附中文註解）。</summary>
        public static string Format(AppConfig c)
        {
            var sb = new StringBuilder();
            void Line(string s = "") => sb.Append(s).Append(NewLine);
            void Title(string s)
            {
                Line("# ============================================================");
                Line("#  " + s);
                Line("# ============================================================");
            }

            Line("# 省電螢幕工具 設定檔");
            Line("# 本檔由程式整份重寫。手動修改並存檔後，請從右鍵選單選「重新載入cfg檔」才會生效；");
            Line("# 程式存檔時會依格式重新產生整份內容與註解，自行加入的註解不會保留。");
            Line();

            Title("一般設定");
            Line("[" + GeneralSection + "]");
            Line("# 自動切換總開關（1=開，0=關）");
            Line("AutoSwitch=" + Bool(c.AutoSwitch));
            Line("# 目前模式（填下方某個 [Mode.N] 的 N）");
            Line("CurrentMode=" + Int(c.CurrentModeId));
            Line("# 滑鼠要移動超過幾個像素才算使用（過濾桌面震動造成的微小位移）");
            Line("MouseMoveThreshold=" + Int(c.MouseMoveThreshold));
            Line("# 聲音峰值超過多少（0~1）才算有聲音");
            Line("AudioThreshold=" + c.AudioThreshold.ToString("R", CultureInfo.InvariantCulture));
            Line("# 聲音要連續播放幾秒才算一次使用（過濾通知音這類短暫聲音）");
            Line("AudioSeconds=" + Int(c.AudioSeconds));
            Line("# 1 = 忽略遠端連線軟體送出的滑鼠鍵盤輸入，避免遠端操作時被當成使用");
            Line("IgnoreRemoteInput=" + Bool(c.IgnoreRemoteInput));
            Line("# 自動切換時是否跳出通知（1=是，0=否）");
            Line("ShowNotifications=" + Bool(c.ShowNotifications));
            Line("# 開機登入時自動開啟本程式（1=是，0=否）");
            Line("StartWithWindows=" + Bool(c.StartWithWindows));
            Line();

            Title("舊版切換（過渡用）");
            Line("# 目前切換螢幕仍透過 bat；之後的版本改為依模式套用後，這一段會被移除。");
            Line("[" + LegacySection + "]");
            Line("# 螢幕切換 bat 的完整路徑");
            Line("BatPath=" + c.BatPath);
            Line("# 切換成遊玩模式 / 省電模式時傳給 bat 的參數");
            Line("HomeArgs=" + c.HomeArgs);
            Line("AwayArgs=" + c.AwayArgs);
            Line("# 遊玩模式閒置幾分鐘後切換成省電模式（也可以從右鍵選單調整）");
            Line("IdleMinutes=" + Int(c.IdleMinutes));
            Line("# 聲音偵測（1=開啟，也可以從右鍵選單調整）");
            Line("AudioDetect=" + Bool(c.AudioDetect));
            Line("# 目前是遊玩模式（home）或省電模式（away）");
            Line("LastMode=" + AppConfig.ModeKey(c.LastMode));

            if (c.Modes.Count > 0)
            {
                Line();
                Line("# ------------------------------------------------------------");
                Line("#  模式與螢幕設定");
                Line("#  [Mode.N] 是一個模式，N 為模式的 ID（不會因改名而變）。");
                Line("#  [Mode.N.Monitor.K] 是模式 N 中一台螢幕的螢幕設定，K 只是順序：");
                Line("#    Monitor      螢幕識別：廠牌/型號/SN:序號（讀不到序號時為 廠牌/型號/PORT:接孔）");
                Line("#    Name         螢幕名稱（僅供辨識）");
                Line("#    Enabled      開啟（1）或關閉（0）");
                Line("#    X, Y         座標，以主螢幕左上角為 (0,0)");
                Line("#    Width, Height  解析度（未旋轉時的寬、高）");
                Line("#    RefreshRate  更新率（Hz）");
                Line("#    Orientation  方向：Landscape（橫向）、Portrait（直向）、LandscapeFlipped（橫向翻轉）、PortraitFlipped（直向翻轉）");
                Line("#    Primary      主螢幕（1=是）");
                Line("# ------------------------------------------------------------");
            }

            foreach (ModeConfig mode in c.Modes)
            {
                string modeName = SingleLine(mode.Name);
                Line();
                Title("模式「" + modeName + "」");
                Line("[" + ModePrefix + Int(mode.Id) + "]");
                Line("# 模式名稱");
                Line("Name=" + modeName);

                int k = 0;
                foreach (MonitorSettings m in mode.Monitors)
                {
                    k++;
                    Line();
                    Line("# 模式「" + modeName + "」的螢幕設定：" + SingleLine(m.Name));
                    Line("[" + ModePrefix + Int(mode.Id) + MonitorInfix + Int(k) + "]");
                    Line("Monitor=" + m.Id);
                    Line("Name=" + SingleLine(m.Name));
                    Line("Enabled=" + Bool(m.Enabled));
                    Line("X=" + Int(m.X));
                    Line("Y=" + Int(m.Y));
                    Line("Width=" + Int(m.Width));
                    Line("Height=" + Int(m.Height));
                    Line("RefreshRate=" + Int(m.RefreshRate));
                    Line("Orientation=" + m.Orientation);
                    Line("Primary=" + Bool(m.Primary));
                }
            }
            return sb.ToString();
        }

        static string SingleLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();

        static string Int(int v) => v.ToString(CultureInfo.InvariantCulture);

        static string Bool(bool b) => b ? "1" : "0";

        // ------------------------------------------------------------
        //  共用
        // ------------------------------------------------------------

        static bool IsComment(string line) => line.StartsWith("#") || line.StartsWith(";");

        static IEnumerable<(string Key, string Value)> KeyValues(IEnumerable<string> lines)
        {
            foreach (string raw in lines)
                if (TryKeyValue(raw.Trim(), out string key, out string val)) yield return (key, val);
        }

        static bool TryKeyValue(string line, out string key, out string val)
        {
            key = val = "";
            if (line.Length == 0 || IsComment(line)) return false;
            int eq = line.IndexOf('=');
            if (eq <= 0) return false;
            key = line.Substring(0, eq).Trim().ToLowerInvariant();
            val = line.Substring(eq + 1).Trim();
            return true;
        }

        // 與 File.ReadAllLines 相同的斷行規則（\r\n、\n、\r；結尾的斷行不產生空行）
        static IEnumerable<string> SplitLines(string text)
        {
            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null) yield return line;
        }

        static bool ParseBool(string s, bool def)
        {
            s = s.Trim().ToLowerInvariant();
            if (s is "1" or "true" or "yes" or "on") return true;
            if (s is "0" or "false" or "no" or "off") return false;
            return def;
        }

        static int ParseInt(string s, int def) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : def;

        static double ParseDouble(string s, double def) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;

        static Orientation ParseOrientation(string s, Orientation def) =>
            Enum.TryParse(s.Trim(), true, out Orientation o) && Enum.IsDefined(typeof(Orientation), o) ? o : def;
    }
}
