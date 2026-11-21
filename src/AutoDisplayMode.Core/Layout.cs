using System.Collections.Generic;
using System.Linq;

namespace AutoDisplayMode.Core
{
    /// <summary>配置驗證的結果：錯誤會擋下存檔，警告只提醒。</summary>
    public sealed record LayoutReport(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

    /// <summary>配置驗證：檢查一個模式的螢幕配置（模式中記錄的螢幕設定）。</summary>
    public static class MonitorLayout
    {
        /// <summary>
        /// 檢查模式中記錄的螢幕設定（只看開啟的螢幕，寬高依方向對調）：
        /// <list type="bullet">
        /// <item>錯誤：螢幕重疊。</item>
        /// <item>錯誤：有開啟的螢幕時，主螢幕必須剛好一台且位於 (0,0)。</item>
        /// <item>錯誤：有記錄螢幕、全部關閉且未記錄的螢幕也關閉（套用後沒有螢幕開著）。</item>
        /// <item>警告：有螢幕沒有沿著邊與主螢幕相連（空隙）。</item>
        /// <item>警告（有傳入 <paramref name="connected"/> 時）：此模式沒記錄、套用時維持開啟的接上螢幕，
        ///   以目前位置會與記錄的螢幕重疊。是否重疊要看套用當時的狀態，所以只警告。</item>
        /// </list>
        /// </summary>
        public static LayoutReport Check(ModeConfig mode, IReadOnlyList<DetectedMonitor>? connected = null)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            List<MonitorSettings> on = mode.Monitors.Where(m => m.Enabled).ToList();

            if (on.Count == 0)
            {
                // 未記錄的螢幕維持現狀時，套用後是否有螢幕開著要看當時的狀態（套用規劃會再檢查）
                if (mode.Monitors.Count > 0 && mode.UnlistedMonitors == UnlistedMonitors.Off)
                    errors.Add("至少要開啟一台螢幕");
                return new LayoutReport(errors, warnings);
            }

            List<MonitorSettings> primaries = on.Where(m => m.Primary).ToList();
            if (primaries.Count == 0) errors.Add("沒有主螢幕");
            else if (primaries.Count > 1) errors.Add("主螢幕只能有一台");
            else if (primaries[0].X != 0 || primaries[0].Y != 0) errors.Add("主螢幕「" + Label(primaries[0]) + "」必須位於 (0,0)");

            for (int i = 0; i < on.Count; i++)
                for (int j = i + 1; j < on.Count; j++)
                    if (Overlaps(on[i], on[j]))
                        errors.Add("「" + Label(on[i]) + "」與「" + Label(on[j]) + "」重疊");

            // 空隙：從主螢幕出發，沿著相接的邊（只有角落相接不算）走不到的螢幕
            var reached = new HashSet<MonitorSettings> { primaries.Count > 0 ? primaries[0] : on[0] };
            var queue = new Queue<MonitorSettings>(reached);
            while (queue.Count > 0)
            {
                MonitorSettings current = queue.Dequeue();
                foreach (MonitorSettings next in on)
                    if (!reached.Contains(next) && (Touches(current, next) || Overlaps(current, next)) && reached.Add(next))
                        queue.Enqueue(next);
            }
            List<MonitorSettings> apart = on.Where(m => !reached.Contains(m)).ToList();
            if (apart.Count > 0)
                warnings.Add("螢幕之間有空隙：" + string.Join("、", apart.Select(m => "「" + Label(m) + "」")) + "沒有與主螢幕相連，游標無法移過去。");

            foreach (DetectedMonitor d in connected ?? new DetectedMonitor[0])
            {
                if (mode.Monitors.Exists(m => m.Id == d.Id)) continue;
                MonitorSettings kept = mode.SettingsFor(d);
                if (!kept.Enabled) continue;
                foreach (MonitorSettings m in on.Where(m => Overlaps(m, kept)))
                    warnings.Add("「" + Label(kept) + "」沒有記錄在此模式中，套用時維持現狀，會與「" + Label(m) + "」重疊。");
            }

            return new LayoutReport(errors, warnings);
        }

        /// <summary>兩台螢幕的邊相接，且相接的長度大於 0。</summary>
        static bool Touches(MonitorSettings a, MonitorSettings b)
        {
            bool sideBySide = (a.X + a.DesktopWidth == b.X || b.X + b.DesktopWidth == a.X) &&
                              a.Y < b.Y + b.DesktopHeight && b.Y < a.Y + a.DesktopHeight;
            bool stacked = (a.Y + a.DesktopHeight == b.Y || b.Y + b.DesktopHeight == a.Y) &&
                           a.X < b.X + b.DesktopWidth && b.X < a.X + a.DesktopWidth;
            return sideBySide || stacked;
        }

        internal static bool Overlaps(MonitorSettings a, MonitorSettings b) =>
            a.X < b.X + b.DesktopWidth && b.X < a.X + a.DesktopWidth &&
            a.Y < b.Y + b.DesktopHeight && b.Y < a.Y + a.DesktopHeight;

        /// <summary>訊息中稱呼螢幕的名稱：有名稱用名稱，否則用螢幕識別。</summary>
        public static string Label(MonitorSettings s) => s.Name.Trim().Length > 0 ? s.Name.Trim() : s.Id.ToString();
    }
}
