using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AutoDisplayMode.Core
{
    /// <summary>cfg 編解碼（目前的「鍵=值」格式）。</summary>
    internal static class ConfigFile
    {
        const string NewLine = "\r\n";

        public static AppConfig Parse(string text)
        {
            var c = new AppConfig();
            foreach (string raw in SplitLines(text))
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
                    case "automode": c.AutoSwitch = ParseBool(val, c.AutoSwitch); break;
                    case "idleminutes": c.IdleMinutes = ParseInt(val, c.IdleMinutes); break;
                    case "lastmode": c.LastMode = AppConfig.ParseMode(val); break;
                    case "audiodetect": c.AudioDetect = ParseBool(val, c.AudioDetect); break;
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

        public static void Normalize(AppConfig c)
        {
            if (c.IdleMinutes < 1) c.IdleMinutes = 10;
            if (c.AudioSeconds < 1) c.AudioSeconds = 3;
            if (c.MouseMoveThreshold < 1) c.MouseMoveThreshold = 1;
        }

        /// <summary>整份寫出（附中文註解）。</summary>
        public static string Format(AppConfig c)
        {
            var sb = new StringBuilder();
            void Line(string s = "") => sb.Append(s).Append(NewLine);

            Line("# 省電螢幕工具 設定檔");
            Line("# 修改並存檔後，從右鍵選單選「重新載入cfg檔」即可生效。");
            Line();
            Line("# 螢幕切換 bat 的完整路徑");
            Line("BatPath=" + c.BatPath);
            Line("# 切換成遊玩模式 / 省電模式時傳給 bat 的參數");
            Line("HomeArgs=" + c.HomeArgs);
            Line("AwayArgs=" + c.AwayArgs);
            Line();
            Line("# 以下三項也可以從右鍵選單調整");
            Line("AutoMode=" + ValueOf(c, "automode"));
            Line("IdleMinutes=" + ValueOf(c, "idleminutes"));
            Line("LastMode=" + ValueOf(c, "lastmode"));
            Line();
            Line("# 聲音偵測（1=開啟，也可以從右鍵選單調整），連續有聲音超過幾秒才算使用中，音量門檻（0~1）");
            Line("AudioDetect=" + ValueOf(c, "audiodetect"));
            Line("AudioSeconds=" + c.AudioSeconds.ToString(CultureInfo.InvariantCulture));
            Line("AudioThreshold=" + c.AudioThreshold.ToString(CultureInfo.InvariantCulture));
            Line("# 進階：省電模式中，滑鼠要移動超過幾個像素才算有動靜（過濾桌面震動造成的微小位移）");
            Line("MouseMoveThreshold=" + c.MouseMoveThreshold.ToString(CultureInfo.InvariantCulture));
            Line("# 進階：1 = 省電模式中忽略遠端連線軟體送出的滑鼠輸入，避免遠端操作時被切回遊玩模式");
            Line("IgnoreRemoteInput=" + Bool(c.IgnoreRemoteInput));
            Line("# 自動切換時是否跳出通知");
            Line("ShowNotifications=" + Bool(c.ShowNotifications));
            return sb.ToString();
        }

        /// <summary>
        /// 只更新指定的項目，其他內容（包括使用者在記事本裡改過但還沒重新載入的部分）保持不變。
        /// 找不到的項目附加在最後。
        /// </summary>
        public static string UpdateKeys(string text, AppConfig c, params string[] keys)
        {
            var lines = new List<string>(SplitLines(text));
            foreach (string key in keys)
            {
                string value = ValueOf(c, key);
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

            var sb = new StringBuilder();
            foreach (string line in lines) sb.Append(line).Append(NewLine);
            return sb.ToString();
        }

        static string ValueOf(AppConfig c, string key) => key.ToLowerInvariant() switch
        {
            "automode" => Bool(c.AutoSwitch),
            "idleminutes" => c.IdleMinutes.ToString(CultureInfo.InvariantCulture),
            "lastmode" => AppConfig.ModeKey(c.LastMode),
            "audiodetect" => Bool(c.AudioDetect),
            _ => "",
        };

        static string Bool(bool b) => b ? "1" : "0";

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
    }
}
