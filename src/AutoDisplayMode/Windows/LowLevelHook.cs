using System;
using System.Runtime.InteropServices;

namespace AutoDisplayMode.Windows
{
    /// <summary>低階滑鼠鉤子或低階鍵盤鉤子（每個執行個體只掛其中一種；只在核心要求時掛上）。</summary>
    sealed class LowLevelHook
    {
        /// <summary>收到一筆輸入：視窗訊息（WM_MOUSEMOVE、WM_KEYDOWN 等）與 MSLLHOOKSTRUCT／KBDLLHOOKSTRUCT 的指標。</summary>
        public delegate void Callback(int message, IntPtr data);
        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string? lpModuleName);

        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;

        readonly int hookType;
        readonly HookProc proc;          // 保持參考，避免被 GC 回收
        readonly Callback callback;
        IntPtr handle = IntPtr.Zero;

        /// <param name="hookType"><see cref="WH_MOUSE_LL"/> 或 <see cref="WH_KEYBOARD_LL"/>。</param>
        public LowLevelHook(int hookType, Callback cb)
        {
            this.hookType = hookType;
            callback = cb;
            proc = HookCallback;
        }

        public bool IsInstalled => handle != IntPtr.Zero;

        public void Install()
        {
            if (handle != IntPtr.Zero) return;
            handle = SetWindowsHookEx(hookType, proc, GetModuleHandle(null), 0);
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
                    callback(wParam.ToInt32(), lParam);
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
