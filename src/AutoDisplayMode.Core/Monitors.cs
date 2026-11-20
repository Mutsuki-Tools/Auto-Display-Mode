using System;
using System.Collections.Generic;

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
    }

    /// <summary>模式：一組有名稱的螢幕設定。以內部 ID 識別，名稱可改。</summary>
    public sealed class ModeConfig
    {
        public ModeConfig(int id, string name)
        {
            Id = id;
            Name = name;
        }

        public int Id { get; }

        public string Name { get; set; }

        /// <summary>每台螢幕的螢幕設定（依 cfg 中的順序）。</summary>
        public List<MonitorSettings> Monitors { get; } = new();
    }
}
