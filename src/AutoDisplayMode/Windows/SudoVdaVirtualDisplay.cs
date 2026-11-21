using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using AutoDisplayMode.Core;
using Microsoft.Win32.SafeHandles;

namespace AutoDisplayMode.Windows
{
    /// <summary>
    /// 虛擬螢幕的 Windows 實作：以 IOCTL 操作 SudoVDA 驅動（ADR-0005；做法同票 02 的 VddProbe）。
    /// 從 SetupAPI 找到驅動的裝置介面，CreateFile 開啟後以 DeviceIoControl 新增、移除虛擬螢幕與送心跳。
    /// 不需要系統管理員權限。驅動超過約 3 秒沒收到任何 IOCTL 就移除所有虛擬螢幕（心跳保險）。
    /// 虛擬螢幕是否存在由螢幕系統判斷（介面卡的硬體 ID，見 <see cref="WindowsMonitorSystem.VirtualDisplayPresent"/>）。
    /// <para>
    /// 心跳：協調者每秒從 UI 執行緒呼叫 <see cref="Ping"/>。但 SetDisplayConfig 開關螢幕時（在背景執行緒），UI 執行緒的訊息處理
    /// 也會卡住（實測開啟兩台實體螢幕時卡了約 2.5 秒），再加上每秒一次的間隔就超過驅動的 3 秒。所以另有一條心跳執行緒每 0.5 秒送一次，
    /// 但只在 UI 執行緒最近 <see cref="UiGraceMs"/> 內呼叫過 <see cref="Ping"/> 時才送：介面短暫卡住不會讓虛擬螢幕消失，
    /// 介面真的卡死時心跳仍會停止（最多 <see cref="UiGraceMs"/> 加上驅動的 3 秒）。
    /// </para>
    /// </summary>
    sealed class SudoVdaVirtualDisplay : IVirtualDisplay, IDisposable
    {
        /// <summary>UI 執行緒多久沒呼叫 <see cref="Ping"/>，心跳執行緒就停止送心跳。</summary>
        const long UiGraceMs = 5_000;
        const int HeartbeatIntervalMs = 500;

        const uint IOCTL_ADD = (0x22 << 16) | (0x800 << 2);
        const uint IOCTL_REMOVE = (0x22 << 16) | (0x801 << 2);
        const uint IOCTL_PING = (0x22 << 16) | (0x888 << 2);
        const int ERROR_FILE_NOT_FOUND = 2;
        const int ERROR_NOT_FOUND = 1168;

        /// <summary>本程式的虛擬螢幕的識別（驅動以它區分各個虛擬螢幕；移除時指定）。</summary>
        static readonly Guid MonitorGuid = new("2f6c1b0e-8d4a-4f57-9a63-5e0b7c2d9a14");
        /// <summary>虛擬螢幕的名稱（EDID 中，「設定 → 顯示器」看得到）與序號。</summary>
        const string MonitorName = "AutoDisplay", MonitorSerial = "ADM0001";

        readonly Func<bool> exists;
        readonly object gate = new();   // 保護 device：UI 執行緒與心跳執行緒都會用
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        SafeFileHandle? device;          // 建立後開著，移除時關閉
        long lastUiPingMs;               // UI 執行緒最後一次呼叫 Ping（或建立）的時間
        Thread? heartbeat;               // 心跳執行緒；沒有虛擬螢幕時為 null
        volatile bool heartbeatOn;

        /// <param name="exists">本程式的虛擬螢幕目前是否出現在 Windows 的顯示設定中。</param>
        public SudoVdaVirtualDisplay(Func<bool> exists) => this.exists = exists;

        public bool Exists => exists();

        public void Create(VideoMode mode)
        {
            // 先開啟驅動（之後的心跳要用），已經存在時（例如上一個程式留下的）才沿用
            SafeFileHandle handle;
            lock (gate)
            {
                handle = device ??= Open() ?? throw new InvalidOperationException(
                    "找不到虛擬螢幕驅動。請在設定視窗的「一般設定」分頁安裝驅動（安裝後如果仍是這樣，可能需要重新開機）。");
            }
            if (Exists)
            {
                StartHeartbeat();
                return;
            }

            var input = new byte[56];
            BitConverter.GetBytes((uint)mode.Width).CopyTo(input, 0);
            BitConverter.GetBytes((uint)mode.Height).CopyTo(input, 4);
            BitConverter.GetBytes((uint)mode.RefreshRate).CopyTo(input, 8);
            MonitorGuid.ToByteArray().CopyTo(input, 12);
            Encoding.ASCII.GetBytes(MonitorName).CopyTo(input, 28);
            Encoding.ASCII.GetBytes(MonitorSerial).CopyTo(input, 42);
            try
            {
                lock (gate) Io(handle, IOCTL_ADD, input, 12);
            }
            catch (Win32Exception e)
            {
                throw new InvalidOperationException("無法建立虛擬螢幕：" + e.Message + "（" + e.NativeErrorCode + "）", e);
            }
            StartHeartbeat();
        }

        public void Remove()
        {
            StopHeartbeat();
            lock (gate)
            {
                SafeFileHandle? handle = device ?? Open();
                if (handle == null) return;   // 驅動不在了：虛擬螢幕也不會在
                try
                {
                    Io(handle, IOCTL_REMOVE, MonitorGuid.ToByteArray(), 0);
                }
                catch (Win32Exception e) when (e.NativeErrorCode is ERROR_NOT_FOUND or ERROR_FILE_NOT_FOUND)
                {
                    // 已經被移除（例如心跳保險）
                }
                finally
                {
                    handle.Dispose();
                    device = null;
                }
            }
        }

        public void Ping()
        {
            Interlocked.Exchange(ref lastUiPingMs, clock.ElapsedMilliseconds);
            lock (gate)
            {
                if (device == null) throw new InvalidOperationException("虛擬螢幕驅動沒有開啟");
                Io(device, IOCTL_PING, null, 0);
            }
        }

        public void Dispose()
        {
            StopHeartbeat();
            lock (gate)
            {
                device?.Dispose();
                device = null;
            }
        }

        /// <summary>開始（或繼續）心跳執行緒，並把現在當成 UI 執行緒最後一次送心跳的時間。</summary>
        void StartHeartbeat()
        {
            Interlocked.Exchange(ref lastUiPingMs, clock.ElapsedMilliseconds);
            if (heartbeat != null) return;
            heartbeatOn = true;
            heartbeat = new Thread(HeartbeatLoop) { IsBackground = true, Name = "虛擬螢幕心跳" };
            heartbeat.Start();
        }

        void StopHeartbeat()
        {
            heartbeatOn = false;
            heartbeat?.Join(2 * HeartbeatIntervalMs);
            heartbeat = null;
        }

        /// <summary>每 0.5 秒送一次心跳，但 UI 執行緒超過 <see cref="UiGraceMs"/> 沒呼叫 <see cref="Ping"/> 就不送（介面卡死時讓驅動移除虛擬螢幕）。</summary>
        void HeartbeatLoop()
        {
            while (heartbeatOn)
            {
                Thread.Sleep(HeartbeatIntervalMs);
                if (!heartbeatOn || clock.ElapsedMilliseconds - Interlocked.Read(ref lastUiPingMs) > UiGraceMs) continue;
                lock (gate)
                {
                    try
                    {
                        if (device != null) Io(device, IOCTL_PING, null, 0);
                    }
                    catch
                    {
                        // 虛擬螢幕不見了由協調者的一致性檢查處理
                    }
                }
            }
        }

        /// <summary>開啟驅動的裝置介面；驅動沒有安裝或沒有啟動時回傳 null。</summary>
        static SafeFileHandle? Open()
        {
            string? path = SudoVdaSetup.InterfacePath();
            if (path == null) return null;
            SafeFileHandle h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (!h.IsInvalid) return h;
            h.Dispose();
            return null;
        }

        static void Io(SafeFileHandle handle, uint code, byte[]? input, int outSize)
        {
            var output = new byte[Math.Max(outSize, 1)];
            if (!DeviceIoControl(handle, code, input, input?.Length ?? 0, output, outSize, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize, byte[] output, int outputSize,
            out int returned, IntPtr overlapped);
    }
}
