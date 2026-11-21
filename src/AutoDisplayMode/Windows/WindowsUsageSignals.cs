using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Windows
{
    /// <summary>使用訊號的 Windows 實作：低階滑鼠與鍵盤鉤子、GetLastInputInfo、Core Audio 峰值。</summary>
    sealed class WindowsUsageSignals : IUsageSignals, IDisposable
    {
        const int WM_MOUSEMOVE = 0x0200;
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;
        const uint LLMHF_INJECTED = 0x01;
        const uint LLMHF_LOWER_IL_INJECTED = 0x02;
        const uint LLKHF_LOWER_IL_INJECTED = 0x02;
        const uint LLKHF_INJECTED = 0x10;
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
        readonly LowLevelHook mouseHook;
        readonly LowLevelHook keyboardHook;
        readonly AudioMonitor? audio;

        InputHookPolicy policy;
        bool hookUsage;           // 上次讀取以來是否有實體的滑鼠或鍵盤動作
        bool isRemoteSession;     // 是否為 Windows 遠端桌面 (RDP) 工作階段
        bool hasAnchor;
        int anchorX, anchorY;
        int hookAgeCalls;

        public WindowsUsageSignals(IClock clock)
        {
            this.clock = clock;
            try { audio = new AudioMonitor(); }
            catch { audio = null; }
            mouseHook = new LowLevelHook(LowLevelHook.WH_MOUSE_LL, OnMouse);
            keyboardHook = new LowLevelHook(LowLevelHook.WH_KEYBOARD_LL, OnKeyboard);
        }

        public uint LastInputTick
        {
            get
            {
                var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
                return GetLastInputInfo(ref lii) ? lii.dwTime : 0;
            }
        }

        public bool TakeHookUsage()
        {
            isRemoteSession = SystemInformation.TerminalServerSession;
            bool r = hookUsage;
            hookUsage = false;
            return r;
        }

        public void ResetHookTracking()
        {
            hookUsage = false;
            hasAnchor = false;
        }

        public float ReadAudioPeak()
        {
            if (audio == null) return 0f;
            try { return audio.GetPeak(); }
            catch { return 0f; }
        }

        public void RequestAudioRefresh() => audio?.RequestRefresh();

        public void ApplyHookPolicy(InputHookPolicy p)
        {
            policy = p;
            if (p.Install)
            {
                if (!mouseHook.IsInstalled || !keyboardHook.IsInstalled)
                {
                    mouseHook.Install();
                    keyboardHook.Install();
                    hasAnchor = false;
                    hookAgeCalls = 0;
                }
                else if (++hookAgeCalls >= RehookEveryCalls)
                {
                    mouseHook.Uninstall();
                    mouseHook.Install();
                    keyboardHook.Uninstall();
                    keyboardHook.Install();
                    hookAgeCalls = 0;
                }
            }
            else
            {
                mouseHook.Uninstall();
                keyboardHook.Uninstall();
            }
        }

        /// <summary>鉤子收到的輸入是否要忽略：忽略期間內，或（設定忽略遠端輸入時）遠端桌面工作階段、注入的輸入。</summary>
        bool Ignored(bool injected)
        {
            if (clock.NowMs < policy.IgnoreUntilMs)
            {
                hasAnchor = false;
                return true;
            }
            return policy.IgnoreRemoteInput && (isRemoteSession || injected);
        }

        void OnMouse(int msg, IntPtr data)
        {
            // MSLLHOOKSTRUCT: pt.x(0) pt.y(4) mouseData(8) flags(12)
            int x = Marshal.ReadInt32(data, 0);
            int y = Marshal.ReadInt32(data, 4);
            uint flags = (uint)Marshal.ReadInt32(data, 12);
            if (Ignored((flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0)) return;

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
                    hookUsage = true;
                    anchorX = x;
                    anchorY = y;
                }
            }
            else
            {
                // 點擊、滾輪
                hookUsage = true;
            }
        }

        void OnKeyboard(int msg, IntPtr data)
        {
            if (msg != WM_KEYDOWN && msg != WM_SYSKEYDOWN) return;
            // KBDLLHOOKSTRUCT: vkCode(0) scanCode(4) flags(8) time(12)
            uint flags = (uint)Marshal.ReadInt32(data, 8);
            if (Ignored((flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0)) return;
            hookUsage = true;
        }

        public void Dispose()
        {
            mouseHook.Uninstall();
            keyboardHook.Uninstall();
            audio?.Dispose();
        }
    }
}
