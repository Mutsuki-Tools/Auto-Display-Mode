using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoDisplayMode.Core
{
    /// <summary>螢幕方向。</summary>
    public enum Orientation
    {
        /// <summary>橫向。</summary>
        Landscape,

        /// <summary>直向。</summary>
        Portrait,

        /// <summary>橫向（翻轉）。</summary>
        LandscapeFlipped,

        /// <summary>直向（翻轉）。</summary>
        PortraitFlipped,
    }

    /// <summary>一組解析度與更新率。寬高一律為未旋轉（橫向）時的值。</summary>
    public sealed record VideoMode(int Width, int Height, int RefreshRate);

    /// <summary>
    /// 螢幕識別（ADR-0003）：「廠牌 + 型號 + 序號」；讀不到序號時改用「廠牌 + 型號 + 接孔」。
    /// 文字形式為 <c>廠牌/型號/SN:序號</c> 或 <c>廠牌/型號/PORT:接孔</c>，存在 cfg 中。
    /// </summary>
    public sealed record MonitorId
    {
        const string SerialPrefix = "SN:";
        const string ConnectorPrefix = "PORT:";

        MonitorId(string manufacturer, string productCode, string? serial, string? connector)
        {
            Manufacturer = manufacturer;
            ProductCode = productCode;
            Serial = serial;
            Connector = connector;
        }

        /// <summary>廠牌（EDID 的三字母廠商代碼，例如 DEL）。</summary>
        public string Manufacturer { get; }

        /// <summary>型號（EDID 的產品代碼，四位十六進位）。</summary>
        public string ProductCode { get; }

        /// <summary>序號；以接孔辨識時為 null。</summary>
        public string? Serial { get; }

        /// <summary>接孔；以序號辨識時為 null。</summary>
        public string? Connector { get; }

        /// <summary>依 ADR-0003 組成螢幕識別：有序號用序號，否則用接孔。</summary>
        public static MonitorId Create(string manufacturer, string productCode, string? serial, string connector)
        {
            string m = manufacturer.Trim().ToUpperInvariant();
            string p = productCode.Trim().ToUpperInvariant();
            string? s = string.IsNullOrWhiteSpace(serial) ? null : serial!.Trim();
            return s != null ? new MonitorId(m, p, s, null) : new MonitorId(m, p, null, connector.Trim());
        }

        /// <summary>解析 cfg 中的文字形式；格式不符時回傳 null。</summary>
        public static MonitorId? Parse(string text)
        {
            string[] parts = text.Trim().Split(new[] { '/' }, 3);
            if (parts.Length != 3) return null;
            string manufacturer = parts[0].Trim().ToUpperInvariant();
            string product = parts[1].Trim().ToUpperInvariant();
            if (manufacturer.Length == 0 || product.Length == 0) return null;
            string last = parts[2];
            if (last.StartsWith(SerialPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string serial = last.Substring(SerialPrefix.Length).Trim();
                return serial.Length == 0 ? null : new MonitorId(manufacturer, product, serial, null);
            }
            if (last.StartsWith(ConnectorPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string connector = last.Substring(ConnectorPrefix.Length).Trim();
                return connector.Length == 0 ? null : new MonitorId(manufacturer, product, null, connector);
            }
            return null;
        }

        public override string ToString() =>
            Manufacturer + "/" + ProductCode + "/" + (Serial != null ? SerialPrefix + Serial : ConnectorPrefix + Connector);
    }

    /// <summary>從螢幕系統偵測到的一台接上的螢幕。</summary>
    public sealed record DetectedMonitor
    {
        /// <summary>廠牌（EDID 三字母廠商代碼）。</summary>
        public string Manufacturer { get; init; } = "";

        /// <summary>型號（EDID 產品代碼，四位十六進位）。</summary>
        public string ProductCode { get; init; } = "";

        /// <summary>EDID 序號；讀不到時為 null。</summary>
        public string? Serial { get; init; }

        /// <summary>接孔（例如 HDMI1、DP2）。</summary>
        public string Connector { get; init; } = "";

        /// <summary>螢幕名稱（給人看的，例如 DELL U2720Q）。</summary>
        public string Name { get; init; } = "";

        /// <summary>目前是否開啟（Windows 中有延伸或複製桌面到這台螢幕）。</summary>
        public bool IsActive { get; init; }

        /// <summary>目前的座標（開啟時才有意義）。</summary>
        public int X { get; init; }

        public int Y { get; init; }

        /// <summary>目前的解析度與更新率（未旋轉的寬高；開啟時才有意義）。</summary>
        public VideoMode? Current { get; init; }

        public Orientation Orientation { get; init; }

        public bool IsPrimary { get; init; }

        /// <summary>原生解析度（螢幕建議的顯示模式）；讀不到時為 null。</summary>
        public VideoMode? Native { get; init; }

        /// <summary>支援的解析度與更新率組合。</summary>
        public IReadOnlyList<VideoMode> SupportedModes { get; init; } = Array.Empty<VideoMode>();

        /// <summary>螢幕識別（ADR-0003）。</summary>
        public MonitorId Id => MonitorId.Create(Manufacturer, ProductCode, Serial, Connector);
    }

    /// <summary>螢幕設定：某個模式中單一螢幕的狀態。</summary>
    public sealed record MonitorSettings
    {
        public MonitorId Id { get; init; } = MonitorId.Create("", "", null, "");

        /// <summary>螢幕名稱（僅供辨識，不影響比對）。</summary>
        public string Name { get; init; } = "";

        /// <summary>開啟或關閉。</summary>
        public bool Enabled { get; init; }

        public int X { get; init; }
        public int Y { get; init; }

        /// <summary>解析度寬度（未旋轉）。</summary>
        public int Width { get; init; }

        /// <summary>解析度高度（未旋轉）。</summary>
        public int Height { get; init; }

        /// <summary>更新率（Hz）。</summary>
        public int RefreshRate { get; init; }

        public Orientation Orientation { get; init; }

        /// <summary>主螢幕。</summary>
        public bool Primary { get; init; }

        /// <summary>直向（含翻轉）：桌面上的寬高是解析度對調。</summary>
        public bool IsPortrait => Orientation is Orientation.Portrait or Orientation.PortraitFlipped;

        /// <summary>在桌面上佔的寬度（依方向對調）。</summary>
        public int DesktopWidth => IsPortrait ? Height : Width;

        /// <summary>在桌面上佔的高度（依方向對調）。</summary>
        public int DesktopHeight => IsPortrait ? Width : Height;
    }

    /// <summary>套用模式時，接上但模式中沒有記錄的螢幕要怎麼處理。</summary>
    public enum UnlistedMonitors
    {
        /// <summary>維持現狀（預設）。</summary>
        Keep,

        /// <summary>關閉。</summary>
        Off,
    }

    /// <summary>手動切換進入模式時，自動切換要怎麼處理。</summary>
    public enum ManualAutoSwitch
    {
        /// <summary>維持（預設）。</summary>
        Keep,

        /// <summary>開啟。</summary>
        On,

        /// <summary>關閉。</summary>
        Off,
    }

    /// <summary>模式：一組有名稱的螢幕設定。以內部 ID 識別，名稱可改。</summary>
    public sealed class ModeConfig
    {
        /// <summary><see cref="Color"/> 尚未指定。</summary>
        public const int NoColor = -1;

        /// <summary>新模式依序選用的工作列圖示顏色（0xRRGGBB）：選第一個沒被其他模式使用的。</summary>
        public static readonly IReadOnlyList<int> Palette = new[]
        {
            0xEB8232, // 橘
            0x3CAA5A, // 綠
            0x3C82DC, // 藍
            0xD24B4B, // 紅
            0x9B5AD2, // 紫
            0xD2B432, // 黃
            0x32B4B4, // 青
            0x8C8C8C, // 灰
        };

        public ModeConfig(int id, string name)
        {
            Id = id;
            Name = name;
        }

        public int Id { get; }

        public string Name { get; set; }

        /// <summary>工作列圖示顏色（0xRRGGBB）；<see cref="NoColor"/> 表示尚未指定。</summary>
        public int Color { get; set; } = NoColor;

        /// <summary>手動切換進入此模式時，自動切換要維持、開啟或關閉。</summary>
        public ManualAutoSwitch ManualAutoSwitch { get; set; }

        /// <summary>套用此模式時，接上但沒記錄的螢幕要維持現狀或關閉。</summary>
        public UnlistedMonitors UnlistedMonitors { get; set; }

        /// <summary>每台螢幕的螢幕設定（依 cfg 中的順序）。</summary>
        public List<MonitorSettings> Monitors { get; } = new();

        /// <summary>
        /// 螢幕配置分頁顯示的螢幕設定：此模式記錄的（名稱換成偵測到的名稱）；
        /// 沒有記錄時為套用此模式時它會有的狀態（依「未記錄的螢幕」維持現狀或關閉；維持現狀的螢幕不搶主螢幕）。
        /// </summary>
        public MonitorSettings SettingsFor(DetectedMonitor d)
        {
            MonitorSettings? s = Monitors.Find(m => m.Id == d.Id);
            return s != null ? s with { Name = d.Name } : ApplyPlanner.Unlisted(d, UnlistedMonitors) with { Primary = false };
        }

        /// <summary>
        /// 以 <paramref name="s"/> 取代同一台螢幕的螢幕設定（沒有記錄時加入），再維持配置的規則：
        /// 主螢幕只有一台且是開啟的（設為主螢幕的取代原本的；沒有時由第一台開啟的螢幕擔任），
        /// 並平移開啟的螢幕讓主螢幕位於 (0,0)（所以移動主螢幕等於反向移動其他螢幕）。關閉的螢幕座標不變。
        /// </summary>
        public void UpdateMonitor(MonitorSettings s)
        {
            int index = Monitors.FindIndex(m => m.Id == s.Id);
            if (index >= 0) Monitors[index] = s;
            else Monitors.Add(s);

            for (int i = 0; i < Monitors.Count; i++)
            {
                MonitorSettings m = Monitors[i];
                bool primary = m.Enabled && m.Primary && (m.Id == s.Id || !(s.Enabled && s.Primary));
                if (primary != m.Primary) Monitors[i] = m with { Primary = primary };
            }

            int p = Monitors.FindIndex(m => m.Enabled && m.Primary);
            if (p < 0) p = Monitors.FindIndex(m => m.Enabled);
            if (p < 0) return;
            int dx = Monitors[p].X, dy = Monitors[p].Y;
            for (int i = 0; i < Monitors.Count; i++)
            {
                MonitorSettings m = Monitors[i];
                if (m.Enabled) Monitors[i] = m with { X = m.X - dx, Y = m.Y - dy, Primary = i == p };
            }
        }

        /// <summary>
        /// 開啟或關閉一台螢幕（<paramref name="shown"/> 是目前顯示的螢幕設定，見 <see cref="SettingsFor"/>）。
        /// 開啟原本關閉的螢幕時，若會與其他開啟的螢幕重疊，就移到最右邊（與主螢幕上緣對齊）。
        /// </summary>
        public void SetEnabled(MonitorSettings shown, bool on)
        {
            MonitorSettings s = shown with { Enabled = on };
            if (on && !shown.Enabled)
            {
                List<MonitorSettings> others = Monitors.FindAll(m => m.Enabled && m.Id != s.Id);
                if (others.Exists(m => MonitorLayout.Overlaps(m, s)))
                    s = s with { X = others.Max(m => m.X + m.DesktopWidth), Y = 0 };
            }
            UpdateMonitor(s);
        }

        /// <summary>深複製，保留 ID（螢幕設定是不可變的 record，複製清單即可）。</summary>
        public ModeConfig Clone() => CopyAs(Id, Name);

        /// <summary>以新的 ID 與名稱深複製，其餘設定與螢幕設定相同（清單各自獨立）。</summary>
        public ModeConfig CopyAs(int id, string name)
        {
            var m = new ModeConfig(id, name) { Color = Color, ManualAutoSwitch = ManualAutoSwitch, UnlistedMonitors = UnlistedMonitors };
            m.Monitors.AddRange(Monitors);
            return m;
        }

        /// <summary>為尚未指定顏色的模式依序配上沒被其他模式使用的顏色（全部用過就從頭循環）。</summary>
        public static void AssignMissingColors(IReadOnlyList<ModeConfig> modes)
        {
            var used = new HashSet<int>();
            foreach (ModeConfig m in modes)
                if (m.Color != NoColor) used.Add(m.Color);
            int next = 0;
            foreach (ModeConfig m in modes)
            {
                if (m.Color != NoColor) continue;
                int? free = null;
                foreach (int c in Palette)
                    if (!used.Contains(c)) { free = c; break; }
                m.Color = free ?? Palette[next++ % Palette.Count];
                used.Add(m.Color);
            }
        }
    }
}
