using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using AutoDisplayMode.Core;
using Microsoft.Win32;

namespace AutoDisplayMode.Windows
{
    /// <summary>時鐘：程式啟動後經過的毫秒數。</summary>
    sealed class StopwatchClock : IClock
    {
        readonly Stopwatch watch = Stopwatch.StartNew();
        public long NowMs => watch.ElapsedMilliseconds;
    }

    /// <summary>cfg 儲存：exe 旁的 UTF-8（含 BOM）文字檔。</summary>
    sealed class FileConfigStore : IConfigStore
    {
        public FileConfigStore(string path) => Location = path;

        public string Location { get; }

        public string? ReadText() => File.Exists(Location) ? File.ReadAllText(Location, Encoding.UTF8) : null;

        public void WriteText(string text) => File.WriteAllText(Location, text, new UTF8Encoding(true));

        public void CreateBackup() => File.Copy(Location, Location + ".bak", overwrite: true);
    }

    /// <summary>開機啟動：目前使用者登錄檔的 Run 機碼（不需系統管理員權限）。</summary>
    sealed class RegistryStartup : IStartupRegistration
    {
        const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValueName = "AutoDisplayMode";

        static string Command => "\"" + Application.ExecutablePath + "\"";

        /// <summary>有本程式的 Run 值就算開啟，不比對路徑：exe 搬家後首次啟動或轉移舊 cfg 時仍沿用開機啟動。</summary>
        public bool IsEnabled
        {
            get
            {
                try
                {
                    using RegistryKey? k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                    return k?.GetValue(RunValueName) is string v && v.Trim().Length > 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void SetEnabled(bool enabled)
        {
            using RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled) k.SetValue(RunValueName, Command);
            else k.DeleteValue(RunValueName, false);
        }
    }

    /// <summary>通知：工作列圖示的氣球通知。</summary>
    sealed class BalloonNotifier : INotifier
    {
        const int TimeoutMs = 4000;
        readonly NotifyIcon tray;
        readonly string title;

        public BalloonNotifier(NotifyIcon tray, string title)
        {
            this.tray = tray;
            this.title = title;
        }

        public void Notify(string text, NotificationKind kind)
        {
            ToolTipIcon icon = kind switch
            {
                NotificationKind.Warning => ToolTipIcon.Warning,
                NotificationKind.Error => ToolTipIcon.Error,
                _ => ToolTipIcon.Info,
            };
            tray.ShowBalloonTip(TimeoutMs, title, text, icon);
        }
    }
}
