#nullable enable
using System;

namespace NovaGames.Mobile.Ads
{
    /// <summary>Phần thưởng do vendor báo (label/amount cấu hình trên dashboard mediation).</summary>
    public sealed record AdReward(string Label, double Amount);

    /// <summary>Kết quả show interstitial. <c>ShowOperationId</c> nối với revenue của lần show đó.</summary>
    public sealed record InterstitialResult(ShowOutcome Outcome, Guid ShowOperationId, SdkError? Error = null);

    /// <summary>Kết quả show rewarded. Chỉ cấp thưởng khi <see cref="IsRewarded"/>.</summary>
    public sealed record RewardedResult(ShowOutcome Outcome, Guid ShowOperationId, AdReward? Reward = null, SdkError? Error = null)
    {
        public bool IsRewarded => Outcome == ShowOutcome.Rewarded;
    }

    /// <summary>Kết quả show app open.</summary>
    public sealed record AppOpenResult(ShowOutcome Outcome, Guid ShowOperationId, SdkError? Error = null);

    /// <summary>Tùy chọn show rewarded. <c>WaitForLoad</c> = Zero: chưa Ready thì trả NotReady ngay.</summary>
    public sealed record RewardedShowOptions(TimeSpan WaitForLoad)
    {
        public static RewardedShowOptions Immediate { get; } = new RewardedShowOptions(TimeSpan.Zero);
    }

    /// <summary>App open được show vì game mở lần đầu hay quay lại từ background.</summary>
    public enum AppOpenTrigger : byte { ColdStart, Resume }

    /// <summary>Availability của một placement vừa đổi (vd. để bật/tắt nút rewarded).</summary>
    public sealed record AdAvailabilityChanged(FullScreenPlacement Placement, AdAvailability Availability);

    public enum FullScreenPresentationPhase : byte { Showing, Closed }

    /// <summary>Full-screen ad bắt đầu hiện / đã đóng.</summary>
    public sealed record FullScreenAdPresentation(FullScreenPresentationPhase Phase, AdFormat Format, string PlacementId, Guid ShowOperationId);

    /// <summary>Một impression có doanh thu (Value theo Currency, thường USD). Mediation = "max" / "admob".</summary>
    public sealed record AdImpression(
        Guid ShowOperationId, AdFormat Format, string PlacementId, string Mediation, string Network,
        string AdUnitId, double Value, string Currency);
}
