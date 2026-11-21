using System.Collections.Generic;
using System.Linq;

namespace AutoDisplayMode.Core
{
    /// <summary>套用規劃的結果：要套用的完整螢幕狀態，或拒絕套用的原因。</summary>
    internal sealed record ApplyPlan(IReadOnlyList<MonitorSettings>? States, string? Rejection)
    {
        public static ApplyPlan Reject(string reason) => new(null, reason);
    }

    /// <summary>套用規劃：依模式與目前接上的螢幕，算出每台接上螢幕要套用的狀態。</summary>
    internal static class ApplyPlanner
    {
        /// <summary>
        /// 規則：
        /// <list type="bullet">
        /// <item>模式中記錄但沒接上的螢幕：略過。</item>
        /// <item>接上且有記錄的螢幕：依記錄的螢幕設定。</item>
        /// <item>接上但沒記錄的螢幕：依模式的「未記錄的螢幕」設定維持現狀或關閉。</item>
        /// <item>結果沒有任何螢幕開著：拒絕套用。</item>
        /// <item>主螢幕只有一台且位於 (0,0)：以模式記錄的主螢幕為準（維持現狀的螢幕不搶主螢幕）；
        ///   記錄的主螢幕沒接上或關閉時，改以第一台開啟的螢幕為主螢幕；必要時整體平移座標讓主螢幕回到原點。</item>
        /// </list>
        /// 輸出依偵測順序，每台接上的螢幕一筆。
        /// </summary>
        public static ApplyPlan Plan(ModeConfig mode, IReadOnlyList<DetectedMonitor> detected)
        {
            // 每台接上的螢幕（依偵測順序）：狀態，以及在模式中的位置（沒記錄為 -1）
            var entries = detected
                .Select((d, pos) =>
                {
                    int index = mode.Monitors.FindIndex(m => m.Id == d.Id);
                    MonitorSettings state = index >= 0
                        ? mode.Monitors[index] with { Name = d.Name }
                        : Unlisted(d, mode.UnlistedMonitors);
                    return (State: state, Index: index, Pos: pos);
                })
                .ToList();

            if (!entries.Any(e => e.State.Enabled))
                return ApplyPlan.Reject("套用「" + mode.Name + "」後沒有任何螢幕會開著，已取消切換。");

            // 主螢幕：模式中第一台開啟且標記為主螢幕的；沒有就是模式中第一台開啟的；再沒有就是第一台維持開啟的
            var enabled = entries.Where(e => e.State.Enabled).ToList();
            var recorded = enabled.Where(e => e.Index >= 0).OrderBy(e => e.Index).ToList();
            int primary = recorded.Where(e => e.State.Primary).Concat(recorded).Concat(enabled).First().Pos;
            int dx = entries[primary].State.X, dy = entries[primary].State.Y;

            var states = entries
                .Select((e, i) => e.State.Enabled
                    ? e.State with { X = e.State.X - dx, Y = e.State.Y - dy, Primary = i == primary }
                    : e.State with { Primary = false })
                .ToList();
            return new ApplyPlan(states, null);
        }

        /// <summary>接上但模式沒有記錄的螢幕，套用時的狀態。</summary>
        internal static MonitorSettings Unlisted(DetectedMonitor d, UnlistedMonitors policy)
        {
            VideoMode mode = d.Current ?? d.Native ?? new VideoMode(0, 0, 0);
            bool on = d.IsActive && policy == UnlistedMonitors.Keep;
            return new MonitorSettings
            {
                Id = d.Id,
                Name = d.Name,
                Enabled = on,
                X = on ? d.X : 0,
                Y = on ? d.Y : 0,
                Width = mode.Width,
                Height = mode.Height,
                RefreshRate = mode.RefreshRate,
                Orientation = on ? d.Orientation : Orientation.Landscape,
                Primary = on && d.IsPrimary,
            };
        }
    }
}
