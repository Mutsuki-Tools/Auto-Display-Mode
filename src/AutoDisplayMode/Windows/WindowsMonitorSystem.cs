using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using AutoDisplayMode.Core;
using Microsoft.Win32;

namespace AutoDisplayMode.Windows
{
    /// <summary>
    /// 螢幕系統的 Windows 實作（ADR-0002）：以 QueryDisplayConfig 列出接上的螢幕，
    /// DisplayConfigGetDeviceInfo 取得名稱、接孔與原生解析度，EnumDisplaySettings 取得目前狀態與支援的顯示模式，
    /// 並從登錄檔讀取 EDID 取得廠牌、型號與序號（ADR-0003）。
    /// 套用時先以 SetDisplayConfig 開關螢幕，再以 ChangeDisplaySettingsEx 設定解析度、更新率、方向、座標與主螢幕。
    /// </summary>
    sealed class WindowsMonitorSystem : IMonitorSystem, IDisposable
    {
        readonly SynchronizationContext ui;
        readonly ChangeWatcher watcher;

        /// <summary>必須在 UI 執行緒建立（插拔通知的隱藏視窗屬於建立它的執行緒）。</summary>
        public WindowsMonitorSystem(SynchronizationContext ui)
        {
            this.ui = ui;
            watcher = new ChangeWatcher(() => Changed?.Invoke());
        }

        /// <summary>套用結束（在 UI 執行緒觸發）；參數為錯誤訊息，成功時為 null。</summary>
        public event Action<string?>? ApplyFinished;

        public event Action? Changed;

        public void Dispose() => watcher.Dispose();

        /// <summary>
        /// 監聽螢幕插拔與顯示設定改變：隱藏的頂層視窗（訊息專用視窗收不到廣播）接收
        /// WM_DISPLAYCHANGE（桌面解析度或開啟的螢幕改變）與 WM_DEVICECHANGE 的 DBT_DEVNODES_CHANGED
        /// （裝置增減，涵蓋插拔目前關閉中的螢幕）。後者任何裝置都會觸發，且一次插拔會連續收到好幾個，
        /// 所以合併 0.5 秒內的變化成一次通知。
        /// </summary>
        sealed class ChangeWatcher : System.Windows.Forms.NativeWindow, IDisposable
        {
            const int WM_DISPLAYCHANGE = 0x007E;
            const int WM_DEVICECHANGE = 0x0219;
            const int DBT_DEVNODES_CHANGED = 0x0007;

            readonly System.Windows.Forms.Timer debounce = new() { Interval = 500 };

            public ChangeWatcher(Action changed)
            {
                debounce.Tick += delegate
                {
                    debounce.Stop();
                    changed();
                };
                CreateHandle(new System.Windows.Forms.CreateParams());
            }

            protected override void WndProc(ref System.Windows.Forms.Message m)
            {
                if (m.Msg == WM_DISPLAYCHANGE || (m.Msg == WM_DEVICECHANGE && m.WParam.ToInt64() == DBT_DEVNODES_CHANGED))
                {
                    debounce.Stop();
                    debounce.Start();
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                debounce.Dispose();
                DestroyHandle();
            }
        }

        public IReadOnlyList<DetectedMonitor> Detect() => DetectTargets(Query(QDC_ALL_PATHS).Paths).Select(t => t.Monitor).ToList();

        /// <summary>
        /// 列出接上的每台螢幕與代表它的路徑（開啟中的路徑優先）。
        /// QDC_ALL_PATHS 會列出每個「來源 × 螢幕」的可能組合，同一台螢幕只取一條。
        /// </summary>
        static List<(DetectedMonitor Monitor, DISPLAYCONFIG_PATH_INFO Path)> DetectTargets(DISPLAYCONFIG_PATH_INFO[] paths)
        {
            var targets = paths
                .Where(p => p.targetInfo.targetAvailable != 0)
                .GroupBy(TargetKey)
                .Select(g => g.OrderByDescending(IsActive).First());

            var result = new List<(DetectedMonitor Monitor, DISPLAYCONFIG_PATH_INFO Path)>();
            foreach (DISPLAYCONFIG_PATH_INFO path in targets)
            {
                DetectedMonitor? m = Describe(path);
                if (m != null) result.Add((m, path));
            }

            // 沒有序號又剛好同型號、同接孔名稱（例如兩張顯示卡各一個 DP1，或 DP 串接）時，識別會相同：
            // 在接孔後面加上目標 ID 區分，避免其中一台被當成另一台
            return result
                .GroupBy(r => r.Monitor.Id)
                .SelectMany(g => g.Count() == 1 || g.Key.Serial != null
                    ? g
                    : g.Select(r => (r.Monitor with { Connector = r.Monitor.Connector + "-" + r.Path.targetInfo.id }, r.Path)))
                .ToList();
        }

        static (uint, int, uint) TargetKey(DISPLAYCONFIG_PATH_INFO p) =>
            (p.targetInfo.adapterId.LowPart, p.targetInfo.adapterId.HighPart, p.targetInfo.id);

        static (uint, int, uint) SourceKey(DISPLAYCONFIG_PATH_INFO p) =>
            (p.sourceInfo.adapterId.LowPart, p.sourceInfo.adapterId.HighPart, p.sourceInfo.id);

        static bool IsActive(DISPLAYCONFIG_PATH_INFO p) => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0;

        // ------------------------------------------------------------
        //  套用
        // ------------------------------------------------------------

        public void BeginApply(IReadOnlyList<MonitorSettings> states)
        {
            MonitorSettings[] copy = states.ToArray();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string? error = null;
                try
                {
                    Apply(copy);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                ui.Post(_ => ApplyFinished?.Invoke(error), null);
            });
        }

        /// <summary>
        /// 套用 <paramref name="states"/>。中途失敗且螢幕已被改動時，盡量還原為套用前的狀態
        /// （同時蓋掉已暫存在登錄檔的設定），讓程式記錄的模式與實際狀態一致。
        /// </summary>
        static void Apply(MonitorSettings[] states)
        {
            MonitorSettings[] before = Snapshot(DetectTargets(Query(QDC_ALL_PATHS).Paths));
            bool changed = false;
            try
            {
                ApplyCore(states, ref changed);
            }
            catch (Exception ex) when (changed)
            {
                string note;
                try
                {
                    bool ignored = false;
                    ApplyCore(before, ref ignored);
                    note = "已還原為切換前的螢幕狀態。";
                }
                catch (Exception rollback)
                {
                    note = "還原切換前的螢幕狀態也失敗：" + rollback.Message;
                }
                throw new InvalidOperationException(ex.Message + "\n" + note, ex);
            }
        }

        /// <summary><paramref name="changed"/>：是否已改動螢幕（開關，或已暫存任何一台的顯示設定）。</summary>
        static void ApplyCore(MonitorSettings[] states, ref bool changed)
        {
            // 1. 開關：開啟中的螢幕與要求的不同時，以 SetDisplayConfig 調整
            var (paths, modes) = Query(QDC_ALL_PATHS);
            var targets = DetectTargets(paths);
            var wanted = new HashSet<(uint, int, uint)>();
            foreach (MonitorSettings s in states.Where(s => s.Enabled))
                wanted.Add(TargetKey(FindTarget(targets, s).Path));
            var active = new HashSet<(uint, int, uint)>(paths.Where(IsActive).Select(TargetKey));
            if (!wanted.SetEquals(active))
            {
                changed = true;
                SetActiveTargets(paths, modes, wanted, targets);
            }

            // 2. 解析度、更新率、方向、座標、主螢幕（主螢幕先設定）
            var activePaths = WaitForActive(wanted);
            foreach (MonitorSettings s in states.Where(s => s.Enabled).OrderByDescending(s => s.Primary))
            {
                var key = TargetKey(FindTarget(targets, s).Path);
                DISPLAYCONFIG_PATH_INFO? path = activePaths.Where(p => TargetKey(p).Equals(key)).Cast<DISPLAYCONFIG_PATH_INFO?>().FirstOrDefault();
                string gdi = (path == null ? null : SourceGdiName(path.Value.sourceInfo))
                    ?? throw new InvalidOperationException("無法開啟「" + s.Name + "」");
                changed = true;
                SetMode(gdi, s);
            }
            int r = ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            if (r != DISP_CHANGE_SUCCESSFUL) throw new InvalidOperationException("無法套用顯示設定（" + DispChangeText(r) + "）");
        }

        /// <summary>等到 <paramref name="wanted"/> 的螢幕都已開啟並有顯示來源（最多約 3 秒），回傳開啟中的路徑。</summary>
        static DISPLAYCONFIG_PATH_INFO[] WaitForActive(HashSet<(uint, int, uint)> wanted)
        {
            for (int attempt = 0; ; attempt++)
            {
                DISPLAYCONFIG_PATH_INFO[] paths = Query(QDC_ONLY_ACTIVE_PATHS).Paths;
                bool ready = wanted.All(k => paths.Any(p => TargetKey(p).Equals(k) && SourceGdiName(p.sourceInfo) != null));
                if (ready || attempt >= 12) return paths;
                Thread.Sleep(250);
            }
        }

        /// <summary>目前的螢幕狀態（還原用）。複製（鏡像）時多台位於 (0,0)，只把第一台當主螢幕。</summary>
        static MonitorSettings[] Snapshot(List<(DetectedMonitor Monitor, DISPLAYCONFIG_PATH_INFO Path)> targets)
        {
            bool primaryTaken = false;
            return targets.Select(t =>
            {
                DetectedMonitor d = t.Monitor;
                VideoMode mode = d.Current ?? d.Native ?? new VideoMode(0, 0, 0);
                bool primary = d.IsActive && d.IsPrimary && !primaryTaken;
                primaryTaken |= primary;
                return new MonitorSettings
                {
                    Id = d.Id, Name = d.Name, Enabled = d.IsActive, X = d.X, Y = d.Y,
                    Width = mode.Width, Height = mode.Height, RefreshRate = mode.RefreshRate,
                    Orientation = d.Orientation, Primary = primary,
                };
            }).ToArray();
        }

        static (DetectedMonitor Monitor, DISPLAYCONFIG_PATH_INFO Path) FindTarget(
            List<(DetectedMonitor Monitor, DISPLAYCONFIG_PATH_INFO Path)> targets, MonitorSettings s)
        {
            foreach (var t in targets)
                if (t.Monitor.Id == s.Id) return t;
            throw new InvalidOperationException("找不到螢幕「" + s.Name + "」（可能剛拔掉）");
        }

        /// <summary>
        /// 讓開啟的螢幕剛好是 <paramref name="wanted"/>：保留要的開啟中路徑，關閉其他的，
        /// 要開啟的螢幕各選一個沒被使用的顯示來源。新開啟的路徑不指定模式，由系統選定（之後再以 ChangeDisplaySettingsEx 設定）。
        /// </summary>
        static void SetActiveTargets(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes,
            HashSet<(uint, int, uint)> wanted, List<(DetectedMonitor Monitor, DISPLAYCONFIG_PATH_INFO Path)> targets)
        {
            var result = new List<DISPLAYCONFIG_PATH_INFO>();
            var resultModes = new List<DISPLAYCONFIG_MODE_INFO>();
            var usedSources = new HashSet<(uint, int, uint)>();
            var done = new HashSet<(uint, int, uint)>();

            uint CopyMode(uint index)
            {
                if (index == DISPLAYCONFIG_PATH_MODE_IDX_INVALID || index >= modes.Length) return DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                resultModes.Add(modes[index]);
                return (uint)(resultModes.Count - 1);
            }

            foreach (DISPLAYCONFIG_PATH_INFO p in paths.Where(IsActive))
            {
                if (!wanted.Contains(TargetKey(p)) || !done.Add(TargetKey(p))) continue;
                var kept = p;
                kept.sourceInfo.modeInfoIdx = CopyMode(p.sourceInfo.modeInfoIdx);
                kept.targetInfo.modeInfoIdx = CopyMode(p.targetInfo.modeInfoIdx);
                result.Add(kept);
                usedSources.Add(SourceKey(p));
            }

            foreach (var key in wanted.Where(k => !done.Contains(k)))
            {
                DISPLAYCONFIG_PATH_INFO? free = paths
                    .Where(p => TargetKey(p).Equals(key) && !usedSources.Contains(SourceKey(p)))
                    .OrderBy(p => p.sourceInfo.id)
                    .Cast<DISPLAYCONFIG_PATH_INFO?>()
                    .FirstOrDefault();
                if (free == null)
                {
                    string name = targets.FirstOrDefault(t => TargetKey(t.Path).Equals(key)).Monitor?.Name ?? "";
                    throw new InvalidOperationException("沒有可用的顯示來源可以開啟「" + name + "」（顯示卡同時輸出的螢幕數量已達上限？）");
                }
                var p = free.Value;
                p.flags = DISPLAYCONFIG_PATH_ACTIVE;
                p.sourceInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                p.targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                result.Add(p);
                usedSources.Add(SourceKey(p));
            }

            DISPLAYCONFIG_MODE_INFO[] modeArray = resultModes.ToArray();
            int err = SetDisplayConfig((uint)result.Count, result.ToArray(), (uint)modeArray.Length, modeArray,
                SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
            if (err != 0) throw new InvalidOperationException("無法開關螢幕：" + new Win32Exception(err).Message + "（" + err + "）");
        }

        /// <summary>以 ChangeDisplaySettingsEx 暫存（CDS_NORESET）一台螢幕的解析度、更新率、方向與座標。</summary>
        static void SetMode(string gdi, MonitorSettings s)
        {
            var dm = NewDevMode();
            if (!EnumDisplaySettings(gdi, ENUM_CURRENT_SETTINGS, ref dm))
                throw new InvalidOperationException("無法讀取「" + s.Name + "」目前的顯示設定");

            dm.dmFields = DM_POSITION | DM_DISPLAYORIENTATION;
            dm.dmPositionX = s.X;
            dm.dmPositionY = s.Y;
            dm.dmDisplayOrientation = ToDmdo(s.Orientation);
            if (s.Width > 0 && s.Height > 0)
            {
                // DEVMODE 的寬高是旋轉後的值
                bool rotated = s.Orientation is Orientation.Portrait or Orientation.PortraitFlipped;
                dm.dmPelsWidth = (uint)(rotated ? s.Height : s.Width);
                dm.dmPelsHeight = (uint)(rotated ? s.Width : s.Height);
                dm.dmFields |= DM_PELSWIDTH | DM_PELSHEIGHT;
            }
            if (s.RefreshRate > 0)
            {
                dm.dmDisplayFrequency = (uint)s.RefreshRate;
                dm.dmFields |= DM_DISPLAYFREQUENCY;
            }

            uint flags = CDS_UPDATEREGISTRY | CDS_NORESET | (s.Primary ? CDS_SET_PRIMARY : 0);
            int r = ChangeDisplaySettingsEx(gdi, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
            if (r != DISP_CHANGE_SUCCESSFUL)
                throw new InvalidOperationException("無法設定「" + s.Name + "」為 " + s.Width + "x" + s.Height + " " + s.RefreshRate + "Hz（" +
                    DispChangeText(r) + "）");
        }

        static string DispChangeText(int code) => code switch
        {
            DISP_CHANGE_BADMODE => "螢幕不支援這個顯示模式",
            DISP_CHANGE_FAILED => "顯示卡驅動程式無法套用",
            DISP_CHANGE_RESTART => "需要重新開機",
            DISP_CHANGE_BADPARAM or DISP_CHANGE_BADFLAGS => "參數錯誤",
            DISP_CHANGE_NOTUPDATED => "無法寫入登錄檔",
            DISP_CHANGE_BADDUALVIEW => "雙螢幕設定錯誤",
            _ => "錯誤碼 " + code,
        };

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

        static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) Query(uint flags)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                int err = GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
                if (err != 0) throw new Win32Exception(err);
                var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                err = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (err == ERROR_INSUFFICIENT_BUFFER) continue;   // 螢幕在兩次呼叫之間改變，重試
                if (err != 0) throw new Win32Exception(err);
                Array.Resize(ref paths, (int)pathCount);
                Array.Resize(ref modes, (int)modeCount);
                return (paths, modes);
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

        // 方向對照（偵測與套用共用同一張表，擷取後再套用一定還原成相同的方向）
        static Orientation ToOrientation(uint dmdo) => dmdo switch
        {
            DMDO_90 => Orientation.Portrait,
            DMDO_180 => Orientation.LandscapeFlipped,
            DMDO_270 => Orientation.PortraitFlipped,
            _ => Orientation.Landscape,
        };

        static uint ToDmdo(Orientation o) => o switch
        {
            Orientation.Portrait => DMDO_90,
            Orientation.LandscapeFlipped => DMDO_180,
            Orientation.PortraitFlipped => DMDO_270,
            _ => DMDO_DEFAULT,
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
        const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
        const uint DISPLAYCONFIG_PATH_ACTIVE = 0x1;
        const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;
        const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20;
        const uint SDC_APPLY = 0x80;
        const uint SDC_SAVE_TO_DATABASE = 0x200;
        const uint SDC_ALLOW_CHANGES = 0x400;
        const uint DMDO_DEFAULT = 0;
        const uint DM_POSITION = 0x20;
        const uint DM_DISPLAYORIENTATION = 0x80;
        const uint DM_PELSWIDTH = 0x80000;
        const uint DM_PELSHEIGHT = 0x100000;
        const uint DM_DISPLAYFREQUENCY = 0x400000;
        const uint CDS_UPDATEREGISTRY = 0x1;
        const uint CDS_SET_PRIMARY = 0x10;
        const uint CDS_NORESET = 0x10000000;
        const int DISP_CHANGE_SUCCESSFUL = 0;
        const int DISP_CHANGE_RESTART = 1;
        const int DISP_CHANGE_FAILED = -1;
        const int DISP_CHANGE_BADMODE = -2;
        const int DISP_CHANGE_NOTUPDATED = -3;
        const int DISP_CHANGE_BADFLAGS = -4;
        const int DISP_CHANGE_BADPARAM = -5;
        const int DISP_CHANGE_BADDUALVIEW = -6;
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

        /// <summary>
        /// QueryDisplayConfig 的模式陣列元素。只宣告共同的開頭欄位；聯集部分不讀取，
        /// 但整個結構可直接複製（blittable，64 位元組原樣保留）再交給 SetDisplayConfig。
        /// </summary>
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
        static extern int SetDisplayConfig(uint numPathArrayElements, [In] DISPLAYCONFIG_PATH_INFO[] pathArray,
            uint numModeInfoArrayElements, [In] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
        static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
        static extern int ChangeDisplaySettingsEx(string? deviceName, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr lParam);

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
