// 核心對外的介面（ports）：由 Windows 轉接層實作，測試以假物件實作。
// 核心只透過這些介面與外界互動，本身不引用任何 Windows API、WinForms 或登錄檔。

using System.Collections.Generic;

namespace AutoDisplayMode.Core
{
    /// <summary>時鐘：單調遞增的毫秒時間。</summary>
    public interface IClock
    {
        long NowMs { get; }
    }

    /// <summary>
    /// 螢幕切換執行。此階段由轉接層呼叫 bat 完成。
    /// 切換在背景進行，結束後轉接層須在 UI 執行緒呼叫 <see cref="AppCoordinator.OnSwitchFinished"/>。
    /// </summary>
    public interface IDisplaySwitcher
    {
        /// <summary>目前的設定是否能執行切換（此階段：bat 檔是否存在）。</summary>
        bool IsReady(AppConfig config);

        /// <summary>開始在背景切換到 <paramref name="target"/>。</summary>
        void Begin(Mode target, AppConfig config);
    }

    /// <summary>螢幕系統：偵測目前接上的螢幕。（套用螢幕狀態在之後的版本加入，此階段切換仍透過 <see cref="IDisplaySwitcher"/>。）</summary>
    public interface IMonitorSystem
    {
        /// <summary>列出目前接上的每台螢幕（包含接上但關閉的）。偵測失敗時丟出例外。</summary>
        IReadOnlyList<DetectedMonitor> Detect();
    }

    /// <summary>
    /// 使用訊號：提供滑鼠或鍵盤、聲音的使用跡象，並依核心的指令安裝或移除滑鼠鉤子。
    /// </summary>
    public interface IUsageSignals
    {
        /// <summary>
        /// 自上次呼叫以來，滑鼠鉤子是否偵測到實體滑鼠使用（已經過移動門檻與遠端過濾）。呼叫後清除。
        /// </summary>
        bool TakeMouseUsage();

        /// <summary>清除累積的滑鼠使用，並重設移動門檻的起點。</summary>
        void ResetMouseTracking();

        /// <summary>Windows 最後輸入時間（任意單位的計數，只用來比較是否改變）。</summary>
        uint LastInputTick { get; }

        /// <summary>所有播放裝置目前的聲音峰值（0~1）；無法讀取時回傳 0。</summary>
        float ReadAudioPeak();

        /// <summary>要求下次讀取聲音峰值前重新列舉播放裝置。</summary>
        void RequestAudioRefresh();

        /// <summary>告知目前需要的滑鼠鉤子狀態；每秒至少呼叫一次。</summary>
        void ApplyHookPolicy(MouseHookPolicy policy);
    }

    /// <summary>
    /// 滑鼠鉤子的需求。
    /// </summary>
    /// <param name="Install">是否需要安裝滑鼠鉤子。</param>
    /// <param name="IgnoreUntilMs">在此時間（<see cref="IClock"/> 的毫秒）之前，鉤子收到的輸入都不算使用，並重設移動起點。</param>
    /// <param name="MoveThreshold">滑鼠要移動超過幾個像素才算使用。</param>
    /// <param name="IgnoreRemoteInput">是否忽略遠端連線軟體送出的滑鼠輸入。</param>
    public readonly record struct MouseHookPolicy(bool Install, long IgnoreUntilMs, int MoveThreshold, bool IgnoreRemoteInput);

    /// <summary>cfg 儲存：讀取／寫入 cfg 文字。</summary>
    public interface IConfigStore
    {
        /// <summary>cfg 檔的位置（顯示給使用者看）。</summary>
        string Location { get; }

        /// <summary>讀取 cfg 全文；檔案不存在時回傳 null。讀取失敗時丟出例外。</summary>
        string? ReadText();

        /// <summary>寫入 cfg 全文（UTF-8 含 BOM）。寫入失敗時丟出例外。</summary>
        void WriteText(string text);

        /// <summary>把目前的 cfg 檔原封不動複製為 <c>.bak</c>（覆蓋舊的備份）。失敗時丟出例外。</summary>
        void CreateBackup();
    }

    /// <summary>開機啟動：目前使用者登入時是否自動開啟本程式。</summary>
    public interface IStartupRegistration
    {
        bool IsEnabled { get; }

        /// <summary>修改開機啟動設定；失敗時丟出例外。</summary>
        void SetEnabled(bool enabled);
    }

    public enum NotificationKind
    {
        Info,
        Warning,
        Error,
    }

    /// <summary>通知：顯示氣球通知。</summary>
    public interface INotifier
    {
        void Notify(string text, NotificationKind kind);
    }
}
