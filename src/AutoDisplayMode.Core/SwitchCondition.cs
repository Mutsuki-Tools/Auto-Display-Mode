using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AutoDisplayMode.Core
{
    /// <summary>切換條件的條件類型。</summary>
    public enum SwitchConditionKind
    {
        /// <summary>閒置：所選的使用來源全部都沒有使用，持續指定的時間。</summary>
        Idle,

        /// <summary>偵測到使用：所選的使用來源任一個出現使用，就立即成立。</summary>
        UsageDetected,
    }

    /// <summary>閒置時間的單位。</summary>
    public enum IdleUnit
    {
        Minutes,
        Hours,
    }

    /// <summary>使用來源（可複選）。</summary>
    [Flags]
    public enum UsageSources
    {
        None = 0,

        /// <summary>滑鼠或鍵盤。</summary>
        MouseKeyboard = 1,

        /// <summary>聲音（需持續達聲音持續秒數且超過音量門檻才算一次使用）。</summary>
        Audio = 2,
    }

    /// <summary>切換條件：處於來源模式時，條件成立就切換到目標模式。</summary>
    public sealed record SwitchCondition
    {
        /// <summary>來源模式的 ID。</summary>
        public int FromModeId { get; init; }

        public SwitchConditionKind Kind { get; init; }

        /// <summary>閒置時間的數值（單位為 <see cref="IdleUnit"/>）；只用於閒置條件。</summary>
        public int IdleAmount { get; init; } = 10;

        public IdleUnit IdleUnit { get; init; }

        public UsageSources Sources { get; init; } = UsageSources.MouseKeyboard;

        /// <summary>目標模式的 ID。</summary>
        public int ToModeId { get; init; }

        /// <summary>閒置時間（毫秒）。</summary>
        public long IdleMs => IdleAmount * (IdleUnit == IdleUnit.Hours ? 3_600_000L : 60_000L);

        public bool Uses(UsageSources source) => (Sources & source) != 0;

        /// <summary>
        /// 把使用者輸入的閒置時間文字轉成 <see cref="IdleAmount"/>：前後的空白不影響，全形數字（例如「１５」）視同半形；
        /// 不是整數（例如「1.5」「abc」、空白、超出範圍）時回傳 0，讓 <see cref="Validate"/> 回報「閒置時間必須是正整數」。
        /// </summary>
        public static int ParseIdleAmount(string text) =>
            int.TryParse(text.Normalize(NormalizationForm.FormKC).Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) ? n : 0;

        /// <summary>依 <paramref name="config"/> 中的模式檢查這條切換條件；有效時回傳 null，否則回傳無效的原因。</summary>
        public string? Validate(AppConfig config)
        {
            if (config.FindMode(FromModeId) == null) return "來源模式不存在";
            if (config.FindMode(ToModeId) == null) return "目標模式不存在";
            if (FromModeId == ToModeId) return "來源模式與目標模式相同";
            if ((Sources & (UsageSources.MouseKeyboard | UsageSources.Audio)) == 0) return "未選任何使用來源";
            if (Kind == SwitchConditionKind.Idle && IdleAmount < 1) return "閒置時間必須是正整數";
            return null;
        }

        /// <summary>給人看的描述，例如「遊玩」閒置 5 分鐘（滑鼠或鍵盤、聲音）→「省電」。</summary>
        public string Describe(AppConfig config) =>
            "「" + ModeName(config, FromModeId) + "」" + DescribeCondition() + "（" + DescribeSources() + "）→「" + ModeName(config, ToModeId) + "」";

        /// <summary>條件部分的描述，例如「閒置 5 分鐘」「偵測到使用」。</summary>
        public string DescribeCondition() =>
            Kind == SwitchConditionKind.Idle
                ? "閒置 " + IdleAmount + (IdleUnit == IdleUnit.Hours ? " 小時" : " 分鐘")
                : "偵測到使用";

        string DescribeSources()
        {
            var parts = new List<string>();
            if (Uses(UsageSources.MouseKeyboard)) parts.Add("滑鼠或鍵盤");
            if (Uses(UsageSources.Audio)) parts.Add("聲音");
            return parts.Count == 0 ? "未選使用來源" : string.Join("、", parts);
        }

        static string ModeName(AppConfig config, int id) => config.FindMode(id)?.Name ?? "模式 " + id;
    }
}
