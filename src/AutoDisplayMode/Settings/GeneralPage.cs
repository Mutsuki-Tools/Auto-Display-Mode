using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Settings
{
    /// <summary>
    /// 「一般設定」分頁：滑鼠移動門檻、聲音音量門檻、聲音持續秒數、忽略遠端輸入、自動切換通知、開機登入時自動開啟，
    /// 以及虛擬螢幕驅動的狀態與「安裝驅動」「移除驅動」（<see cref="DriverController"/>，與模式分頁共用）。
    /// 驅動的安裝與移除按下就立即執行，不屬於設定副本，不算未套用的變更（不呼叫 <see cref="SettingsPage.MarkEdited"/>）。
    /// </summary>
    sealed class GeneralPage : SettingsPage
    {
        readonly NumericUpDown mouseThreshold;
        readonly NumericUpDown audioThreshold;
        readonly NumericUpDown audioSeconds;
        readonly CheckBox ignoreRemote;
        readonly CheckBox notifications;
        readonly CheckBox startWithWindows;
        readonly DriverController driver;
        readonly Label driverState;
        readonly Label driverDetail;
        readonly Button installDriver;
        readonly Button uninstallDriver;
        AppConfig draft = new();

        public GeneralPage(DriverController driver) : base("一般設定")
        {
            this.driver = driver;
            // 欄位範圍與讀取 cfg 時的正規化一致（AppConfig 的 Min／Max 常數）
            mouseThreshold = Number(AppConfig.MinMouseMoveThreshold, AppConfig.MaxMouseMoveThreshold, 0, 1);
            audioThreshold = Number((decimal)AppConfig.MinAudioThreshold, (decimal)AppConfig.MaxAudioThreshold, 3, 0.001m);
            audioSeconds = Number(AppConfig.MinAudioSeconds, AppConfig.MaxAudioSeconds, 0, 1);
            ignoreRemote = Check("偵測到使用時，忽略遠端連線軟體送出的鍵盤滑鼠輸入");
            notifications = Check("自動切換時跳出通知");
            startWithWindows = Check("開機登入時自動開啟");

            mouseThreshold.ValueChanged += delegate { draft.MouseMoveThreshold = (int)mouseThreshold.Value; MarkEdited(); };
            audioThreshold.ValueChanged += delegate { draft.AudioThreshold = (double)audioThreshold.Value; MarkEdited(); };
            audioSeconds.ValueChanged += delegate { draft.AudioSeconds = (int)audioSeconds.Value; MarkEdited(); };
            ignoreRemote.CheckedChanged += delegate { draft.IgnoreRemoteInput = ignoreRemote.Checked; MarkEdited(); };
            notifications.CheckedChanged += delegate { draft.ShowNotifications = notifications.Checked; MarkEdited(); };
            startWithWindows.CheckedChanged += delegate { draft.StartWithWindows = startWithWindows.Checked; MarkEdited(); };

            driverState = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Text = "檢查中…" };
            driverDetail = Hint("");
            driverDetail.Visible = false;
            installDriver = DriverButton("安裝驅動");
            uninstallDriver = DriverButton("移除驅動");
            installDriver.Click += delegate { driver.Run(install: true); };
            uninstallDriver.Click += delegate { driver.Run(install: false); };
            driver.Changed += ShowDriverStatus;

            AutoScroll = true;   // 視窗縮小時整頁捲動
            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3,
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            void Row(string label, NumericUpDown input, string unit, string hint)
            {
                table.Controls.Add(Caption(label));
                table.Controls.Add(input);
                table.Controls.Add(Caption(unit));
                Label h = Hint(hint);
                table.Controls.Add(h);
                table.SetColumnSpan(h, 3);
            }

            void Wide(Control c)
            {
                table.Controls.Add(c);
                table.SetColumnSpan(c, 3);
            }

            Wide(Section("使用偵測"));
            Row("滑鼠移動門檻：", mouseThreshold, "像素", "滑鼠要移動超過這個距離才算使用，過濾桌面震動造成的微小位移。");
            Row("聲音音量門檻：", audioThreshold, "（0～1）", "聲音峰值超過這個值才算有聲音。");
            Row("聲音持續秒數：", audioSeconds, "秒", "聲音要連續播放這麼久才算一次使用，過濾通知音這類短暫聲音。");
            Wide(ignoreRemote);
            Wide(Hint("開啟時，遠端操作不會觸發「偵測到使用」；閒置計時仍會被遠端操作打斷。\n" +
                      "潛行模式的內建切換條件不受這個設定影響，一律只認實體鍵盤滑鼠。"));

            Wide(Section("其他"));
            Wide(notifications);
            Wide(startWithWindows);

            Wide(Section("虛擬螢幕驅動"));
            table.Controls.Add(Caption("狀態："));
            table.Controls.Add(driverState);
            table.SetColumnSpan(driverState, 2);
            Wide(driverDetail);
            var driverButtons = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 3),
            };
            driverButtons.Controls.AddRange(new Control[] { installDriver, uninstallDriver });
            Wide(driverButtons);
            Wide(Hint("潛行模式用這個驅動產生虛擬螢幕。安裝與移除需要系統管理員權限（會跳出 UAC），\n" +
                      "按下後立即執行，不必按「套用」。"));

            Controls.Add(table);
        }

        public override string HelpText =>
            "【使用偵測】這些門檻決定切換條件中什麼才算「使用」。\n" +
            "・滑鼠移動門檻：「偵測到使用」時，滑鼠要移動超過這個距離才算使用。桌面震動造成誤判時調高。閒置計時不看這個門檻，任何輸入都會打斷。\n" +
            "・聲音音量門檻：聲音峰值超過這個值（0～1）才算有聲音。背景雜音被當成聲音時調高，播放中的影片或音樂沒被偵測到時調低。\n" +
            "・聲音持續秒數：聲音要連續播放這麼久才算一次使用，過濾通知音這類短暫聲音。\n" +
            "・偵測到使用時，忽略遠端連線軟體送出的鍵盤滑鼠輸入：開啟時，遠端操作不會觸發「偵測到使用」；閒置計時仍會被遠端操作打斷。" +
            "潛行模式的內建切換條件不受這個設定影響，一律只認實體鍵盤滑鼠（在 Chrome 遠端桌面裡操作不會離開潛行模式）。\n\n" +
            "【其他】\n" +
            "・自動切換時跳出通知：依切換條件自動切換時，在工作列跳出通知。\n" +
            "・開機登入時自動開啟：登入 Windows 後自動在工作列執行。\n\n" +
            "【虛擬螢幕驅動】潛行模式關閉所有螢幕時，用這個驅動產生一個虛擬螢幕，讓遠端連線軟體（例如 Chrome 遠端桌面）仍有畫面可以擷取。" +
            "驅動是本程式內附的 SudoVDA，放在程式旁邊的 drivers\\sudovda 資料夾；搬到別台電腦時，連同這個資料夾一起複製。\n" +
            "・安裝驅動：匯入驅動的憑證（受信任的根憑證、受信任的發行者），再安裝驅動。\n" +
            "・移除驅動：移除驅動與裝置，並刪除安裝時匯入的憑證。\n" +
            "・安裝與移除會變更整台電腦的驅動與憑證，所以需要系統管理員權限：按下後會跳出 UAC，按「是」才會執行。" +
            "按下就立即執行，不必按「套用」，按「取消」也不會復原。之後新增、移除虛擬螢幕不需要系統管理員權限。\n" +
            "・狀態：已安裝、未安裝、找不到驅動檔（drivers 資料夾沒有跟著 exe）、無法使用（例如需要重新開機，或電腦上有其他軟體裝的同一個驅動）。" +
            "不是「已安裝」時無法切換到潛行模式，模式分頁的潛行模式旁也會顯示警告與「安裝驅動」按鈕。";

        protected override void OnLoad(AppConfig d)
        {
            draft = d;
            mouseThreshold.Value = Clamp(d.MouseMoveThreshold, mouseThreshold);
            audioThreshold.Value = Clamp((decimal)d.AudioThreshold, audioThreshold);
            audioSeconds.Value = Clamp(d.AudioSeconds, audioSeconds);
            ignoreRemote.Checked = d.IgnoreRemoteInput;
            notifications.Checked = d.ShowNotifications;
            startWithWindows.Checked = d.StartWithWindows;
            ShowDriverStatus();   // 狀態由設定視窗在開啟、切換分頁與套用後重新讀取
        }

        // ------------------------------------------------------------
        //  虛擬螢幕驅動
        // ------------------------------------------------------------

        void ShowDriverStatus()
        {
            if (IsDisposed) return;
            driverState.Text = driver.StateText;
            driverState.ForeColor = driver.IsError ? Color.Firebrick : SystemColors.ControlText;
            driverDetail.Text = driver.DetailText;
            driverDetail.Visible = driver.DetailText.Length > 0;
            installDriver.Enabled = driver.CanInstall;
            uninstallDriver.Enabled = driver.CanUninstall;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) driver.Changed -= ShowDriverStatus;
            base.Dispose(disposing);
        }

        static Button DriverButton(string text) => new()
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(90, 0),
            Enabled = false,   // 讀到狀態後才依狀態啟用
            Margin = new Padding(3, 3, 6, 3),
        };

        public override void CommitEdits()
        {
            // 讀取 Value 會確認數字欄位中剛打的字（觸發 ValueChanged 寫回副本）
            _ = mouseThreshold.Value;
            _ = audioThreshold.Value;
            _ = audioSeconds.Value;
        }

        static decimal Clamp(decimal v, NumericUpDown n) => Math.Min(n.Maximum, Math.Max(n.Minimum, v));

        NumericUpDown Number(decimal min, decimal max, int decimals, decimal increment)
        {
            var n = new NumberBox
            {
                Minimum = min,
                Maximum = max,
                DecimalPlaces = decimals,
                Increment = increment,
                Width = 90,
                TextAlign = HorizontalAlignment.Right,
                Anchor = AnchorStyles.Left,
            };
            n.TextChanged += delegate { MarkEdited(); };   // 打字時就啟用「套用」（值在確認時才寫回副本）
            return n;
        }

        static CheckBox Check(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(3, 3, 3, 3),
        };

        static Label Caption(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
        };

        Label Section(string text) => BoldLabel(text, new Padding(0, 8, 3, 6));
    }
}
