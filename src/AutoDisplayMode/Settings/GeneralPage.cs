using System;
using System.Drawing;
using System.Windows.Forms;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Settings
{
    /// <summary>「一般設定」分頁：滑鼠移動門檻、聲音音量門檻、聲音持續秒數、忽略遠端輸入、自動切換通知、開機登入時自動開啟。</summary>
    sealed class GeneralPage : SettingsPage
    {
        readonly NumericUpDown mouseThreshold;
        readonly NumericUpDown audioThreshold;
        readonly NumericUpDown audioSeconds;
        readonly CheckBox ignoreRemote;
        readonly CheckBox notifications;
        readonly CheckBox startWithWindows;
        AppConfig draft = new();

        public GeneralPage() : base("一般設定")
        {
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
            Wide(Hint("開啟時，遠端操作不會觸發「偵測到使用」；閒置計時仍會被遠端操作打斷。"));

            Wide(Section("其他"));
            Wide(notifications);
            Wide(startWithWindows);

            Controls.Add(table);
        }

        protected override void OnLoad(AppConfig d)
        {
            draft = d;
            mouseThreshold.Value = Clamp(d.MouseMoveThreshold, mouseThreshold);
            audioThreshold.Value = Clamp((decimal)d.AudioThreshold, audioThreshold);
            audioSeconds.Value = Clamp(d.AudioSeconds, audioSeconds);
            ignoreRemote.Checked = d.IgnoreRemoteInput;
            notifications.Checked = d.ShowNotifications;
            startWithWindows.Checked = d.StartWithWindows;
        }

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
