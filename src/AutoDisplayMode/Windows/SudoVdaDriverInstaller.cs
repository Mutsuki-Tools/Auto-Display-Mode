using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Windows
{
    /// <summary>
    /// 虛擬螢幕驅動的安裝與移除：以系統管理員身分重新啟動本程式（<c>--install-driver</c>／<c>--uninstall-driver</c>，跳出 UAC），
    /// 由子程序呼叫 <see cref="SudoVdaSetup"/>，結果寫進暫存檔交回。
    /// <para>
    /// 子程序在 <see cref="Program"/> 拿單一執行個體的 mutex 之前就處理掉（見 <see cref="TryRunElevatedCommand"/>），
    /// 不建立工作列圖示，也不建立協調者（協調者一建立就會讀寫 cfg）。
    /// </para>
    /// </summary>
    sealed class SudoVdaDriverInstaller : IVirtualDisplayDriverInstaller
    {
        const string InstallArg = "--install-driver";
        const string UninstallArg = "--uninstall-driver";
        const string ResultArg = "--result=";
        const int ErrorCancelled = 1223;   // 使用者在 UAC 按「否」
        const int InstallSettleMs = 10000; // 安裝後等裝置啟動、出現裝置介面的時間上限

        public SudoVdaDriverInstaller(string packageFolder) => PackageFolder = packageFolder;

        public string PackageFolder { get; }

        public DriverStatus GetStatus() => SudoVdaSetup.GetStatus(PackageFolder);

        public async Task<DriverOperationResult> InstallAsync()
        {
            DriverOperationResult r = await RunElevatedAsync(InstallArg);
            if (!r.Succeeded) return r;

            // 驅動裝好後，裝置要一點時間才會啟動並出現裝置介面
            DriverStatus status = await Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                while (true)
                {
                    DriverStatus s = GetStatus();
                    if (s.State == DriverState.Installed || watch.ElapsedMilliseconds >= InstallSettleMs) return s;
                    Thread.Sleep(500);
                }
            });
            if (status.State == DriverState.Installed || r.Message.Length > 0) return r;   // 需要重新開機時，無法使用是預期中的
            return new DriverOperationResult(true, "驅動已安裝，但還無法使用：\n" + status.Detail);
        }

        public Task<DriverOperationResult> UninstallAsync() => RunElevatedAsync(UninstallArg);

        /// <summary>以系統管理員身分執行本程式的 <paramref name="command"/>，等它結束（不卡住 UI），讀回結果。</summary>
        static async Task<DriverOperationResult> RunElevatedAsync(string command)
        {
            string resultFile = Path.Combine(Path.GetTempPath(), "AutoDisplayMode-driver-" + Guid.NewGuid().ToString("N") + ".txt");
            var psi = new ProcessStartInfo(Application.ExecutablePath, command + " \"" + ResultArg + resultFile + "\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                ErrorDialog = false,
                ErrorDialogParentHandle = Form.ActiveForm?.Handle ?? IntPtr.Zero,   // UAC 視窗出現在設定視窗前面
                WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath)!,
            };

            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
            {
                return new DriverOperationResult(false, "已取消：沒有取得系統管理員權限，驅動沒有變更。");
            }
            catch (Win32Exception e)
            {
                return new DriverOperationResult(false, "無法以系統管理員身分執行：" + e.Message);
            }
            if (process == null) return new DriverOperationResult(false, "無法以系統管理員身分執行。");

            int exitCode;
            using (process)
            {
                exitCode = await Task.Run(() =>
                {
                    process.WaitForExit();
                    return process.ExitCode;
                });
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(resultFile, Encoding.UTF8);
                File.Delete(resultFile);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return new DriverOperationResult(false, "沒有收到結果（結束代碼 " + exitCode + "）。");
            }

            string code = lines.FirstOrDefault() ?? "";
            string message = string.Join("\n", lines.Skip(1)).Trim();
            return code switch
            {
                "OK" => new DriverOperationResult(true, ""),
                "REBOOT" => new DriverOperationResult(true, "需要重新開機才能完成。"),
                _ => new DriverOperationResult(false, message.Length > 0 ? message : "失敗（結束代碼 " + exitCode + "）。"),
            };
        }

        /// <summary>
        /// 程式一啟動就呼叫：參數是 <c>--install-driver</c> 或 <c>--uninstall-driver</c> 時，在這個（已提權的）程序中執行並回傳結束代碼；
        /// 不是時回傳 null，照常啟動。驅動檔一律從本 exe 旁邊的 drivers\sudovda\ 找。
        /// </summary>
        public static int? TryRunElevatedCommand(string[] args)
        {
            if (args.Length == 0 || (args[0] != InstallArg && args[0] != UninstallArg)) return null;
            string? resultFile = args.Skip(1).FirstOrDefault(a => a.StartsWith(ResultArg, StringComparison.Ordinal))?.Substring(ResultArg.Length);
            string folder = DefaultPackageFolder();

            string code, message = "";
            try
            {
                if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new DriverSetupException("沒有系統管理員權限。");
                if (!Environment.Is64BitProcess)
                    throw new DriverSetupException("只支援 64 位元的 Windows。");
                bool reboot = args[0] == InstallArg ? SudoVdaSetup.Install(folder) : SudoVdaSetup.Uninstall();
                code = reboot ? "REBOOT" : "OK";
            }
            catch (DriverSetupException e)
            {
                code = "FAIL";
                message = e.Message;
            }
            catch (Exception e)
            {
                code = "FAIL";
                message = e.GetType().Name + "：" + e.Message;
            }

            if (resultFile != null)
            {
                try
                {
                    File.WriteAllText(resultFile, code + "\n" + message, new UTF8Encoding(false));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // 寫不出結果：父程序會回報「沒有收到結果」
                }
            }
            return code == "FAIL" ? 1 : 0;
        }

        /// <summary>exe 所在資料夾\drivers\sudovda。</summary>
        public static string DefaultPackageFolder() =>
            Path.Combine(Path.GetDirectoryName(Application.ExecutablePath)!, "drivers", "sudovda");
    }
}
