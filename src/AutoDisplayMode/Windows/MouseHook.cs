using System;
using System.Runtime.InteropServices;

namespace AutoDisplayMode.Windows
{
    /// <summary>低階滑鼠鉤子（只在核心要求時掛上）。</summary>
    sealed class MouseHook
    {
        public delegate void MouseCallback(int message, int x, int y, uint flags);
        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string? lpModuleName);

        const int WH_MOUSE_LL = 14;

        readonly HookProc proc;          // 保持參考，避免被 GC 回收
        readonly MouseCallback callback;
        IntPtr handle = IntPtr.Zero;

        public MouseHook(MouseCallback cb)
        {
            callback = cb;
            proc = HookCallback;
        }

        public bool IsInstalled => handle != IntPtr.Zero;

        public void Install()
        {
            if (handle != IntPtr.Zero) return;
            handle = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
        }

        public void Uninstall()
        {
            if (handle == IntPtr.Zero) return;
            UnhookWindowsHookEx(handle);
            handle = IntPtr.Zero;
        }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    // MSLLHOOKSTRUCT: pt.x(0) pt.y(4) mouseData(8) flags(12)
                    int x = Marshal.ReadInt32(lParam, 0);
                    int y = Marshal.ReadInt32(lParam, 4);
                    uint flags = (uint)Marshal.ReadInt32(lParam, 12);
                    callback(wParam.ToInt32(), x, y, flags);
                }
                catch
                {
                    // 鉤子裡絕對不能丟例外
                }
            }
            return CallNextHookEx(handle, nCode, wParam, lParam);
        }
    }
}
