#nullable enable
using System;

namespace NovaGames.Mobile.Ads
{
    /// <summary>
    /// Giá trị tường minh: được dùng trong capping/log, không đổi số của format đã có.
    /// Đã hỗ trợ: Interstitial, Rewarded, AppOpen, Banner, MRec. RewardedInterstitial và Native chưa có API/adapter.
    /// </summary>
    public enum AdFormat : byte
    {
        Interstitial = 0,
        Rewarded = 1,
        RewardedInterstitial = 2,
        AppOpen = 3,
        Banner = 4,
        MRec = 5,
        Native = 6,
    }

    [Flags]
    public enum AdCapability
    {
        None = 0,
        Banner = 1 << 0,
        CollapsibleBanner = 1 << 1,
        MRec = 1 << 2,
        Interstitial = 1 << 3,
        Rewarded = 1 << 4,
        AppOpen = 1 << 5,
    }

    public static class AdFormatExtensions
    {
        public static bool IsFullScreen(this AdFormat format) =>
            format == AdFormat.Interstitial || format == AdFormat.Rewarded || format == AdFormat.AppOpen;

        public static AdCapability ToCapability(this AdFormat format) => format switch
        {
            AdFormat.Interstitial => AdCapability.Interstitial,
            AdFormat.Rewarded => AdCapability.Rewarded,
            AdFormat.AppOpen => AdCapability.AppOpen,
            AdFormat.Banner => AdCapability.Banner,
            AdFormat.MRec => AdCapability.MRec,
            _ => AdCapability.None,
        };
    }
}
