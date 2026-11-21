using System;
using System.Drawing;
using System.Windows.Forms;
using AutoDisplayMode.Core;

namespace AutoDisplayMode.Settings
{
    /// <summary>
    /// 設定視窗的一個分頁。分頁直接修改設定視窗暫存的設定副本（<see cref="AppConfig.Clone"/>），
    /// 套用時由設定視窗把副本交給協調者儲存。
    /// </summary>
    abstract class SettingsPage : TabPage
    {
        bool loading;

        protected SettingsPage(string title)
        {
            Text = title;
            Padding = new Padding(12);
            UseVisualStyleBackColor = true;
        }

        /// <summary>使用者在分頁中做了修改（設定視窗據此啟用「套用」）。</summary>
        public event Action? Edited;

        /// <summary>把副本 <paramref name="draft"/> 的內容顯示在分頁上；之後的修改都寫回 <paramref name="draft"/>。</summary>
        public void Load(AppConfig draft)
        {
            loading = true;
            try
            {
                OnLoad(draft);
            }
            finally
            {
                loading = false;
            }
        }

        protected abstract void OnLoad(AppConfig draft);

        /// <summary>套用前呼叫：把還沒確認的輸入（例如數字欄位中剛打的字）寫回副本。</summary>
        public virtual void CommitEdits()
        {
        }

        /// <summary>分頁的控制項改變時呼叫；載入中的改變不算修改。</summary>
        protected void MarkEdited()
        {
            if (!loading) Edited?.Invoke();
        }

        /// <summary>灰色的說明文字。</summary>
        protected static Label Hint(string text) => new()
        {
            Text = text,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 0, 3, 10),
        };
    }
}
