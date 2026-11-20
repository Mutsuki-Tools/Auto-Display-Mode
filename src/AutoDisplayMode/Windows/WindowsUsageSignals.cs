using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Windows
{
    /// <summary>使用訊號的 Windows 實作：低階滑鼠鉤子、GetLastInputInfo、Core Audio 峰值。</summary>
    sealed class WindowsUsageSignals : IUsageSignals, IDisposable
    {
        const int WM_MOUSEMOVE = 0x0200;
        const uint LLMHF_INJECTED = 0x01;
        const uint LLMHF_LOWER_IL_INJECTED = 0x02;
        /// <summary>Windows 可能在系統忙碌時默默移除鉤子，每 600 次（約 10 分鐘）重新掛一次保險。</summary>
        const int RehookEveryCalls = 600;

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        readonly IClock clock;
        readonly MouseHook hook;
        readonly AudioMonitor? audio;

        MouseHookPolicy policy;
        bool mouseUsage;          // 上次讀取以來是否有實體滑鼠動作
        bool isRemoteSession;     // 是否為 Windows 遠端桌面 (RDP) 工作階段
        bool hasAnchor;
        int anchorX, anchorY;
        int hookAgeCalls;

        public WindowsUsageSignals(IClock clock)
        {
            this.clock = clock;
            try { audio = new AudioMonitor(); }
            catch { audio = null; }
            hook = new MouseHook(OnMouse);
        }

        public uint LastInputTick
        {
            get
            {
                var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
                return GetLastInputInfo(ref lii) ? lii.dwTime : 0;
            }
        }

        public bool TakeMouseUsage()
        {
            isRemoteSession = SystemInformation.TerminalServerSession;
            bool r = mouseUsage;
            mouseUsage = false;
            return r;
        }

        public void ResetMouseTracking()
        {
            mouseUsage = false;
            hasAnchor = false;
        }

        public float ReadAudioPeak()
        {
            if (audio == null) return 0f;
            try { return audio.GetPeak(); }
            catch { return 0f; }
        }

        public void RequestAudioRefresh() => audio?.RequestRefresh();

        public void ApplyHookPolicy(MouseHookPolicy p)
        {
            policy = p;
            if (p.Install)
            {
                if (!hook.IsInstalled)
                {
                    hook.Install();
                    hasAnchor = false;
                    hookAgeCalls = 0;
                }
                else if (++hookAgeCalls >= RehookEveryCalls)
                {
                    hook.Uninstall();
                    hook.Install();
                    hookAgeCalls = 0;
                }
            }
            else if (hook.IsInstalled)
            {
                hook.Uninstall();
            }
        }

        void OnMouse(int msg, int x, int y, uint flags)
        {
            if (clock.NowMs < policy.IgnoreUntilMs)
            {
                hasAnchor = false;
                return;
            }

            if (policy.IgnoreRemoteInput &&
                (isRemoteSession || (flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0))
                return;

            if (msg == WM_MOUSEMOVE)
            {
                if (!hasAnchor)
                {
                    anchorX = x;
                    anchorY = y;
                    hasAnchor = true;
                    return;
                }
                long dx = x - anchorX;
                long dy = y - anchorY;
                long th = policy.MoveThreshold;
                if (dx * dx + dy * dy >= th * th)
                {
                    mouseUsage = true;
                    anchorX = x;
                    anchorY = y;
                }
            }
            else
            {
                // 點擊、滾輪
                mouseUsage = true;
            }
        }

        public void Dispose()
        {
            hook.Uninstall();
            audio?.Dispose();
        }
    }
}
