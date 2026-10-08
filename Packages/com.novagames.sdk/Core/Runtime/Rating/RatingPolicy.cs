#nullable enable
using System;
using System.Globalization;

namespace NovaGames.Mobile.Rating
{
    public enum RatingEligibility : byte
    {
        Eligible,
        /// <summary>Đã đánh giá hoặc chọn "Never"/"No thanks": không hỏi lại.</summary>
        AlreadyRated,
        LevelTooLow,
        /// <summary>Vừa hỏi gần đây (chọn "Later"): chờ hết thời gian chờ.</summary>
        Cooldown,
        MaxPromptsReached,
    }

    /// <summary>Kết quả khi popup đóng.</summary>
    public enum RatingResult : byte
    {
        /// <summary>Chọn đủ sao: đã mở hộp thoại review của store (store không cho biết người chơi có gửi đánh giá hay không).</summary>
        StoreReview,
        /// <summary>Ít sao, bấm gửi góp ý.</summary>
        Feedback,
        /// <summary>Ít sao, bấm "No thanks".</summary>
        NoThanks,
        Later,
        Never,
    }

    public sealed record RatingOptions
    {
        public static RatingOptions Default { get; } = new RatingOptions();

        /// <summary>Chỉ hỏi từ level này.</summary>
        public int MinLevel { get; init; } = 3;

        /// <summary>Sau "Later", chờ bao lâu mới hỏi lại.</summary>
        public TimeSpan LaterCooldown { get; init; } = TimeSpan.FromHours(24);

        /// <summary>Số lần hỏi tối đa (0 = không giới hạn).</summary>
        public int MaxPrompts { get; init; }
    }

    /// <summary>Trạng thái đánh giá của người chơi, lưu trên máy.</summary>
    public sealed class RatingState
    {
        /// <summary>Đã đánh giá hoặc từ chối hẳn: không hỏi lại.</summary>
        public bool Done { get; set; }
        public int PromptCount { get; set; }
        public DateTime? LastPromptUtc { get; set; }

        public string Serialize() =>
            (Done ? "1" : "0") + "|" + PromptCount.ToString(CultureInfo.InvariantCulture) + "|" +
            (LastPromptUtc?.Ticks.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

        /// <summary>Đọc chuỗi đã lưu; chuỗi hỏng = trạng thái mới.</summary>
        public static RatingState Parse(string? text)
        {
            var state = new RatingState();
            if (string.IsNullOrEmpty(text)) return state;
            var parts = text!.Split('|');
            if (parts.Length != 3) return state;
            state.Done = parts[0] == "1";
            if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)) state.PromptCount = Math.Max(0, count);
            if (long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks)
                state.LastPromptUtc = new DateTime(ticks, DateTimeKind.Utc);
            return state;
        }
    }

    public static class RatingPolicy
    {
        public static RatingEligibility Check(RatingState state, RatingOptions options, int level, DateTime nowUtc)
        {
            if (state.Done) return RatingEligibility.AlreadyRated;
            if (level < options.MinLevel) return RatingEligibility.LevelTooLow;
            if (options.MaxPrompts > 0 && state.PromptCount >= options.MaxPrompts) return RatingEligibility.MaxPromptsReached;
            if (state.LastPromptUtc.HasValue && nowUtc - state.LastPromptUtc.Value < options.LaterCooldown) return RatingEligibility.Cooldown;
            return RatingEligibility.Eligible;
        }
    }
}
