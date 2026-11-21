// AutoDisplayMode - 省電螢幕自動切換工具
// 建置：執行專案根目錄的 build.bat（需要 .NET SDK；產出的 exe 目標為 .NET Framework 4.8，Win10/11 內建）
// 輸出到 release/：AutoDisplayMode.exe、旁邊的 AutoDisplayMode.cfg 與 drivers/sudovda/（整個資料夾就是可攜的程式資料夾）

using System;
using System.Threading;
using System.Windows.Forms;
using AutoDisplayMode.Windows;

namespace AutoDisplayMode
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // 以系統管理員身分重新啟動自己來安裝或移除驅動的子程序：在拿單一執行個體的 mutex 之前處理
            // （主程式正在執行，mutex 已被拿走），而且不建立工作列圖示與協調者（協調者一建立就會讀寫 cfg）
            int? exitCode = SudoVdaDriverInstaller.TryRunElevatedCommand(args);
            if (exitCode != null) return exitCode.Value;

            using var mutex = new Mutex(true, "AutoDisplayMode_SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("Auto Display Mode 已經在執行中，請到工作列右下角的圖示操作。",
                    "Auto Display Mode", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (_, e) =>
            {
                MessageBox.Show("發生錯誤：\n" + e.Exception.Message, "Auto Display Mode",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.Run(new TrayApp());
            GC.KeepAlive(mutex);
            return 0;
        }
    }
}
