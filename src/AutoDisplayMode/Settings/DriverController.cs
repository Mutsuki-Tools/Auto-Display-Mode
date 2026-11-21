using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Settings
{
    /// <summary>
    /// 虛擬螢幕驅動的狀態與「安裝驅動」「移除驅動」：設定視窗一個，一般設定分頁與模式分頁共用（按鈕狀態、UAC、錯誤訊息都在這裡）。
    /// 狀態在背景執行緒讀取（約幾百毫秒）。安裝與移除按下就立即執行，不屬於設定副本，不算未套用的變更。
    /// 設定視窗關閉後才完成的讀取或操作，結果直接丟掉。
    /// </summary>
    sealed class DriverController
    {
        readonly IVirtualDisplayDriverInstaller installer;
        readonly Form owner;
        int statusVersion;   // 只採用最新一次讀取的結果

        public DriverController(IVirtualDisplayDriverInstaller installer, Form owner)
        {
            this.installer = installer;
            this.owner = owner;
        }

        /// <summary>狀態或進行中的操作改變（分頁據此更新畫面）。</summary>
        public event Action? Changed;

        /// <summary>最後一次讀到的狀態；還沒讀到時為 null。</summary>
        public DriverStatus? Status { get; private set; }

        /// <summary>進行中的操作：true 安裝中、false 移除中；沒有時為 null。</summary>
        public bool? Running { get; private set; }

        public bool CanInstall => Running == null && Status?.CanInstall == true;

        public bool CanUninstall => Running == null && Status?.CanUninstall == true;

        /// <summary>狀態的文字：已安裝、未安裝、找不到驅動檔、無法使用（操作中時為「安裝中…」「移除中…」；還沒讀到時為「檢查中…」）。</summary>
        public string StateText => Running switch
        {
            true => "安裝中…",
            false => "移除中…",
            _ => Status?.State switch
            {
                null => "檢查中…",
                DriverState.Installed => "已安裝",
                DriverState.NotInstalled => "未安裝",
                DriverState.FilesMissing => "找不到驅動檔",
                _ => "無法使用",
            },
        };

        /// <summary>狀態是錯誤（找不到驅動檔、無法使用），以紅字顯示。</summary>
        public bool IsError => Running == null && Status?.State is DriverState.FilesMissing or DriverState.Unavailable;

        /// <summary>補充說明：操作中時請使用者在 UAC 按「是」，否則為狀態的補充說明（沒有時為空字串）。</summary>
        public string DetailText => Running != null ? "請在 UAC 視窗按「是」，完成後狀態會自動更新。" : Status?.Detail ?? "";

        /// <summary>在背景重新讀取狀態（安裝或移除中不讀）。</summary>
        public async void Refresh()
        {
            if (Running != null) return;
            await ReadStatusAsync();
        }

        async Task ReadStatusAsync()
        {
            int version = ++statusVersion;
            DriverStatus status;
            try
            {
                status = await Task.Run(installer.GetStatus);
            }
            catch (Exception e)
            {
                status = new DriverStatus(DriverState.Unavailable, "無法讀取驅動狀態：" + e.Message, CanInstall: false, CanUninstall: false);
            }
            if (version != statusVersion || owner.IsDisposed) return;
            Status = status;
            Changed?.Invoke();
        }

        /// <summary>
        /// 安裝或移除驅動：立即執行（跳出 UAC），與設定副本的套用無關。完成後重新讀取狀態；失敗或有提醒時跳出訊息。
        /// </summary>
        public async void Run(bool install)
        {
            if (Running != null) return;
            Running = install;
            statusVersion++;   // 忽略進行中的讀取
            Changed?.Invoke();

            DriverOperationResult result;
            try
            {
                result = install ? await installer.InstallAsync() : await installer.UninstallAsync();
            }
            catch (Exception e)
            {
                result = new DriverOperationResult(false, e.Message);
            }
            Running = null;
            if (owner.IsDisposed) return;   // 設定視窗已關閉

            await ReadStatusAsync();
            if (owner.IsDisposed) return;
            string action = install ? "安裝驅動" : "移除驅動";
            if (!result.Succeeded)
                MessageBox.Show(owner, "無法" + action + "：\n" + result.Message, owner.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (result.Message.Length > 0)
                MessageBox.Show(owner, action + "完成。\n" + result.Message, owner.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
