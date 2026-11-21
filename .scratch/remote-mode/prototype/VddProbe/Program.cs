// 票 02 的拋棄式測試工具（不進產品）。
//
//   VddProbe status                 驅動、裝置介面、憑證、虛擬螢幕的狀態
//   VddProbe install <drvdir>       （需系統管理員）匯入憑證、建立裝置節點、安裝 INF（只用 Windows 內建 API）
//   VddProbe uninstall <drvdir>     （需系統管理員）移除裝置、驅動套件與憑證
//   VddProbe hold [w h hz]          建立虛擬螢幕並每秒送心跳，直到 `VddProbe stop`
//   VddProbe solo [w h hz]          同 hold，另外關閉所有實體螢幕只留虛擬螢幕；停止時還原
//   VddProbe stop                   通知執行中的 hold/solo 正常結束
//   VddProbe watch [seconds]        監看虛擬螢幕何時消失，以及之後開啟中的畫面如何變化
//   VddProbe detect                 列出所有顯示目標（含介面卡的硬體 ID），供票 03 辨識虛擬螢幕
//
// hold/solo 期間記錄低階鍵盤滑鼠鉤子收到的輸入是否帶有注入旗標。

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

static class Program
{
    const string HardwareId = "Root\\SudoMaker\\SudoVDA";
    static readonly Guid InterfaceGuid = new Guid("e5bcc234-1e0c-418a-a0d4-ef8b7501414d");
    static readonly Guid MonitorGuid = new Guid("6a1d2c7e-5b0f-4c55-9e1a-0a5d1e2f3b41");
    const string StopEventName = "Local\\AutoDisplayMode.VddProbe.Stop";
    static readonly string TargetFile = Path.Combine(Path.GetTempPath(), "vddprobe-target.txt");

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        // --log=<file>：另外附加寫入檔案（以系統管理員身分執行時看不到主控台輸出）
        string logArg = args.FirstOrDefault(a => a.StartsWith("--log=", StringComparison.Ordinal));
        if (logArg != null)
        {
            logFile = new StreamWriter(logArg.Substring(6), append: true, Encoding.UTF8) { AutoFlush = true };
            args = args.Where(a => a != logArg).ToArray();
        }
        try
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (cmd)
            {
                case "status": return Status();
                case "install": return Install(Arg(args, 1));
                case "uninstall": return Uninstall(Arg(args, 1));
                case "hold": return Hold(args, solo: false);
                case "solo": return Hold(args, solo: true);
                case "stop": return Stop();
                case "watch": return Watch(args.Length > 1 ? int.Parse(args[1]) : 20);
                case "detect": return Detect();
                default:
                    Console.WriteLine("usage: VddProbe status|install <dir>|uninstall <dir>|hold [w h hz]|solo [w h hz]|stop|watch [s]|detect");
                    return 2;
            }
        }
        catch (Exception e)
        {
            Log("ERROR " + e.GetType().Name + ": " + e.Message);
            return 1;
        }
    }

    static string Arg(string[] a, int i) => a.Length > i ? a[i] : throw new ArgumentException("missing argument");

    static readonly object logLock = new object();
    static readonly DateTime start = DateTime.Now;
    static StreamWriter logFile;
    static void Log(string s)
    {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + " +" + (DateTime.Now - start).TotalSeconds.ToString("0.0") + "s] " + s;
        lock (logLock)
        {
            Console.WriteLine(line);
            logFile?.WriteLine(line);
        }
    }

    // ================================================================
    //  status
    // ================================================================

    static int Status()
    {
        var devices = FindDevices();
        Log("DEVICE_COUNT=" + devices.Count + (devices.Count > 0 ? " (" + string.Join(", ", devices) + ")" : ""));
        string path = FindInterfacePath();
        Log("INTERFACE=" + (path ?? "(none)"));
        if (path != null)
        {
            using (var dev = new Vda(path))
            {
                var v = dev.ProtocolVersion();
                var w = dev.Watchdog();
                Log("PROTOCOL=" + v + " WATCHDOG_TIMEOUT=" + w.timeout + "s COUNTDOWN=" + w.countdown);
            }
        }
        foreach (var name in new[] { StoreName.Root, StoreName.TrustedPublisher })
        {
            var store = new X509Store(name, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var mine = store.Certificates.Cast<X509Certificate2>().Where(c => c.Subject.Contains("AutoDisplayMode")).ToList();
            Log("CERT_" + name + "=" + (mine.Count == 0 ? "(none)" : string.Join(", ", mine.Select(c => c.Subject + " " + c.Thumbprint))));
            store.Close();
        }
        var t = ReadTarget();
        Log("VIRTUAL_PRESENT=" + (t != null && TargetPresent(t.Value)));
        Log("ACTIVE=" + DescribeActive());
        return 0;
    }

    // ================================================================
    //  install / uninstall（只用 Windows 內建 API）
    // ================================================================

    static int Install(string dir)
    {
        string inf = Path.GetFullPath(File.Exists(Path.Combine(dir, "x64", "SudoVDA.inf")) ? Path.Combine(dir, "x64", "SudoVDA.inf") : Path.Combine(dir, "SudoVDA.inf"));
        string cer = Directory.GetFiles(dir, "*.cer").Single();
        var cert = new X509Certificate2(cer);
        foreach (var name in new[] { StoreName.Root, StoreName.TrustedPublisher })
        {
            var store = new X509Store(name, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            store.Add(cert);
            store.Close();
            Log("imported cert into LocalMachine\\" + name + ": " + cert.Thumbprint);
        }

        if (FindDevices().Count == 0)
        {
            var classGuid = Guid.Empty;
            var className = new StringBuilder(256);
            if (!SetupDiGetINFClass(inf, ref classGuid, className, className.Capacity, out _)) throw new Win32Exception();
            IntPtr set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
            if (set == INVALID_HANDLE) throw new Win32Exception();
            try
            {
                var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                if (!SetupDiCreateDeviceInfo(set, className.ToString(), ref classGuid, null, IntPtr.Zero, DICD_GENERATE_ID, ref info)) throw new Win32Exception();
                byte[] hwid = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
                if (!SetupDiSetDeviceRegistryProperty(set, ref info, SPDRP_HARDWAREID, hwid, hwid.Length)) throw new Win32Exception();
                if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref info)) throw new Win32Exception();
                Log("created root device node (" + className + ")");
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        else Log("device node already exists");

        if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HardwareId, inf, INSTALLFLAG_FORCE, out bool reboot)) throw new Win32Exception();
        Log("driver installed" + (reboot ? " (REBOOT REQUIRED)" : ""));
        return 0;
    }

    static int Uninstall(string dir)
    {
        int removed = 0;
        ForEachDevice((set, info) =>
        {
            if (!DiUninstallDevice(IntPtr.Zero, set, ref info, 0, out bool reboot)) throw new Win32Exception();
            removed++;
            if (reboot) Log("device removal needs reboot");
        });
        Log("removed " + removed + " device(s)");

        // 驅動存放區中的套件：找出原始名稱是 SudoVDA.inf 的 oemNN.inf
        string infDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        foreach (string oem in Directory.GetFiles(infDir, "oem*.inf"))
        {
            string text;
            try { text = File.ReadAllText(oem); } catch { continue; }
            if (text.IndexOf("SudoMaker", StringComparison.OrdinalIgnoreCase) < 0 || text.IndexOf("SudoVDA", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (!SetupUninstallOEMInf(Path.GetFileName(oem), SUOI_FORCEDELETE, IntPtr.Zero)) throw new Win32Exception();
            Log("removed driver package " + Path.GetFileName(oem));
        }

        string cer = Directory.GetFiles(dir, "*.cer").Single();
        string thumb = new X509Certificate2(cer).Thumbprint;
        foreach (var name in new[] { StoreName.Root, StoreName.TrustedPublisher })
        {
            var store = new X509Store(name, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            foreach (var c in store.Certificates.Find(X509FindType.FindByThumbprint, thumb, false).Cast<X509Certificate2>().ToList())
            {
                store.Remove(c);
                Log("removed cert from LocalMachine\\" + name + ": " + c.Thumbprint);
            }
            store.Close();
        }
        return 0;
    }

    static List<string> FindDevices()
    {
        var list = new List<string>();
        ForEachDevice((set, info) =>
        {
            var id = new StringBuilder(512);
            SetupDiGetDeviceInstanceId(set, ref info, id, id.Capacity, out _);
            list.Add(id.ToString());
        });
        return list;
    }

    static void ForEachDevice(Action<IntPtr, SP_DEVINFO_DATA> action)
    {
        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES);
        if (set == INVALID_HANDLE) throw new Win32Exception();
        try
        {
            var matches = new List<SP_DEVINFO_DATA>();
            for (uint i = 0; ; i++)
            {
                var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                if (!SetupDiEnumDeviceInfo(set, i, ref info)) break;
                var buf = new byte[2048];
                if (!SetupDiGetDeviceRegistryProperty(set, ref info, SPDRP_HARDWAREID, out _, buf, buf.Length, out int size)) continue;
                var ids = Encoding.Unicode.GetString(buf, 0, size).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
                if (ids.Any(x => x.Equals(HardwareId, StringComparison.OrdinalIgnoreCase))) matches.Add(info);
            }
            foreach (var info in matches) action(set, info);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    static string FindInterfacePath()
    {
        var guid = InterfaceGuid;
        IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE) return null;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA)) };
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, 0, ref data)) return null;
            SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int needed, IntPtr.Zero);
            IntPtr detail = Marshal.AllocHGlobal(needed);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, needed, out _, IntPtr.Zero)) throw new Win32Exception();
                return Marshal.PtrToStringUni(detail + 4);
            }
            finally { Marshal.FreeHGlobal(detail); }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    // ================================================================
    //  hold / solo / stop
    // ================================================================

    static int Hold(string[] args, bool solo)
    {
        uint w = args.Length > 3 ? uint.Parse(args[1]) : 1920, h = args.Length > 3 ? uint.Parse(args[2]) : 1080, hz = args.Length > 3 ? uint.Parse(args[3]) : 60;
        string path = FindInterfacePath() ?? throw new InvalidOperationException("SudoVDA interface not found (driver not installed?)");
        using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName))
        using (var dev = new Vda(path))
        {
            stop.Reset();
            Log("PROTOCOL=" + dev.ProtocolVersion() + " WATCHDOG_TIMEOUT=" + dev.Watchdog().timeout + "s");
            Log("REMOTE_SESSION(RDP)=" + System.Windows.Forms.SystemInformation.TerminalServerSession);

            var saved = Query(QDC_ONLY_ACTIVE_PATHS);
            Log("BEFORE ACTIVE=" + DescribeActive());

            var target = dev.Add(w, h, hz);
            File.WriteAllText(TargetFile, target.luid.LowPart + " " + target.luid.HighPart + " " + target.id);
            Log("ADDED " + w + "x" + h + "@" + hz + " adapter=" + target.luid.HighPart + ":" + target.luid.LowPart + " target=" + target.id);

            var pinger = new Thread(() =>
            {
                while (!stop.WaitOne(1000))
                {
                    try { dev.Ping(); } catch (Exception e) { Log("PING FAILED " + e.Message); }
                }
            }) { IsBackground = true };
            pinger.Start();

            var hooks = new Thread(HookLoop) { IsBackground = true };
            hooks.SetApartmentState(ApartmentState.STA);
            hooks.Start();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!TargetPresent(target) && sw.ElapsedMilliseconds < 15000) Thread.Sleep(100);
            Log("VIRTUAL_APPEARED=" + TargetPresent(target) + " after " + sw.ElapsedMilliseconds + "ms; active=" + TargetActive(target));
            if (!TargetActive(target))
            {
                // Windows 沒自動延伸到新螢幕時，延伸到它（保留原本的實體螢幕）
                int r = SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
                Thread.Sleep(1500);
                Log("EXTEND result=" + r + " active=" + TargetActive(target));
            }
            LogModes(target);

            if (solo)
            {
                var all = Query(QDC_ALL_PATHS).Paths;
                var p = all.First(x => Same(x.targetInfo, target));
                p.flags = DISPLAYCONFIG_PATH_ACTIVE;
                p.sourceInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                p.targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                int r = SetDisplayConfig(1, new[] { p }, 0, null, SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
                Thread.Sleep(1500);
                Log("SOLO result=" + r + " ACTIVE=" + DescribeActive());
            }

            Log("RUNNING; stop with `VddProbe stop`");
            var cancel = new ManualResetEvent(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; stop.Set(); };
            stop.WaitOne();
            Log("STOP requested");

            if (solo)
            {
                int r = SetDisplayConfig((uint)saved.Paths.Length, saved.Paths, (uint)saved.Modes.Length, saved.Modes,
                    SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
                Thread.Sleep(1500);
                Log("RESTORE result=" + r + " ACTIVE=" + DescribeActive());
            }
            dev.Remove();
            Thread.Sleep(1000);
            Log("REMOVED; VIRTUAL_PRESENT=" + TargetPresent(target) + " ACTIVE=" + DescribeActive());
            try { File.Delete(TargetFile); } catch { }
        }
        return 0;
    }

    static int Stop()
    {
        if (!EventWaitHandle.TryOpenExisting(StopEventName, out var ev)) { Log("no running hold/solo"); return 1; }
        ev.Set();
        Log("stop signalled");
        return 0;
    }

    // ================================================================
    //  watch
    // ================================================================

    static int Watch(int seconds)
    {
        var t = ReadTarget();
        if (t == null) { Log("no target recorded; start hold/solo first"); return 1; }
        Log("watching target; present=" + TargetPresent(t.Value) + " ACTIVE=" + DescribeActive());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool wasPresent = TargetPresent(t.Value);
        long goneAt = -1;
        string lastActive = DescribeActive();
        while (true)
        {
            Thread.Sleep(100);
            bool present = TargetPresent(t.Value);
            if (wasPresent && !present) { goneAt = sw.ElapsedMilliseconds; Log("VIRTUAL_GONE after " + goneAt + "ms"); }
            wasPresent = present;
            string active = DescribeActive();
            if (active != lastActive) { Log("ACTIVE changed: " + active); lastActive = active; }
            if (goneAt >= 0 && sw.ElapsedMilliseconds - goneAt > seconds * 1000) break;
            if (goneAt < 0 && sw.ElapsedMilliseconds > 60000) { Log("virtual display still present after 60s"); break; }
        }
        Log("FINAL ACTIVE=" + lastActive);
        return 0;
    }

    // ================================================================
    //  detect
    // ================================================================

    static int Detect()
    {
        var seen = new HashSet<string>();
        foreach (var p in Query(QDC_ALL_PATHS).Paths)
        {
            var key = p.targetInfo.adapterId.HighPart + ":" + p.targetInfo.adapterId.LowPart + ":" + p.targetInfo.id;
            if (!seen.Add(key)) continue;
            var name = TargetName(p.targetInfo);
            if (name == null) continue;
            string adapter = AdapterPath(p.targetInfo.adapterId);
            Log("TARGET " + key + " active=" + ((p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0) + " available=" + (p.targetInfo.targetAvailable != 0) +
                " tech=" + p.targetInfo.outputTechnology + " name=\"" + name.Value.monitorFriendlyDeviceName + "\" edid=" +
                DecodeManufacturer(name.Value.edidManufactureId) + "/" + name.Value.edidProductCodeId.ToString("X4") +
                "\n    monitor=" + name.Value.monitorDevicePath + "\n    adapter=" + adapter + "\n    adapterHwIds=" + AdapterHardwareIds(adapter));
        }
        return 0;
    }

    static string DecodeManufacturer(ushort id)
    {
        int v = (id >> 8) | ((id & 0xFF) << 8);
        return new string(new[] { (char)('A' - 1 + ((v >> 10) & 31)), (char)('A' - 1 + ((v >> 5) & 31)), (char)('A' - 1 + (v & 31)) });
    }

    static string AdapterHardwareIds(string adapterPath)
    {
        if (string.IsNullOrEmpty(adapterPath) || !adapterPath.StartsWith("\\\\?\\")) return "";
        string s = adapterPath.Substring(4);
        int brace = s.LastIndexOf("#{", StringComparison.Ordinal);
        if (brace >= 0) s = s.Substring(0, brace);
        string instance = s.Replace('#', '\\');
        if (CM_Locate_DevNode(out uint devInst, instance, 0) != 0) return "(locate failed: " + instance + ")";
        var buf = new byte[2048];
        uint len = (uint)buf.Length;
        if (CM_Get_DevNode_Registry_Property(devInst, CM_DRP_HARDWAREID, out _, buf, ref len, 0) != 0) return "(no hwid) instance=" + instance;
        return instance + " => " + string.Join(";", Encoding.Unicode.GetString(buf, 0, (int)len).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries));
    }

    // ================================================================
    //  顯示設定輔助
    // ================================================================

    struct Target { public LUID luid; public uint id; }

    static Target? ReadTarget()
    {
        try
        {
            var parts = File.ReadAllText(TargetFile).Split(' ');
            return new Target { luid = new LUID { LowPart = uint.Parse(parts[0]), HighPart = int.Parse(parts[1]) }, id = uint.Parse(parts[2]) };
        }
        catch { return null; }
    }

    static bool Same(DISPLAYCONFIG_PATH_TARGET_INFO t, Target target) =>
        t.adapterId.LowPart == target.luid.LowPart && t.adapterId.HighPart == target.luid.HighPart && t.id == target.id;

    static bool TargetPresent(Target target)
    {
        try { return Query(QDC_ALL_PATHS).Paths.Any(p => Same(p.targetInfo, target) && p.targetInfo.targetAvailable != 0); }
        catch { return false; }
    }

    static bool TargetActive(Target target)
    {
        try { return Query(QDC_ONLY_ACTIVE_PATHS).Paths.Any(p => Same(p.targetInfo, target)); }
        catch { return false; }
    }

    static string DescribeActive()
    {
        try
        {
            var q = Query(QDC_ONLY_ACTIVE_PATHS);
            return "[" + string.Join(", ", q.Paths.Select(p =>
            {
                var n = TargetName(p.targetInfo);
                string gdi = SourceGdiName(p.sourceInfo);
                string mode = "";
                var dm = NewDevMode();
                if (gdi != null && EnumDisplaySettings(gdi, ENUM_CURRENT_SETTINGS, ref dm)) mode = " " + dm.dmPelsWidth + "x" + dm.dmPelsHeight + "@" + dm.dmDisplayFrequency + " (" + dm.dmPositionX + "," + dm.dmPositionY + ")";
                return (n?.monitorFriendlyDeviceName ?? "?") + mode;
            })) + "]";
        }
        catch (Exception e) { return "(query failed: " + e.Message + ")"; }
    }

    static void LogModes(Target target)
    {
        var p = Query(QDC_ONLY_ACTIVE_PATHS).Paths.FirstOrDefault(x => Same(x.targetInfo, target));
        if (!Same(p.targetInfo, target)) { Log("MODES: virtual display not active"); return; }
        string gdi = SourceGdiName(p.sourceInfo);
        var modes = new SortedSet<string>(StringComparer.Ordinal);
        var list = new List<(uint w, uint h, uint f)>();
        var dm = NewDevMode();
        for (int i = 0; EnumDisplaySettings(gdi, i, ref dm); i++) list.Add((dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
        var text = string.Join(" ", list.Distinct().OrderByDescending(x => x.w).ThenByDescending(x => x.h).ThenByDescending(x => x.f).Select(x => x.w + "x" + x.h + "@" + x.f));
        Log("GDI=" + gdi + " MODES(" + list.Distinct().Count() + ")=" + text);
    }

    static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) Query(uint flags)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int err = GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
            if (err != 0) throw new Win32Exception(err);
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            err = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (err == 122) continue;
            if (err != 0) throw new Win32Exception(err);
            Array.Resize(ref paths, (int)pathCount);
            Array.Resize(ref modes, (int)modeCount);
            return (paths, modes);
        }
        throw new Win32Exception(122);
    }

    static DISPLAYCONFIG_TARGET_DEVICE_NAME? TargetName(DISPLAYCONFIG_PATH_TARGET_INFO t)
    {
        var name = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        name.header.type = 2;
        name.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_TARGET_DEVICE_NAME));
        name.header.adapterId = t.adapterId;
        name.header.id = t.id;
        return DisplayConfigGetDeviceInfo(ref name) == 0 ? name : (DISPLAYCONFIG_TARGET_DEVICE_NAME?)null;
    }

    static string SourceGdiName(DISPLAYCONFIG_PATH_SOURCE_INFO s)
    {
        var name = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
        name.header.type = 1;
        name.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME));
        name.header.adapterId = s.adapterId;
        name.header.id = s.id;
        return DisplayConfigGetDeviceInfo(ref name) == 0 ? name.viewGdiDeviceName : null;
    }

    static string AdapterPath(LUID luid)
    {
        var name = new DISPLAYCONFIG_ADAPTER_NAME();
        name.header.type = 4;
        name.header.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_ADAPTER_NAME));
        name.header.adapterId = luid;
        return DisplayConfigGetDeviceInfo(ref name) == 0 ? name.adapterDevicePath : null;
    }

    static DEVMODE NewDevMode() => new DEVMODE { dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE)) };

    // ================================================================
    //  鍵盤滑鼠鉤子：每秒彙總一次實體／注入的輸入數量
    // ================================================================

    static int mousePhys, mouseInj, keyPhys, keyInj;
    static LowLevelProc mouseProc, keyProc;

    static void HookLoop()
    {
        mouseProc = (code, wParam, lParam) =>
        {
            if (code >= 0)
            {
                uint flags = (uint)Marshal.ReadInt32(lParam, 12);
                if ((flags & 0x3) != 0) Interlocked.Increment(ref mouseInj); else Interlocked.Increment(ref mousePhys);
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        };
        keyProc = (code, wParam, lParam) =>
        {
            if (code >= 0)
            {
                uint flags = (uint)Marshal.ReadInt32(lParam, 8);
                if ((flags & 0x12) != 0) Interlocked.Increment(ref keyInj); else Interlocked.Increment(ref keyPhys);
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        };
        IntPtr mod = GetModuleHandle(null);
        IntPtr mh = SetWindowsHookEx(14, mouseProc, mod, 0);
        IntPtr kh = SetWindowsHookEx(13, keyProc, mod, 0);
        Log("HOOKS installed mouse=" + (mh != IntPtr.Zero) + " keyboard=" + (kh != IntPtr.Zero));
        var reporter = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(1000);
                int mp = Interlocked.Exchange(ref mousePhys, 0), mi = Interlocked.Exchange(ref mouseInj, 0);
                int kp = Interlocked.Exchange(ref keyPhys, 0), ki = Interlocked.Exchange(ref keyInj, 0);
                if (mp + mi + kp + ki > 0) Log("INPUT mouse physical=" + mp + " injected=" + mi + " | keyboard physical=" + kp + " injected=" + ki);
            }
        }) { IsBackground = true };
        reporter.Start();
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
    }

    // ================================================================
    //  SudoVDA IOCTL
    // ================================================================

    sealed class Vda : IDisposable
    {
        const uint ADD = (0x22 << 16) | (0x800 << 2);
        const uint REMOVE = (0x22 << 16) | (0x801 << 2);
        const uint GET_WATCHDOG = (0x22 << 16) | (0x803 << 2);
        const uint PING = (0x22 << 16) | (0x888 << 2);
        const uint GET_VERSION = (0x22 << 16) | (0x8FF << 2);
        readonly IntPtr handle;
        readonly object gate = new object();

        public Vda(string path)
        {
            handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle == INVALID_HANDLE) throw new Win32Exception();
        }

        byte[] Io(uint code, byte[] input, int outSize)
        {
            var output = new byte[outSize];
            lock (gate)
            {
                if (!DeviceIoControl(handle, code, input, input?.Length ?? 0, output, outSize, out int returned, IntPtr.Zero)) throw new Win32Exception();
                Array.Resize(ref output, returned);
            }
            return output;
        }

        public string ProtocolVersion()
        {
            var b = Io(GET_VERSION, null, 4);
            return b.Length >= 4 ? b[0] + "." + b[1] + "." + b[2] + (b[3] != 0 ? "-test" : "") : "(unknown)";
        }

        public (uint timeout, uint countdown) Watchdog()
        {
            var b = Io(GET_WATCHDOG, null, 8);
            return b.Length >= 8 ? (BitConverter.ToUInt32(b, 0), BitConverter.ToUInt32(b, 4)) : (0, 0);
        }

        public Target Add(uint w, uint h, uint hz)
        {
            var input = new byte[56];
            BitConverter.GetBytes(w).CopyTo(input, 0);
            BitConverter.GetBytes(h).CopyTo(input, 4);
            BitConverter.GetBytes(hz).CopyTo(input, 8);
            MonitorGuid.ToByteArray().CopyTo(input, 12);
            Encoding.ASCII.GetBytes("AutoDisplay").CopyTo(input, 28);
            Encoding.ASCII.GetBytes("ADM0001").CopyTo(input, 42);
            var b = Io(ADD, input, 12);
            if (b.Length < 12) throw new InvalidOperationException("ADD returned " + b.Length + " bytes");
            return new Target { luid = new LUID { LowPart = BitConverter.ToUInt32(b, 0), HighPart = BitConverter.ToInt32(b, 4) }, id = BitConverter.ToUInt32(b, 8) };
        }

        public void Remove() => Io(REMOVE, MonitorGuid.ToByteArray(), 0);
        public void Ping() => Io(PING, null, 0);
        public void Dispose() => CloseHandle(handle);
    }

    // ================================================================
    //  Win32
    // ================================================================

    static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);
    const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4, DIGCF_DEVICEINTERFACE = 0x10;
    const uint DICD_GENERATE_ID = 0x1, SPDRP_HARDWAREID = 0x1, DIF_REGISTERDEVICE = 0x19, INSTALLFLAG_FORCE = 0x1, SUOI_FORCEDELETE = 0x1;
    const uint CM_DRP_HARDWAREID = 0x2;
    const uint QDC_ALL_PATHS = 0x1, QDC_ONLY_ACTIVE_PATHS = 0x2, DISPLAYCONFIG_PATH_ACTIVE = 0x1, DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;
    const uint SDC_TOPOLOGY_EXTEND = 0x4, SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_APPLY = 0x80, SDC_SAVE_TO_DATABASE = 0x200, SDC_ALLOW_CHANGES = 0x400;
    const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA info, uint property, out uint type, byte[] buffer, int size, out int required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA info, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetINFClass(string inf, ref Guid classGuid, StringBuilder className, int size, out int required);
    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwnd);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiCreateDeviceInfo(IntPtr set, string name, ref Guid classGuid, string description, IntPtr hwnd, uint flags, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA info, uint property, byte[] buffer, int size);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref SP_DEVINFO_DATA info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupUninstallOEMInf(string inf, uint flags, IntPtr reserved);
    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwnd, string hardwareId, string infPath, uint flags, out bool reboot);
    [DllImport("newdev.dll", SetLastError = true)] static extern bool DiUninstallDevice(IntPtr hwnd, IntPtr set, ref SP_DEVINFO_DATA info, uint flags, out bool reboot);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern uint CM_Locate_DevNode(out uint devInst, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_Registry_PropertyW")] static extern uint CM_Get_DevNode_Registry_Property(uint devInst, uint property, out uint type, byte[] buffer, ref uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(IntPtr h, uint code, byte[] input, int inSize, byte[] output, int outSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, LowLevelProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);

    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)] struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }
    [StructLayout(LayoutKind.Sequential)] struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }
    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }
    [StructLayout(LayoutKind.Explicit, Size = 64)] struct DISPLAYCONFIG_MODE_INFO { [FieldOffset(0)] public uint infoType; }
    [StructLayout(LayoutKind.Sequential)] struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public int type; public uint size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header; public uint flags, outputTechnology; public ushort edidManufactureId, edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_SOURCE_DEVICE_NAME { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_ADAPTER_NAME { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string adapterDevicePath; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public uint dmFields; public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput; public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels; public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPaths, [In] DISPLAYCONFIG_PATH_INFO[] paths, uint numModes, [In] DISPLAYCONFIG_MODE_INFO[] modes, uint flags);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME packet);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME packet);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_ADAPTER_NAME packet);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
}
