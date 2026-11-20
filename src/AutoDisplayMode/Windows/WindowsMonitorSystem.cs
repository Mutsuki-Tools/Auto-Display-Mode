using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using AutoDisplayMode.Core;
using Microsoft.Win32;

namespace AutoDisplayMode.Windows
{
    /// <summary>
    /// 螢幕系統的 Windows 實作（ADR-0002）：以 QueryDisplayConfig 列出接上的螢幕，
    /// DisplayConfigGetDeviceInfo 取得名稱、接孔與原生解析度，EnumDisplaySettings 取得目前狀態與支援的顯示模式，
    /// 並從登錄檔讀取 EDID 取得廠牌、型號與序號（ADR-0003）。
    /// </summary>
    sealed class WindowsMonitorSystem : IMonitorSystem
    {
        public IReadOnlyList<DetectedMonitor> Detect()
        {
            DISPLAYCONFIG_PATH_INFO[] paths = QueryAllPaths();

            // QDC_ALL_PATHS 會列出每個「來源 × 螢幕」的可能組合；同一台螢幕只取一條，開啟中的路徑優先
            var targets = paths
                .Where(p => p.targetInfo.targetAvailable != 0)
                .GroupBy(p => (p.targetInfo.adapterId.LowPart, p.targetInfo.adapterId.HighPart, p.targetInfo.id))
                .Select(g => g.OrderByDescending(p => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0).First());

            var result = new List<(DetectedMonitor Monitor, uint TargetId)>();
            foreach (DISPLAYCONFIG_PATH_INFO path in targets)
            {
                DetectedMonitor? m = Describe(path);
                if (m != null) result.Add((m, path.targetInfo.id));
            }

            // 沒有序號又剛好同型號、同接孔名稱（例如兩張顯示卡各一個 DP1，或 DP 串接）時，識別會相同：
            // 在接孔後面加上目標 ID 區分，避免其中一台被當成另一台
            return result
                .GroupBy(r => r.Monitor.Id)
                .SelectMany(g => g.Count() == 1 || g.Key.Serial != null
                    ? g.Select(r => r.Monitor)
                    : g.Select(r => r.Monitor with { Connector = r.Monitor.Connector + "-" + r.TargetId }))
                .ToList();
        }

        static DetectedMonitor? Describe(DISPLAYCONFIG_PATH_INFO path)
        {
            var target = path.targetInfo;
            var name = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            name.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            name.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_TARGET_DEVICE_NAME));
            name.header.adapterId = target.adapterId;
            name.header.id = target.id;
            if (DisplayConfigGetDeviceInfo(ref name) != 0) return null;

            // 廠牌、型號、序號：優先用 EDID；讀不到時用 DisplayConfig 提供的廠牌與型號
            Edid? edid = Edid.ReadFromRegistry(name.monitorDevicePath);
            string manufacturer = edid?.Manufacturer ?? DecodeManufacturer(name.edidManufactureId);
            string product = edid?.ProductCode ?? name.edidProductCodeId.ToString("X4");
            string connector = ConnectorName(name.outputTechnology) + name.connectorInstance;
            string friendly = name.monitorFriendlyDeviceName;
            if (string.IsNullOrWhiteSpace(friendly)) friendly = manufacturer + " " + product;

            bool active = (path.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0;
            var monitor = new DetectedMonitor
            {
                Manufacturer = manufacturer,
                ProductCode = product,
                Serial = edid?.Serial,
                Connector = connector,
                Name = friendly,
                IsActive = active,
                Native = PreferredMode(target),
            };

            if (!active) return monitor with { SupportedModes = monitor.Native == null ? Array.Empty<VideoMode>() : new[] { monitor.Native } };

            // 開啟中的螢幕讀不到目前設定時整個偵測失敗，避免擷取時記錄錯誤的座標與解析度
            string gdi = SourceGdiName(path.sourceInfo)
                ?? throw new InvalidOperationException("無法取得「" + friendly + "」的顯示來源");
            var dm = NewDevMode();
            if (!EnumDisplaySettings(gdi, ENUM_CURRENT_SETTINGS, ref dm))
                throw new InvalidOperationException("無法讀取「" + friendly + "」目前的顯示設定");
            Orientation orientation = ToOrientation(dm.dmDisplayOrientation);
            return monitor with
            {
                X = dm.dmPositionX,
                Y = dm.dmPositionY,
                Current = Unrotated(dm),
                Orientation = orientation,
                IsPrimary = dm.dmPositionX == 0 && dm.dmPositionY == 0,
                SupportedModes = SupportedModes(gdi),
            };
        }

        // ------------------------------------------------------------
        //  QueryDisplayConfig / DisplayConfigGetDeviceInfo
        // ------------------------------------------------------------

        static DISPLAYCONFIG_PATH_INFO[] QueryAllPaths()
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                int err = GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out uint pathCount, out uint modeCount);
                if (err != 0) throw new Win32Exception(err);
                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                err = QueryDisplayConfig(QDC_ALL_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (err == ERROR_INSUFFICIENT_BUFFER) continue;   // 螢幕在兩次呼叫之間改變，重試
                if (err != 0) throw new Win32Exception(err);
                Array.Resize(ref paths, (int)pathCount);
                return paths;
            }
            throw new Win32Exception(ERROR_INSUFFICIENT_BUFFER);
        }

        static string? SourceGdiName(DISPLAYCONFIG_PATH_SOURCE_INFO source)
        {
            var name = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            name.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            name.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME));
            name.header.adapterId = source.adapterId;
            name.header.id = source.id;
            return DisplayConfigGetDeviceInfo(ref name) == 0 ? name.viewGdiDeviceName : null;
        }

        static VideoMode? PreferredMode(DISPLAYCONFIG_PATH_TARGET_INFO target)
        {
            var pref = new DISPLAYCONFIG_TARGET_PREFERRED_MODE();
            pref.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE;
            pref.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_TARGET_PREFERRED_MODE));
            pref.header.adapterId = target.adapterId;
            pref.header.id = target.id;
            if (DisplayConfigGetDeviceInfo(ref pref) != 0 || pref.width == 0 || pref.height == 0) return null;
            var v = pref.targetMode.targetVideoSignalInfo.vSyncFreq;
            int hz = v.Denominator == 0 ? 0 : (int)Math.Round((double)v.Numerator / v.Denominator);
            return new VideoMode((int)pref.width, (int)pref.height, hz);
        }

        // ------------------------------------------------------------
        //  EnumDisplaySettings
        // ------------------------------------------------------------

        static IReadOnlyList<VideoMode> SupportedModes(string gdi)
        {
            var modes = new HashSet<VideoMode>();
            var dm = NewDevMode();
            for (int i = 0; EnumDisplaySettings(gdi, i, ref dm); i++)
            {
                if (dm.dmPelsWidth == 0 || dm.dmPelsHeight == 0) continue;
                modes.Add(Unrotated(dm));
            }
            return modes.OrderByDescending(m => m.Width).ThenByDescending(m => m.Height).ThenByDescending(m => m.RefreshRate).ToArray();
        }

        /// <summary>直向時 DEVMODE 的寬高是旋轉後的值，轉回未旋轉的解析度。</summary>
        static VideoMode Unrotated(DEVMODE dm)
        {
            bool rotated = dm.dmDisplayOrientation == DMDO_90 || dm.dmDisplayOrientation == DMDO_270;
            int w = (int)dm.dmPelsWidth, h = (int)dm.dmPelsHeight;
            return rotated ? new VideoMode(h, w, (int)dm.dmDisplayFrequency) : new VideoMode(w, h, (int)dm.dmDisplayFrequency);
        }

        static Orientation ToOrientation(uint dmdo) => dmdo switch
        {
            DMDO_90 => Orientation.Portrait,
            DMDO_180 => Orientation.LandscapeFlipped,
            DMDO_270 => Orientation.PortraitFlipped,
            _ => Orientation.Landscape,
        };

        static DEVMODE NewDevMode() => new() { dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE)) };

        // ------------------------------------------------------------
        //  名稱
        // ------------------------------------------------------------

        static string ConnectorName(uint outputTechnology) => outputTechnology switch
        {
            0 => "VGA",
            1 => "SVIDEO",
            2 => "COMPOSITE",
            3 => "COMPONENT",
            4 => "DVI",
            5 => "HDMI",
            6 => "LVDS",
            8 => "DJPN",
            9 => "SDI",
            10 => "DP",
            11 => "EDP",
            12 => "UDI",
            13 => "UDIE",
            14 => "SDTV",
            15 => "MIRACAST",
            16 => "INDIRECT",
            17 => "INDIRECT",
            0x80000000 => "INTERNAL",
            _ => "OUT" + outputTechnology,
        };

        /// <summary>DisplayConfig 的廠牌代碼是 EDID 第 8~9 位元組以小端序讀出的值：先換回大端序再解碼。</summary>
        static string DecodeManufacturer(ushort raw) => ManufacturerLetters(((raw & 0xFF) << 8) | (raw >> 8));

        /// <summary>EDID 廠牌代碼（大端序 16 位元）：三個 5 位元的字母，1 = A。</summary>
        static string ManufacturerLetters(int v) => new(new[]
        {
            (char)('A' - 1 + ((v >> 10) & 0x1F)),
            (char)('A' - 1 + ((v >> 5) & 0x1F)),
            (char)('A' - 1 + (v & 0x1F)),
        });

        // ------------------------------------------------------------
        //  EDID
        // ------------------------------------------------------------

        sealed class Edid
        {
            Edid(string manufacturer, string productCode, string? serial)
            {
                Manufacturer = manufacturer;
                ProductCode = productCode;
                Serial = serial;
            }

            public string Manufacturer { get; }
            public string ProductCode { get; }
            public string? Serial { get; }

            /// <summary>
            /// 由螢幕裝置路徑（\\?\DISPLAY#DEL4123#5&amp;...&amp;UID4352#{...}）找到
            /// HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\DEL4123\5&amp;...\Device Parameters\EDID。
            /// </summary>
            public static Edid? ReadFromRegistry(string devicePath)
            {
                try
                {
                    string[] parts = devicePath.Split('#');
                    if (parts.Length < 3) return null;
                    string key = @"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + parts[1] + @"\" + parts[2] + @"\Device Parameters";
                    using RegistryKey? k = Registry.LocalMachine.OpenSubKey(key, false);
                    return k?.GetValue("EDID") is byte[] bytes ? Parse(bytes) : null;
                }
                catch
                {
                    return null;
                }
            }

            static Edid? Parse(byte[] b)
            {
                if (b.Length < 128) return null;
                string manufacturer = ManufacturerLetters((b[8] << 8) | b[9]);
                string product = (b[10] | (b[11] << 8)).ToString("X4");
                return new Edid(manufacturer, product, SerialText(b) ?? SerialNumber(b));
            }

            /// <summary>顯示描述區塊中標籤 0xFF 的序號字串。</summary>
            static string? SerialText(byte[] b)
            {
                for (int offset = 54; offset <= 108; offset += 18)
                {
                    if (b[offset] != 0 || b[offset + 1] != 0 || b[offset + 3] != 0xFF) continue;
                    var sb = new StringBuilder();
                    for (int i = offset + 5; i < offset + 18 && b[i] != 0x0A && b[i] != 0; i++)
                        if (b[i] >= 0x20 && b[i] < 0x7F) sb.Append((char)b[i]);
                    string s = sb.ToString().Trim();
                    if (s.Length > 0) return s;
                }
                return null;
            }

            /// <summary>第 12~15 位元組的數字序號；0 或全部相同的填充值視為沒有序號。</summary>
            static string? SerialNumber(byte[] b)
            {
                uint n = (uint)(b[12] | (b[13] << 8) | (b[14] << 16) | (b[15] << 24));
                return n == 0 || n == 0x01010101 || n == 0xFFFFFFFF ? null : n.ToString();
            }
        }

        // ------------------------------------------------------------
        //  Win32
        // ------------------------------------------------------------

        const uint QDC_ALL_PATHS = 0x1;
        const uint DISPLAYCONFIG_PATH_ACTIVE = 0x1;
        const int ERROR_INSUFFICIENT_BUFFER = 122;
        const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
        const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE = 3;
        const int ENUM_CURRENT_SETTINGS = -1;
        const uint DMDO_90 = 1;
        const uint DMDO_180 = 2;
        const uint DMDO_270 = 3;

        [StructLayout(LayoutKind.Sequential)]
        struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DISPLAYCONFIG_2DREGION
        {
            public uint cx;
            public uint cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
        {
            public ulong pixelRate;
            public DISPLAYCONFIG_RATIONAL hSyncFreq;
            public DISPLAYCONFIG_RATIONAL vSyncFreq;
            public DISPLAYCONFIG_2DREGION activeSize;
            public DISPLAYCONFIG_2DREGION totalSize;
            public uint videoStandard;
            public uint scanLineOrdering;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DISPLAYCONFIG_TARGET_MODE
        {
            public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
        }

        /// <summary>只用來接收 QueryDisplayConfig 的模式陣列；內容（聯集）不讀取。</summary>
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        struct DISPLAYCONFIG_MODE_INFO
        {
            [FieldOffset(0)] public uint infoType;
            [FieldOffset(4)] public uint id;
            [FieldOffset(8)] public LUID adapterId;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public int type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DISPLAYCONFIG_TARGET_PREFERRED_MODE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint width;
            public uint height;
            public DISPLAYCONFIG_TARGET_MODE targetMode;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [DllImport("user32.dll")]
        static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [DllImport("user32.dll")]
        static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_PREFERRED_MODE requestPacket);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
        static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    }
}
