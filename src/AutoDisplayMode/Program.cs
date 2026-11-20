// AutoDisplayMode - 省電螢幕自動切換工具
// 建置：執行專案根目錄的 build.bat（需要 .NET SDK；產出的 exe 目標為 .NET Framework 4.8，Win10/11 內建）

using System;
using System.Threading;
using System.Windows.Forms;

namespace AutoDisplayMode
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            using var mutex = new Mutex(true, "AutoDisplayMode_SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("省電螢幕工具已經在執行中，請到工作列右下角的圖示操作。",
                    "省電螢幕工具", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (_, e) =>
            {
                MessageBox.Show("發生錯誤：\n" + e.Exception.Message, "省電螢幕工具",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.Run(new TrayApp());
            GC.KeepAlive(mutex);
        }
    }
}
