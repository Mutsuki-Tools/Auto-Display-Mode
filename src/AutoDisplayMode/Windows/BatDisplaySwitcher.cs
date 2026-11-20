using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Windows
{
    /// <summary>螢幕切換執行：在背景呼叫 bat，結束後在 UI 執行緒觸發 <see cref="Finished"/>。</summary>
    sealed class BatDisplaySwitcher : IDisplaySwitcher
    {
        readonly SynchronizationContext ui;

        public BatDisplaySwitcher(SynchronizationContext ui) => this.ui = ui;

        /// <summary>切換結束；參數為錯誤訊息，成功時為 null。</summary>
        public event Action<string?>? Finished;

        public bool IsReady(AppConfig config) =>
            !string.IsNullOrEmpty(config.BatPath) && File.Exists(config.BatPath);

        public void Begin(Mode target, AppConfig config)
        {
            string bat = config.BatPath;
            var psi = new ProcessStartInfo("cmd.exe", "/c \"\"" + bat + "\" " + config.ArgsFor(target) + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(bat),
            };

            ThreadPool.QueueUserWorkItem(_ =>
            {
                string? error = null;
                try
                {
                    using Process p = Process.Start(psi);
                    if (!p.WaitForExit(60_000))
                    {
                        try { p.Kill(); } catch { }
                        error = "bat 執行超過 60 秒沒有結束，已強制停止。";
                    }
                }
                catch (Exception ex)
                {
                    error = "無法執行 bat：" + ex.Message;
                }
                ui.Post(_ => Finished?.Invoke(error), null);
            });
        }
    }
}
