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
        /// <summary>過渡用的舊版切換條件分段（票 03）：讀到時轉換為切換條件，不再寫出。</summary>
        const string LegacySection = "LegacySwitch";
        const string SwitchPrefix = "Switch.";
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
        /// 聲音持續秒數、忽略遠端輸入、通知；bat 路徑與參數、閒置分鐘、聲音偵測、上次模式等其餘的值捨棄。
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
                }
            }
            Normalize(c);
            return c;
        }

        // ------------------------------------------------------------
        //  新格式：讀取
        // ------------------------------------------------------------

        /// <summary>讀取新格式 cfg 的結果。</summary>
        /// <param name="Config">讀到的設定。</param>
        /// <param name="RewriteNeeded">cfg 中有過時的內容（已轉換或捨棄），應立即改寫 cfg。</param>
        /// <param name="Note">讀取時發生的事（轉換、略過無效的切換條件），每件事一行；沒有則為空字串。</param>
        public sealed record ParseResult(AppConfig Config, bool RewriteNeeded, string Note);

        /// <summary>
        /// 讀取新格式 cfg。過渡用的 [LegacySwitch] 分段轉換為切換條件（見 <see cref="ConvertLegacySwitch"/>）；
        /// bat 相關的鍵與 LastMode 捨棄；無效的切換條件略過。
        /// </summary>
        public static ParseResult Parse(string text)
        {
            var c = new AppConfig();
            int? currentModeId = null;
            var conditions = new List<(int Number, SwitchCondition Condition, string? ReadError)>();
            LegacySwitch? legacy = null;
            bool obsolete = false;

            foreach (var (name, values) in Sections(text))
            {
                if (Is(name, GeneralSection))
                {
                    foreach (var (key, val) in values) ReadGeneralKey(c, key, val, ref currentModeId);
                }
                else if (Is(name, LegacySection))
                {
                    legacy ??= new LegacySwitch();
                    foreach (var (key, val) in values)
                    {
                        legacy.Read(key, val);
                        obsolete |= LegacySwitch.IsObsoleteKey(key);
                    }
                }
                else if (TryParseSwitchSection(name, out int number))
                {
                    if (conditions.Exists(x => x.Number == number)) continue;   // 重複的序號：只認第一個
                    SwitchCondition condition = ReadCondition(values, out string? readError);
                    conditions.Add((number, condition, readError));
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
                    if (c.Modes.Exists(m => m.Id == modeId)) continue;   // 重複的模式編號：只認第一個
                    c.Modes.Add(ReadMode(modeId, values));
                }
            }

            c.CurrentModeId = currentModeId is int id && c.Modes.Exists(m => m.Id == id)
                ? id
                : c.Modes.FirstOrDefault()?.Id ?? 0;
            Normalize(c);

            var note = new StringBuilder();
            foreach (var (number, condition, readError) in conditions.OrderBy(x => x.Number))
            {
                string? invalid = readError ?? condition.Validate(c);
                if (invalid == null) c.SwitchConditions.Add(condition);
                else note.Append("已略過無效的切換條件 [" + SwitchPrefix + Int(number) + "]：" + invalid + "。\n");
            }

            if (legacy != null)
            {
                List<SwitchCondition> converted = ConvertLegacySwitch(c, legacy);
                c.SwitchConditions.AddRange(converted);
                note.Append(converted.Count > 0
                    ? "已將舊版切換條件轉換為 " + converted.Count + " 條切換條件（[" + SwitchPrefix + "N]）。\n"
                    : "已移除舊版切換條件：HomeMode／AwayMode 沒有指向兩個不同的模式，未轉換為切換條件。\n");
            }
            if (obsolete) note.Append("已移除不再使用的設定（bat 設定、LastMode）。\n");

            return new ParseResult(c, legacy != null, note.ToString());
        }

        /// <summary>過渡用的 [LegacySwitch] 分段（票 03）：舊版的兩條切換條件。</summary>
        sealed class LegacySwitch
        {
            public int HomeModeId;
            public int AwayModeId;
            public int IdleMinutes = 10;
            public bool AudioDetect = true;

            public void Read(string key, string val)
            {
                switch (key)
                {
                    case "homemode": HomeModeId = ParseInt(val, 0); break;
                    case "awaymode": AwayModeId = ParseInt(val, 0); break;
                    case "idleminutes": IdleMinutes = ParseInt(val, IdleMinutes); break;
                    case "audiodetect": AudioDetect = ParseBool(val, AudioDetect); break;
                }
            }

            /// <summary>票 02 留在此分段、已不再使用的鍵（bat 相關的鍵與 LastMode）：值捨棄。</summary>
            public static bool IsObsoleteKey(string key) => key is "lastmode" or "batpath" or "homeargs" or "awayargs";
        }

        /// <summary>
        /// 把舊版的兩條切換條件轉成等效的切換條件，維持原本的行為：
        /// HomeMode 閒置 IdleMinutes 分鐘（鍵盤滑鼠＋聲音）→ AwayMode；AwayMode 偵測到使用（鍵盤滑鼠＋聲音）→ HomeMode。
        /// AudioDetect=0 時兩條都不選聲音。HomeMode／AwayMode 沒有指向兩個不同且存在的模式時不轉換。
        /// </summary>
        static List<SwitchCondition> ConvertLegacySwitch(AppConfig c, LegacySwitch legacy)
        {
            var result = new List<SwitchCondition>();
            if (c.FindMode(legacy.HomeModeId) == null || c.FindMode(legacy.AwayModeId) == null || legacy.HomeModeId == legacy.AwayModeId)
                return result;

            UsageSources sources = UsageSources.MouseKeyboard | (legacy.AudioDetect ? UsageSources.Audio : UsageSources.None);
            result.Add(new SwitchCondition
            {
                FromModeId = legacy.HomeModeId, Kind = SwitchConditionKind.Idle,
                IdleAmount = legacy.IdleMinutes < 1 ? 10 : legacy.IdleMinutes, IdleUnit = IdleUnit.Minutes,
                Sources = sources, ToModeId = legacy.AwayModeId,
            });
            result.Add(new SwitchCondition
            {
                FromModeId = legacy.AwayModeId, Kind = SwitchConditionKind.UsageDetected, Sources = sources, ToModeId = legacy.HomeModeId,
            });
            return result;
        }

        /// <summary>
        /// 讀取一條切換條件。沒寫的鍵用預設值（閒置 10 分鐘、只選鍵盤滑鼠、不強制）；
        /// Condition 寫了無法辨識的值時 <paramref name="error"/> 為原因（不默默當成閒置），否則為 null。
        /// </summary>
        static SwitchCondition ReadCondition(List<(string Key, string Value)> values, out string? error)
        {
            var s = new SwitchCondition();
            bool mouseKeyboard = true, audio = false;
            error = null;
            foreach (var (key, val) in values)
            {
                switch (key)
                {
                    case "from": s = s with { FromModeId = ParseInt(val, 0) }; break;
                    case "to": s = s with { ToModeId = ParseInt(val, 0) }; break;
                    case "condition":
                        if (TryParseEnum(val, out SwitchConditionKind kind)) s = s with { Kind = kind };
                        else error = "無法辨識的條件類型「" + val.Trim() + "」（應為 Idle 或 UsageDetected）";
                        break;
                    case "idletime": s = s with { IdleAmount = ParseInt(val, 0) }; break;
                    case "idleunit": s = s with { IdleUnit = ParseEnum(val, s.IdleUnit) }; break;
                    case "mousekeyboard": mouseKeyboard = ParseBool(val, mouseKeyboard); break;
                    case "audio": audio = ParseBool(val, audio); break;
                    case "forced": s = s with { Forced = ParseBool(val, s.Forced) }; break;
                }
            }
            s = s with
            {
                Sources = (mouseKeyboard ? UsageSources.MouseKeyboard : UsageSources.None) | (audio ? UsageSources.Audio : UsageSources.None),
            };
            // 偵測到使用不需要閒置時間：一律用預設值，寫出時也不寫，讀回結果才會相同
            return s.Kind == SwitchConditionKind.UsageDetected ? s with { IdleAmount = new SwitchCondition().IdleAmount, IdleUnit = default } : s;
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

        static ModeConfig ReadMode(int id, List<(string Key, string Value)> values)
        {
            var mode = new ModeConfig(id, "模式 " + id);
            foreach (var (key, val) in values)
            {
                switch (key)
                {
                    case "name":
                        if (val.Trim().Length > 0) mode.Name = val.Trim();
                        break;
                    case "color": mode.Color = ParseColor(val, mode.Color); break;
                    case "manualautoswitch": mode.ManualAutoSwitch = ParseEnum(val, mode.ManualAutoSwitch); break;
                    case "unlistedmonitors": mode.UnlistedMonitors = ParseEnum(val, mode.UnlistedMonitors); break;
                }
            }
            return mode;
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
                    case "orientation": m = m with { Orientation = ParseEnum(val, m.Orientation) }; break;
                    case "primary": m = m with { Primary = ParseBool(val, m.Primary) }; break;
                }
            }
            return id == null ? null : m with { Id = id };
        }

        static bool TryParseSwitchSection(string name, out int number)
        {
            number = 0;
            return name.StartsWith(SwitchPrefix, StringComparison.OrdinalIgnoreCase)
                && TryParseId(name.Substring(SwitchPrefix.Length), out number);
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
            // 範圍與設定視窗的欄位一致（AppConfig.ValidateGeneral）
            if (c.AudioSeconds < AppConfig.MinAudioSeconds) c.AudioSeconds = AppConfig.DefaultAudioSeconds;
            if (c.AudioSeconds > AppConfig.MaxAudioSeconds) c.AudioSeconds = AppConfig.MaxAudioSeconds;
            if (c.MouseMoveThreshold < AppConfig.MinMouseMoveThreshold) c.MouseMoveThreshold = AppConfig.MinMouseMoveThreshold;
            if (c.MouseMoveThreshold > AppConfig.MaxMouseMoveThreshold) c.MouseMoveThreshold = AppConfig.MaxMouseMoveThreshold;
            if (!(c.AudioThreshold >= AppConfig.MinAudioThreshold && c.AudioThreshold <= AppConfig.MaxAudioThreshold))
                c.AudioThreshold = AppConfig.DefaultAudioThreshold;
            ModeConfig.AssignMissingColors(c.Modes);
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

            Line("# Auto Display Mode 設定檔");
            Line("# 本檔由程式整份重寫。手動修改並存檔後，請從右鍵選單選「重新載入 cfg」才會生效；");
            Line("# 程式存檔時會依格式重新產生整份內容與註解，自行加入的註解不會保留。");
            Line();

            Title("一般設定");
            Line("[" + GeneralSection + "]");
            Line("# 自動切換總開關（1=開，0=關）");
            Line("AutoSwitch=" + Bool(c.AutoSwitch));
            Line("# 目前模式（填下方某個 [Mode.N] 的 N）");
            Line("CurrentMode=" + Int(c.CurrentModeId));
            Line("# 滑鼠要移動超過幾個像素才算使用（1~1000，過濾桌面震動造成的微小位移）");
            Line("MouseMoveThreshold=" + Int(c.MouseMoveThreshold));
            Line("# 聲音峰值超過多少（0~1）才算有聲音");
            Line("AudioThreshold=" + c.AudioThreshold.ToString("R", CultureInfo.InvariantCulture));
            Line("# 聲音要連續播放幾秒才算一次使用（1~3600，過濾通知音這類短暫聲音）");
            Line("AudioSeconds=" + Int(c.AudioSeconds));
            Line("# 1 = 忽略遠端連線軟體送出的鍵盤滑鼠輸入，避免遠端操作時被當成使用");
            Line("IgnoreRemoteInput=" + Bool(c.IgnoreRemoteInput));
            Line("# 自動切換時是否跳出通知（1=是，0=否）");
            Line("ShowNotifications=" + Bool(c.ShowNotifications));
            Line("# 開機登入時自動開啟本程式（1=是，0=否）");
            Line("StartWithWindows=" + Bool(c.StartWithWindows));
            Line();

            Title("切換條件");
            Line("# 只檢查來源模式等於目前模式的切換條件，依 [Switch.N] 的 N 由小到大，第一條成立的就切換。");
            Line("# 自動切換關閉或設定視窗開著時，只檢查 Forced=1 的切換條件。");
            Line("# 每次切換完成後所有閒置計時歸零，並有 10 秒緩衝期。");
            Line("#   From           來源模式（填 [Mode.N] 的 N）");
            Line("#   Condition      Idle（閒置）或 UsageDetected（偵測到使用）");
            Line("#   IdleTime       閒置多久（正整數，只用於 Idle）");
            Line("#   IdleUnit       Minutes（分鐘）或 Hours（小時）（只用於 Idle）");
            Line("#   MouseKeyboard  使用來源「鍵盤滑鼠」（1=選，0=不選）");
            Line("#   Audio          使用來源「聲音」（1=選，0=不選；聲音要持續 AudioSeconds 秒才算使用）");
            Line("#   To             目標模式（填 [Mode.N] 的 N，不可與來源模式相同）");
            Line("#   Forced         強制（1=是，0=否；沒寫時為 0）：即使自動切換關閉、設定視窗開著，這條也照樣生效");
            Line("# 閒置：所選的使用來源全部都沒有使用，持續 IdleTime。偵測到使用：所選的使用來源任一個出現使用就成立。");
            Line("# 例：");
            Line("#   [Switch.1]");
            Line("#   From=1");
            Line("#   Condition=Idle");
            Line("#   IdleTime=5");
            Line("#   IdleUnit=Minutes");
            Line("#   MouseKeyboard=1");
            Line("#   Audio=1");
            Line("#   To=2");
            Line("#   Forced=0");
            int n = 0;
            foreach (SwitchCondition s in c.SwitchConditions)
            {
                n++;
                Line();
                Line("# 切換條件 " + Int(n) + "：" + SingleLine(s.Describe(c)));
                Line("[" + SwitchPrefix + Int(n) + "]");
                Line("From=" + Int(s.FromModeId));
                Line("Condition=" + s.Kind);
                if (s.Kind == SwitchConditionKind.Idle)
                {
                    Line("IdleTime=" + Int(s.IdleAmount));
                    Line("IdleUnit=" + s.IdleUnit);
                }
                Line("MouseKeyboard=" + Bool(s.Uses(UsageSources.MouseKeyboard)));
                Line("Audio=" + Bool(s.Uses(UsageSources.Audio)));
                Line("To=" + Int(s.ToModeId));
                Line("Forced=" + Bool(s.Forced));
            }

            if (c.Modes.Count > 0)
            {
                Line();
                Line("# ------------------------------------------------------------");
                Line("#  模式與螢幕設定");
                Line("#  [Mode.N] 是一個模式，N 為模式的編號，依模式的順序（改名不變；在設定視窗調整順序或刪除模式並套用後重新編號）：");
                Line("#    Name              模式名稱");
                Line("#    Color             工作列圖示顏色（#RRGGBB）");
                Line("#    ManualAutoSwitch  手動切換進入此模式時，自動切換要 Keep（維持）、On（開啟）或 Off（關閉）");
                Line("#    UnlistedMonitors  套用此模式時，接上但下方沒有記錄的螢幕要 Keep（維持現狀）或 Off（關閉）");
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
                Line("Name=" + modeName);
                Line("Color=" + FormatColor(mode.Color));
                Line("ManualAutoSwitch=" + mode.ManualAutoSwitch);
                Line("UnlistedMonitors=" + mode.UnlistedMonitors);

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

        /// <summary>依名稱解析列舉（不分大小寫）。</summary>
        static T ParseEnum<T>(string s, T def) where T : struct, Enum =>
            TryParseEnum(s, out T v) ? v : def;

        static bool TryParseEnum<T>(string s, out T value) where T : struct, Enum =>
            Enum.TryParse(s.Trim(), true, out value) && Enum.IsDefined(typeof(T), value);

        /// <summary>#RRGGBB（# 可省略）；格式不符時回傳 <paramref name="def"/>。</summary>
        static int ParseColor(string s, int def)
        {
            s = s.Trim().TrimStart('#');
            return s.Length == 6 && int.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int v) ? v : def;
        }

        static string FormatColor(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
    }
}
