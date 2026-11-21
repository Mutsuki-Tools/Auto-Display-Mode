using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AutoDisplayMode.Core;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AutoDisplayMode.Windows
{
    /// <summary>安裝或移除失敗：訊息直接顯示給使用者。</summary>
    sealed class DriverSetupException : Exception
    {
        public DriverSetupException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// SudoVDA 驅動（ADR-0005）的偵測、安裝與移除，只用 Windows 內建的 API：SetupAPI、
    /// <c>UpdateDriverForPlugAndPlayDevices</c>、<c>DiUninstallDevice</c>、<c>SetupUninstallOEMInf</c>、<see cref="X509Store"/>。
    /// 偵測不需要系統管理員權限；<see cref="Install"/>、<see cref="Uninstall"/> 需要，由 <see cref="SudoVdaDriverInstaller"/> 的提權子程序呼叫。
    /// <para>
    /// 「本程式的」驅動套件以目錄檔（.cat）的簽章者辨識：憑證主旨以 <see cref="CertSubjectPrefix"/> 開頭
    /// （build-driver.ps1 每次編譯都換一張新憑證，主旨開頭不變）。其他軟體（例如 Apollo）裝的 SudoVDA 硬體 ID 相同，
    /// 但簽章者不同，不會被當成本程式的驅動，也不會被移除。
    /// </para>
    /// </summary>
    static class SudoVdaSetup
    {
        public const string HardwareId = @"Root\SudoMaker\SudoVDA";
        const string CertSubjectPrefix = "CN=AutoDisplayMode SudoVDA";
        static readonly Guid InterfaceGuid = new("e5bcc234-1e0c-418a-a0d4-ef8b7501414d");
        static readonly StoreName[] CertStores = { StoreName.Root, StoreName.TrustedPublisher };

        // 驅動資料夾（drivers\sudovda）中需要的檔案
        const string InfFile = @"x64\SudoVDA.inf";
        const string CerFile = "AutoDisplayMode-SudoVDA.cer";
        static readonly string[] RequiredFiles = { InfFile, @"x64\SudoVDA.dll", @"x64\sudovda.cat", CerFile };

        /// <summary>驅動資料夾中缺少的檔案（相對路徑）；齊全時為空。</summary>
        public static List<string> MissingFiles(string folder) =>
            RequiredFiles.Where(f => !File.Exists(Path.Combine(folder, f))).ToList();

        // ================================================================
        //  偵測
        // ================================================================

        /// <summary>硬體 ID 為 SudoVDA 的裝置。</summary>
        /// <param name="InfPath">裝置使用的驅動套件（oemNN.inf）；還沒裝驅動時為 null。</param>
        /// <param name="Present">裝置是否存在（不存在的是留在系統裡的舊裝置）。</param>
        /// <param name="Started">驅動已啟動。</param>
        /// <param name="Problem">裝置管理員的問題代碼；沒有問題時為 0。</param>
        sealed record Device(string InstanceId, string? InfPath, bool Present, bool Started, uint Problem);

        /// <summary>電腦上與 SudoVDA 有關的一切。</summary>
        sealed class Snapshot
        {
            public List<Device> Devices = new();
            public HashSet<string> OurPackages = new(StringComparer.OrdinalIgnoreCase);   // 本程式的驅動套件（oemNN.inf）
            public int CertCount;   // 「受信任的根憑證」與「受信任的發行者」中本程式的憑證
            public bool InterfacePresent;

            /// <summary>本程式的裝置：使用本程式的驅動套件，或還沒裝上任何驅動（例如安裝到一半）。</summary>
            public bool IsOurs(Device d) => d.InfPath == null || OurPackages.Contains(d.InfPath);

            public static Snapshot Take() => new()
            {
                Devices = FindDevices(),
                OurPackages = new HashSet<string>(FindOurPackages(), StringComparer.OrdinalIgnoreCase),
                CertCount = FindOurCerts().Count,
                InterfacePresent = HasInterface(),
            };
        }

        /// <summary>讀取目前的狀態。</summary>
        public static DriverStatus GetStatus(string folder)
        {
            List<string> missing = MissingFiles(folder);
            Snapshot s = Snapshot.Take();
            List<Device> ours = s.Devices.Where(s.IsOurs).ToList();
            bool leftovers = ours.Count > 0 || s.OurPackages.Count > 0 || s.CertCount > 0;

            if (s.Devices.Any(d => d.Present && !s.IsOurs(d)))
                return new DriverStatus(DriverState.Unavailable,
                    "這台電腦已經有其他軟體（例如 Apollo）安裝的 SudoVDA 驅動。\n請先用那個軟體移除它，再安裝本程式的驅動。",
                    CanInstall: false, CanUninstall: leftovers);

            List<Device> present = ours.Where(d => d.Present).ToList();
            if (present.Count > 0)
            {
                if (s.InterfacePresent && present.Any(d => d.InfPath != null && d.Started && d.Problem == 0))
                    return new DriverStatus(DriverState.Installed, "", CanInstall: false, CanUninstall: true);
                return new DriverStatus(DriverState.Unavailable, ProblemText(present[0]) + "\n可以按「移除驅動」後重新安裝。",
                    CanInstall: missing.Count == 0, CanUninstall: true);
            }

            if (missing.Count > 0)
                return new DriverStatus(DriverState.FilesMissing,
                    "驅動檔應該放在：" + folder + "\n缺少：" + string.Join("、", missing) +
                    "\n請把整個程式資料夾（exe 與 drivers 資料夾）一起複製過來。",
                    CanInstall: false, CanUninstall: leftovers);

            return new DriverStatus(DriverState.NotInstalled,
                leftovers ? "電腦上有上次留下的裝置、驅動套件或憑證，可以按「移除驅動」清除。" : "",
                CanInstall: true, CanUninstall: leftovers);
        }

        static string ProblemText(Device d)
        {
            if (d.InfPath == null) return "裝置已建立，但沒有安裝驅動。";
            return d.Problem switch
            {
                0 => d.Started ? "驅動沒有回應（找不到裝置介面）。" : "驅動還沒啟動。",
                10 => "驅動無法啟動（代碼 10）。",
                14 => "需要重新開機才能使用（代碼 14）。",
                22 => "裝置已在裝置管理員中停用（代碼 22）。",
                28 => "裝置沒有安裝驅動（代碼 28）。",
                39 => "驅動無法載入（代碼 39）。",
                52 => "Windows 無法驗證驅動的數位簽章，可能是憑證被刪除了（代碼 52）。",
                _ => "裝置有問題（裝置管理員代碼 " + d.Problem + "）。",
            };
        }

        static List<Device> FindDevices()
        {
            var list = new List<Device>();
            ForEachDevice((set, info) =>
            {
                var id = new StringBuilder(512);
                SetupDiGetDeviceInstanceId(set, ref info, id, id.Capacity, out _);
                bool present = CM_Get_DevNode_Status(out uint status, out uint problem, info.DevInst, 0) == CR_SUCCESS;
                list.Add(new Device(id.ToString(), ReadInfPath(set, info), present,
                    present && (status & DN_STARTED) != 0,
                    present && (status & DN_HAS_PROBLEM) != 0 ? problem : 0));
            });
            return list;
        }

        /// <summary>裝置使用的驅動套件：驅動機碼的 InfPath（例如 oem42.inf）。</summary>
        static string? ReadInfPath(IntPtr set, SP_DEVINFO_DATA info)
        {
            IntPtr h = SetupDiOpenDevRegKey(set, ref info, DICS_FLAG_GLOBAL, 0, DIREG_DRV, KEY_READ);
            if (h == INVALID_HANDLE) return null;
            using RegistryKey key = RegistryKey.FromHandle(new SafeRegistryHandle(h, true));
            return key.GetValue("InfPath") is string s && s.Length > 0 ? s : null;
        }

        /// <summary>對每個硬體 ID 為 SudoVDA 的根列舉裝置（包含不存在的舊裝置）呼叫 <paramref name="action"/>。</summary>
        static void ForEachDevice(Action<IntPtr, SP_DEVINFO_DATA> action)
        {
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, "ROOT", IntPtr.Zero, DIGCF_ALLCLASSES);
            if (set == INVALID_HANDLE) throw Failure("列舉裝置");
            try
            {
                var matches = new List<SP_DEVINFO_DATA>();
                for (uint i = 0; ; i++)
                {
                    var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                    if (!SetupDiEnumDeviceInfo(set, i, ref info)) break;
                    var buf = new byte[2048];
                    if (!SetupDiGetDeviceRegistryProperty(set, ref info, SPDRP_HARDWAREID, out _, buf, buf.Length, out int size)) continue;
                    string[] ids = Encoding.Unicode.GetString(buf, 0, size).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
                    if (ids.Any(x => x.Equals(HardwareId, StringComparison.OrdinalIgnoreCase))) matches.Add(info);
                }
                foreach (SP_DEVINFO_DATA info in matches) action(set, info);
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }

        static bool HasInterface() => InterfacePath() != null;

        /// <summary>
        /// SudoVDA 的裝置介面路徑（以 CreateFile 開啟後送 IOCTL，見 <see cref="SudoVdaVirtualDisplay"/>）；
        /// 驅動沒有安裝或沒有啟動時為 null。很快（不掃描驅動存放區），可在 UI 執行緒呼叫。
        /// </summary>
        public static string? InterfacePath()
        {
            Guid guid = InterfaceGuid;
            IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == INVALID_HANDLE) return null;
            try
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA)) };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, 0, ref data)) return null;
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int needed, IntPtr.Zero);
                if (needed <= 0) return null;
                IntPtr detail = Marshal.AllocHGlobal(needed);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W 的 cbSize：64 位元為 8，32 位元為 6
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, needed, out _, IntPtr.Zero)) return null;
                    return Marshal.PtrToStringUni(detail + 4);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }

        /// <summary>
        /// 驅動存放區中本程式的驅動套件（oemNN.inf）：內容提到 SudoVDA，而且存放區中的目錄檔由本程式的憑證簽章。
        /// </summary>
        static List<string> FindOurPackages()
        {
            var list = new List<string>();
            string infDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
            foreach (string oem in Directory.GetFiles(infDir, "oem*.inf"))
            {
                try
                {
                    if (File.ReadAllText(oem).IndexOf("SudoVDA", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var stored = new StringBuilder(1024);
                    if (!SetupGetInfDriverStoreLocation(oem, IntPtr.Zero, null, stored, stored.Capacity, out _)) continue;
                    string folder = Path.GetDirectoryName(stored.ToString())!;
                    if (Directory.GetFiles(folder, "*.cat").Any(IsSignedByUs)) list.Add(Path.GetFileName(oem));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // 讀不到的套件不是我們的（我們的套件一般使用者也讀得到）
                }
            }
            return list;
        }

        static bool IsSignedByUs(string catalog)
        {
            try
            {
                return X509Certificate.CreateFromSignedFile(catalog).Subject.StartsWith(CertSubjectPrefix, StringComparison.Ordinal);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return false;   // 沒有簽章或無法讀取
            }
        }

        /// <summary>本機「受信任的根憑證」與「受信任的發行者」中本程式的憑證。</summary>
        static List<(StoreName Store, X509Certificate2 Cert)> FindOurCerts()
        {
            var list = new List<(StoreName, X509Certificate2)>();
            foreach (StoreName name in CertStores)
            {
                var store = new X509Store(name, StoreLocation.LocalMachine);
                try
                {
                    store.Open(OpenFlags.ReadOnly);
                    foreach (X509Certificate2 c in store.Certificates)
                        if (c.Subject.StartsWith(CertSubjectPrefix, StringComparison.Ordinal)) list.Add((name, c));
                }
                finally
                {
                    store.Close();
                }
            }
            return list;
        }

        // ================================================================
        //  安裝與移除（需要系統管理員權限）
        // ================================================================

        /// <summary>
        /// 匯入憑證、建立裝置節點、安裝 INF。失敗時丟出 <see cref="DriverSetupException"/>，並盡量復原這次做的步驟
        /// （新匯入的憑證、新建的裝置節點、新加入存放區的驅動套件）。回傳是否需要重新開機。
        /// </summary>
        public static bool Install(string folder)
        {
            List<string> missing = MissingFiles(folder);
            if (missing.Count > 0) throw new DriverSetupException("找不到驅動檔：" + string.Join("、", missing) + "（應該放在 " + folder + "）");
            string inf = Path.GetFullPath(Path.Combine(folder, InfFile));

            Snapshot before = Snapshot.Take();
            if (before.Devices.Any(d => d.Present && !before.IsOurs(d)))
                throw new DriverSetupException("這台電腦已經有其他軟體（例如 Apollo）安裝的 SudoVDA 驅動，請先用那個軟體移除它。");

            var cert = new X509Certificate2(Path.Combine(folder, CerFile));
            var addedCerts = new List<StoreName>();
            IntPtr set = INVALID_HANDLE;
            var node = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
            bool createdNode = false;
            try
            {
                foreach (StoreName name in CertStores)
                {
                    var store = new X509Store(name, StoreLocation.LocalMachine);
                    try
                    {
                        store.Open(OpenFlags.ReadWrite);
                        if (store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false).Count > 0) continue;
                        store.Add(cert);
                        addedCerts.Add(name);
                    }
                    finally
                    {
                        store.Close();
                    }
                }

                // UpdateDriverForPlugAndPlayDevices 只處理存在的裝置：沒有就先建立根列舉的裝置節點
                if (!before.Devices.Any(d => d.Present && before.IsOurs(d)))
                {
                    var classGuid = Guid.Empty;
                    var className = new StringBuilder(256);
                    if (!SetupDiGetINFClass(inf, ref classGuid, className, className.Capacity, out _)) throw Failure("讀取 INF");
                    set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
                    if (set == INVALID_HANDLE) throw Failure("建立裝置節點");
                    if (!SetupDiCreateDeviceInfo(set, className.ToString(), ref classGuid, null, IntPtr.Zero, DICD_GENERATE_ID, ref node))
                        throw Failure("建立裝置節點");
                    byte[] hwid = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
                    if (!SetupDiSetDeviceRegistryProperty(set, ref node, SPDRP_HARDWAREID, hwid, hwid.Length)) throw Failure("建立裝置節點");
                    if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref node)) throw Failure("建立裝置節點");
                    createdNode = true;
                }

                if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HardwareId, inf, INSTALLFLAG_FORCE, out bool reboot))
                    throw Failure("安裝驅動", "詳細紀錄在 " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"INF\setupapi.dev.log"));
                return reboot;
            }
            catch
            {
                // 復原：只撤銷這次做的事，失敗也不蓋掉原本的錯誤
                if (createdNode) SetupDiCallClassInstaller(DIF_REMOVE, set, ref node);
                try
                {
                    foreach (string p in FindOurPackages().Where(p => !before.OurPackages.Contains(p)))
                        SetupUninstallOEMInf(p, SUOI_FORCEDELETE, IntPtr.Zero);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                }
                foreach (StoreName name in addedCerts)
                {
                    var store = new X509Store(name, StoreLocation.LocalMachine);
                    try
                    {
                        store.Open(OpenFlags.ReadWrite);
                        store.Remove(cert);
                    }
                    catch (System.Security.Cryptography.CryptographicException)
                    {
                    }
                    finally
                    {
                        store.Close();
                    }
                }
                throw;
            }
            finally
            {
                if (set != INVALID_HANDLE) SetupDiDestroyDeviceInfoList(set);
            }
        }

        /// <summary>
        /// 移除本程式的裝置、驅動存放區中本程式的驅動套件，以及安裝時匯入的憑證（所有版本）。
        /// 每一步都會嘗試，有任何一步失敗就在最後丟出 <see cref="DriverSetupException"/>。回傳是否需要重新開機。
        /// </summary>
        public static bool Uninstall()
        {
            var errors = new List<string>();
            bool reboot = false;
            HashSet<string> packages = new(FindOurPackages(), StringComparer.OrdinalIgnoreCase);

            ForEachDevice((set, info) =>
            {
                string? infPath = ReadInfPath(set, info);
                if (infPath != null && !packages.Contains(infPath)) return;   // 其他軟體裝的 SudoVDA
                if (DiUninstallDevice(IntPtr.Zero, set, ref info, 0, out bool r)) reboot |= r;
                else errors.Add(Failure("移除裝置").Message);
            });

            foreach (string p in packages)
                if (!SetupUninstallOEMInf(p, SUOI_FORCEDELETE, IntPtr.Zero)) errors.Add(Failure("移除驅動套件 " + p).Message);

            foreach (StoreName name in CertStores)
            {
                var store = new X509Store(name, StoreLocation.LocalMachine);
                try
                {
                    store.Open(OpenFlags.ReadWrite);
                    foreach (X509Certificate2 c in store.Certificates.Cast<X509Certificate2>().ToList())
                        if (c.Subject.StartsWith(CertSubjectPrefix, StringComparison.Ordinal)) store.Remove(c);
                }
                catch (System.Security.Cryptography.CryptographicException e)
                {
                    errors.Add("刪除憑證（" + name + "）失敗：" + e.Message);
                }
                finally
                {
                    store.Close();
                }
            }

            if (errors.Count > 0) throw new DriverSetupException(string.Join("\n", errors));
            return reboot;
        }

        /// <summary>以最後的 Win32 錯誤組成給使用者看的錯誤（例如「安裝驅動失敗：…（0xE000020B）」）。</summary>
        static DriverSetupException Failure(string step, string? more = null)
        {
            int err = Marshal.GetLastWin32Error();
            string text = (uint)err switch
            {
                0xE000020B => "找不到裝置",
                0x800B0109 => "憑證不受信任",
                _ => new Win32Exception(err).Message,
            };
            return new DriverSetupException(step + "失敗：" + text + "（0x" + err.ToString("X8") + "）" + (more != null ? "\n" + more : ""));
        }

        // ================================================================
        //  Win32
        // ================================================================

        static readonly IntPtr INVALID_HANDLE = new(-1);
        const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4, DIGCF_DEVICEINTERFACE = 0x10;
        const uint DICD_GENERATE_ID = 0x1, SPDRP_HARDWAREID = 0x1, DIF_REMOVE = 0x5, DIF_REGISTERDEVICE = 0x19;
        const uint INSTALLFLAG_FORCE = 0x1, SUOI_FORCEDELETE = 0x1;
        const uint DICS_FLAG_GLOBAL = 0x1, DIREG_DRV = 0x2, KEY_READ = 0x20019;
        const uint CR_SUCCESS = 0, DN_STARTED = 0x8, DN_HAS_PROBLEM = 0x400;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwnd, uint flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr hwnd, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA info);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA info, uint property, out uint type, byte[] buffer, int size, out int required);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA info, StringBuilder id, int size, out int required);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SP_DEVINFO_DATA info, uint scope, uint hwProfile, uint keyType, uint access);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetINFClass(string inf, ref Guid classGuid, StringBuilder className, int size, out int required);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwnd);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiCreateDeviceInfo(IntPtr set, string name, ref Guid classGuid, string? description, IntPtr hwnd, uint flags, ref SP_DEVINFO_DATA info);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA info, uint property, byte[] buffer, int size);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref SP_DEVINFO_DATA info);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupUninstallOEMInf(string inf, uint flags, IntPtr reserved);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupGetInfDriverStoreLocation(string fileName, IntPtr altPlatform, string? localeName, StringBuilder buffer, int size, out int required);

        [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwnd, string hardwareId, string infPath, uint flags, out bool reboot);

        [DllImport("newdev.dll", SetLastError = true)]
        static extern bool DiUninstallDevice(IntPtr hwnd, IntPtr set, ref SP_DEVINFO_DATA info, uint flags, out bool reboot);

        [DllImport("cfgmgr32.dll")]
        static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
    }
}
