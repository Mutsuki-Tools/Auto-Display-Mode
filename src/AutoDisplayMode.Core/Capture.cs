using System.Collections.Generic;
using System.Linq;

namespace AutoDisplayMode.Core
{
    /// <summary>擷取：把目前實際的螢幕狀態轉成模式的螢幕設定。</summary>
    internal static class Capture
    {
        /// <summary>
        /// 依目前偵測到的螢幕更新 <paramref name="existing"/>，回傳新的螢幕設定集合：
        /// <list type="bullet">
        /// <item>接上且開啟的螢幕：記錄開啟、座標、解析度、更新率、方向與主螢幕。</item>
        /// <item>接上但關閉的螢幕：記錄為關閉，保留原有的解析度與更新率（沒有就用原生解析度）。</item>
        /// <item>記錄過但沒接上的螢幕：保持不變。</item>
        /// </list>
        /// 原有的螢幕維持原本順序，新的螢幕依偵測順序附加在後面。
        /// </summary>
        public static List<MonitorSettings> Run(IReadOnlyList<DetectedMonitor> detected, IReadOnlyList<MonitorSettings> existing)
        {
            var result = existing.ToList();
            foreach (DetectedMonitor d in detected)
            {
                MonitorId id = d.Id;
                int index = result.FindIndex(m => m.Id == id);
                MonitorSettings? old = index >= 0 ? result[index] : null;
                MonitorSettings captured = d.IsActive ? FromActive(d) : FromInactive(d, old);
                if (index >= 0) result[index] = captured;
                else result.Add(captured);
            }
            return result;
        }

        static MonitorSettings FromActive(DetectedMonitor d)
        {
            VideoMode mode = d.Current ?? d.Native ?? new VideoMode(0, 0, 0);
            return new MonitorSettings
            {
                Id = d.Id,
                Name = d.Name,
                Enabled = true,
                X = d.X,
                Y = d.Y,
                Width = mode.Width,
                Height = mode.Height,
                RefreshRate = mode.RefreshRate,
                Orientation = d.Orientation,
                Primary = d.IsPrimary,
            };
        }

        static MonitorSettings FromInactive(DetectedMonitor d, MonitorSettings? old)
        {
            if (old != null) return old with { Name = d.Name, Enabled = false, Primary = false };

            VideoMode mode = d.Native
                ?? d.SupportedModes.OrderByDescending(m => m.Width * m.Height).ThenByDescending(m => m.RefreshRate).FirstOrDefault()
                ?? new VideoMode(0, 0, 0);
            return new MonitorSettings
            {
                Id = d.Id,
                Name = d.Name,
                Enabled = false,
                Width = mode.Width,
                Height = mode.Height,
                RefreshRate = mode.RefreshRate,
                Orientation = Orientation.Landscape,
            };
        }
    }
}
