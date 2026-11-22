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

        /// <summary>鍵盤滑鼠。</summary>
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

        /// <summary>目標模式的模式編號；<see cref="PreviousTarget"/> 表示「回到上個配置」。</summary>
        public int ToModeId { get; init; }

        /// <summary>
        /// <see cref="ToModeId"/> 的特殊值：目標模式「回到上個配置」，切換時才決定實際的目標（<see cref="AppConfig.PreviousNormalMode"/>）。
        /// 不指向模式編號，所以重新編號與刪除模式都不影響它。cfg 寫成 <c>To=Previous</c>。
        /// </summary>
        public const int PreviousTarget = -1;

        /// <summary>目標模式是「回到上個配置」。</summary>
        public bool ToPrevious => ToModeId == PreviousTarget;

        /// <summary>介面上「回到上個配置」的文字（GLOSSARY 的「上一個一般模式」）。</summary>
        public const string PreviousTargetText = "回到上個配置";

        /// <summary>強制：即使自動切換關閉、設定視窗開著，這條切換條件照樣生效。</summary>
        public bool Forced { get; init; }

        /// <summary>
        /// 內建切換條件：潛行模式自帶、不能刪除。條件固定為「偵測到使用（鍵盤滑鼠，只認實體輸入）」且強制，只有目標模式可以改（只能是一般模式）。
        /// </summary>
        public bool BuiltIn { get; init; }

        /// <summary>潛行模式 <paramref name="remoteModeId"/> 的內建切換條件，目標模式為 <paramref name="targetModeId"/>（可以是 <see cref="PreviousTarget"/>）。</summary>
        public static SwitchCondition BuiltInFor(int remoteModeId, int targetModeId) => new()
        {
            FromModeId = remoteModeId,
            Kind = SwitchConditionKind.UsageDetected,
            Sources = UsageSources.MouseKeyboard,
            ToModeId = targetModeId,
            Forced = true,
            BuiltIn = true,
        };

        /// <summary>內建切換條件鎖定的欄位是否都是固定值（條件、使用來源、強制）。</summary>
        internal bool HasBuiltInFields =>
            Kind == SwitchConditionKind.UsageDetected && Sources == UsageSources.MouseKeyboard && Forced &&
            IdleAmount == new SwitchCondition().IdleAmount && IdleUnit == default;

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
            ModeConfig? from = config.FindMode(FromModeId), to = config.FindMode(ToModeId);
            if (from == null) return "來源模式不存在";
            if (to == null && !ToPrevious) return "目標模式不存在";
            if (FromModeId == ToModeId) return "來源模式與目標模式相同";
            if (BuiltIn)
            {
                if (!from.IsRemote) return "內建切換條件的來源模式必須是潛行模式";
                if (to is { IsRemote: true }) return "內建切換條件的目標模式必須是一般模式";
                if (!HasBuiltInFields) return "內建切換條件的條件固定為偵測到使用（鍵盤滑鼠）且強制";
            }
            if ((Sources & (UsageSources.MouseKeyboard | UsageSources.Audio)) == 0) return "未選任何使用來源";
            if (Kind == SwitchConditionKind.Idle && IdleAmount < 1) return "閒置時間必須是正整數";
            return null;
        }

        /// <summary>
        /// 給人看的描述，例如「遊玩」閒置 5 分鐘（鍵盤滑鼠、聲音）→「省電」、「潛行」偵測到使用（鍵盤滑鼠）→ 回到上個配置；
        /// 有勾強制時結尾加上「（強制）」，內建切換條件加上「（內建）」。
        /// </summary>
        public string Describe(AppConfig config) =>
            "「" + ModeName(config, FromModeId) + "」" + DescribeCondition() + "（" + DescribeSources() + "）→" +
            (ToPrevious ? " " + PreviousTargetText : "「" + ModeName(config, ToModeId) + "」") +
            (BuiltIn ? "（內建）" : Forced ? "（強制）" : "");

        /// <summary>條件部分的描述，例如「閒置 5 分鐘」「偵測到使用」。</summary>
        public string DescribeCondition() =>
            Kind == SwitchConditionKind.Idle
                ? "閒置 " + IdleAmount + (IdleUnit == IdleUnit.Hours ? " 小時" : " 分鐘")
                : "偵測到使用";

        string DescribeSources()
        {
            var parts = new List<string>();
            if (Uses(UsageSources.MouseKeyboard)) parts.Add("鍵盤滑鼠");
            if (Uses(UsageSources.Audio)) parts.Add("聲音");
            return parts.Count == 0 ? "未選使用來源" : string.Join("、", parts);
        }

        static string ModeName(AppConfig config, int id) => config.FindMode(id)?.Name ?? "模式 " + id;
    }
}
