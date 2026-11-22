using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Windows
{
    /// <summary>
    /// 守護程式（ADR-0006 第 2 層）：處於潛行模式時另外執行的同一個 exe（<c>--guardian</c>），監看主程式的存活與心跳。
    /// 主程式不是正常結束（閃退、被工作管理員結束），或心跳逾時（卡住、被除錯器暫停；見 <see cref="GuardianRules"/>），
    /// 就以自己的協調者讀取 cfg、移除虛擬螢幕、等 Windows 依記憶改完配置、套用復原模式並更新 cfg 的目前模式（<see cref="AppCoordinator.TakeOver"/>），然後結束。
    /// <para>
    /// 與主程式之間以具名的共用記憶體（每個主程式一份，以它的 PID 命名）溝通：心跳時間、守護的代數（0 表示不需要守護）與接手的進度。
    /// 每次開始守護代數加一，代數不同的守護程式自己結束；具名 mutex 保證同時只有一個守護程式在守護或復原，
    /// 新的守護程式等前一個結束才開始。主程式啟動時也等正在復原的守護程式結束（<see cref="WaitForRunningGuardian"/>），避免同時改配置與 cfg。
    /// </para>
    /// <para>
    /// 主程式先啟動一個中繼程序（<c>--guardian-launch</c>），由它啟動守護程式後立刻結束：守護程式因此不是主程式的子程序，
    /// 工作管理員的「結束工作」不會把它一起結束。中繼程序盡量脫離主程式所在的 job object（<c>CREATE_BREAKAWAY_FROM_JOB</c>）。
    /// 兩者都在 <see cref="Program"/> 拿單一執行個體的 mutex 之前處理掉。
    /// </para>
    /// </summary>
    static class RecoveryGuardian
    {
        const string LaunchArg = "--guardian-launch";
        const string GuardArg = "--guardian";
        const string MutexName = @"Local\AutoDisplayMode_Guardian";

        const int PollMs = 250;
        /// <summary>復原完成後留著工作列圖示的時間（讓通知顯示）。</summary>
        const int NotifyMs = 5_000;
        /// <summary>復原的時間上限（含等待 Windows 與套用；主程式等守護程式接手完成的上限也是 60 秒）。</summary>
        const long RecoverLimitMs = 60_000;
        /// <summary>主程式啟動時，等正在復原的守護程式結束的時間上限（比復原的上限加上通知的時間長）。</summary>
        const int StartupWaitMs = 70_000;

        /// <summary>
        /// 處理守護程式相關的命令列參數：中繼程序與守護程式本身。回傳結束代碼；不是這些參數時回傳 null（照常啟動主程式）。
        /// </summary>
        public static int? TryRun(string[] args)
        {
            if (args.Length != 3 || (args[0] != LaunchArg && args[0] != GuardArg)) return null;
            if (!int.TryParse(args[1], out int pid) || !int.TryParse(args[2], out int generation)) return 1;
            try
            {
                if (args[0] == LaunchArg)
                {
                    Spawn(GuardArg + " " + pid + " " + generation, breakaway: false);
                    return 0;
                }
                return Guard(pid, generation);
            }
            catch
            {
                return 1;   // 守護程式失效時由驅動的心跳保險與 Windows 接手（第 3 層）
            }
        }

        /// <summary>主程式啟動時呼叫：上次的主程式意外結束、守護程式還在復原時，等它結束再讀 cfg。</summary>
        public static void WaitForRunningGuardian()
        {
            try
            {
                using var mutex = new Mutex(false, MutexName);
                bool acquired;
                try
                {
                    acquired = mutex.WaitOne(StartupWaitMs);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                if (acquired) mutex.ReleaseMutex();
            }
            catch
            {
                // 等不到也照常啟動
            }
        }

        /// <summary>守護程式本身：等前一個守護程式結束，再監看主程式，必要時接手。</summary>
        static int Guard(int pid, int generation)
        {
            using MemoryMappedFile map = MemoryMappedFile.OpenExisting(SharedState.MapName(pid), MemoryMappedFileRights.ReadWrite);
            using var shared = new SharedState(map);
            using Process? main = OpenProcess(pid);
            using var mutex = new Mutex(false, MutexName);

            while (true)
            {
                if (shared.Generation != generation) return 0;
                try
                {
                    if (mutex.WaitOne(PollMs)) break;
                }
                catch (AbandonedMutexException)
                {
                    break;   // 前一個守護程式異常結束：mutex 已歸這裡
                }
            }

            try
            {
                while (true)
                {
                    bool exited = main == null || main.WaitForExit(0);   // 先讀：見 GuardianRules.Decide
                    switch (GuardianRules.Decide(shared.Generation == generation, exited, SharedState.NowMs() - shared.Heartbeat))
                    {
                        case GuardianVerdict.Quit: return 0;
                        case GuardianVerdict.MainExited: return Recover(shared, hung: false);
                        case GuardianVerdict.MainHung: return Recover(shared, hung: true);
                    }
                    Thread.Sleep(PollMs);
                }
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }

        static Process? OpenProcess(int pid)
        {
            try
            {
                return Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return null;   // 已經結束
            }
        }

        /// <summary>
        /// 接手：以自己的協調者切回復原模式（需要訊息迴圈：套用結束的回呼與每秒的 Tick），完成後留著圖示讓通知顯示，再結束。
        /// </summary>
        static int Recover(SharedState shared, bool hung)
        {
            shared.TakeOver = GuardTakeOver.InProgress;   // 主程式從卡住中恢復時據此停止心跳，等這裡完成再切回

            var ui = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(ui);
            using var tray = new NotifyIcon { Text = TrayApp.AppTitle + "（守護程式）" };
            var clock = new StopwatchClock();
            using var monitors = new WindowsMonitorSystem(ui);
            using var virtualDisplay = new SudoVdaVirtualDisplay(monitors.VirtualDisplayPresent);
            string dir = Path.GetDirectoryName(Application.ExecutablePath)!;
            var app = new AppCoordinator(
                new FileConfigStore(Path.Combine(dir, TrayApp.ConfigFileName)),
                monitors, virtualDisplay, new NoGuard(), new NoUsageSignals(), clock, new RegistryStartup(),
                new BalloonNotifier(tray, TrayApp.AppTitle));
            monitors.ApplyFinished += app.OnSwitchFinished;
            tray.Icon = TrayApp.MakeIcon(TrayApp.ToColor(app.Config.RestoreTarget()?.Color ?? ModeConfig.Palette[0]), filled: false);
            tray.Visible = true;

            using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            long readyAt = -1;
            timer.Tick += delegate
            {
                app.Tick();
                long now = clock.NowMs;
                if (app.ReadyToExit && readyAt < 0)
                {
                    readyAt = now;
                    shared.TakeOver = GuardTakeOver.Done;
                }
                if ((readyAt >= 0 && now - readyAt >= NotifyMs) || now >= RecoverLimitMs) Application.ExitThread();
            };
            ui.Post(_ =>
            {
                app.TakeOver(hung);
                timer.Start();
            }, null);
            Application.Run();
            shared.TakeOver = GuardTakeOver.Done;   // 逾時時也讓主程式不必再等
            tray.Visible = false;
            return 0;
        }

        // ------------------------------------------------------------
        //  啟動程序
        // ------------------------------------------------------------

        /// <summary>
        /// 以 <paramref name="arguments"/> 啟動本程式的另一個程序（不繼承任何 handle、不等它）。
        /// <paramref name="breakaway"/>：盡量脫離目前的 job object；不允許脫離時照常啟動。
        /// </summary>
        internal static void Spawn(string arguments, bool breakaway)
        {
            string exe = Application.ExecutablePath;
            var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)) };
            var command = new StringBuilder("\"" + exe + "\" " + arguments);
            if (!CreateProcess(exe, command, IntPtr.Zero, IntPtr.Zero, false, breakaway ? CREATE_BREAKAWAY_FROM_JOB : 0,
                    IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out PROCESS_INFORMATION pi))
            {
                int error = Marshal.GetLastWin32Error();
                if (breakaway && error == ERROR_ACCESS_DENIED)
                {
                    Spawn(arguments, breakaway: false);
                    return;
                }
                throw new Win32Exception(error);
            }
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
        }

        /// <summary>主程式端：開始守護時啟動中繼程序。</summary>
        internal static void Launch(int pid, int generation) => Spawn(LaunchArg + " " + pid + " " + generation, breakaway: true);

        const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        const int ERROR_ACCESS_DENIED = 5;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo,
            out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);
    }

    /// <summary>
    /// 主程式與守護程式共用的狀態（具名共用記憶體，每個主程式一份）：
    /// 心跳時間（<see cref="NowMs"/>）、守護的代數（0 表示不需要守護）、守護程式接手的進度。
    /// </summary>
    sealed class SharedState : IDisposable
    {
        const int HeartbeatOffset = 0, GenerationOffset = 8, TakeOverOffset = 12;
        public const int Size = 16;

        readonly MemoryMappedViewAccessor view;

        public SharedState(MemoryMappedFile map) => view = map.CreateViewAccessor(0, Size);

        public static string MapName(int pid) => @"Local\AutoDisplayMode_Guardian_" + pid;

        public long Heartbeat
        {
            get => view.ReadInt64(HeartbeatOffset);
            set => view.Write(HeartbeatOffset, value);
        }

        public int Generation
        {
            get => view.ReadInt32(GenerationOffset);
            set => view.Write(GenerationOffset, value);
        }

        public GuardTakeOver TakeOver
        {
            get => (GuardTakeOver)view.ReadInt32(TakeOverOffset);
            set => view.Write(TakeOverOffset, (int)value);
        }

        public void Dispose() => view.Dispose();

        /// <summary>
        /// 兩個程序共用的時鐘（毫秒）：不含睡眠與休眠的時間（QueryUnbiasedInterruptTime），
        /// 否則喚醒後主程式還來不及送心跳，守護程式就會誤判逾時。
        /// </summary>
        public static long NowMs()
        {
            QueryUnbiasedInterruptTime(out ulong t);
            return (long)(t / 10_000);
        }

        [DllImport("kernel32.dll")]
        static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
    }

    /// <summary>主程式與守護程式之間的連結（主程式端）：建立共用記憶體；開始守護時啟動守護程式，之後每秒更新心跳。</summary>
    sealed class GuardianLink : IRecoveryGuard, IDisposable
    {
        readonly int pid = Process.GetCurrentProcess().Id;
        readonly MemoryMappedFile map;
        readonly SharedState shared;
        int generation;
        bool armed;

        public GuardianLink()
        {
            map = MemoryMappedFile.CreateNew(SharedState.MapName(pid), SharedState.Size);
            shared = new SharedState(map);
        }

        public void Arm()
        {
            Beat();
            if (armed) return;
            armed = true;
            shared.TakeOver = GuardTakeOver.None;
            shared.Generation = ++generation;
            try
            {
                RecoveryGuardian.Launch(pid, generation);
            }
            catch
            {
                // 無法啟動守護程式：由驅動的心跳保險與 Windows 接手（第 3 層）
            }
        }

        public void Disarm()
        {
            if (!armed) return;
            armed = false;
            shared.Generation = 0;
        }

        public void Beat() => shared.Heartbeat = SharedState.NowMs();

        public GuardTakeOver TakeOver => shared.TakeOver;

        /// <summary>程式結束時呼叫。還在守護時（例如結束前切回復原模式逾時），守護程式會看到主程式結束而接手。</summary>
        public void Dispose()
        {
            shared.Dispose();
            map.Dispose();
        }
    }

    /// <summary>沒有守護程式（守護程式自己，或無法建立共用記憶體時）。</summary>
    sealed class NoGuard : IRecoveryGuard
    {
        public void Arm() { }
        public void Disarm() { }
        public void Beat() { }
        public GuardTakeOver TakeOver => GuardTakeOver.None;
    }

    /// <summary>守護程式用：沒有任何使用跡象，也不安裝鉤子（只切回復原模式，不檢查切換條件）。</summary>
    sealed class NoUsageSignals : IUsageSignals
    {
        public HookUsage TakeHookUsage() => default;
        public void ResetHookTracking() { }
        public uint LastInputTick => 0;
        public float ReadAudioPeak() => 0;
        public void RequestAudioRefresh() { }
        public void ApplyHookPolicy(InputHookPolicy policy) { }
    }
}
