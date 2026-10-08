#nullable enable
using System;

namespace NovaGames.Mobile.Ads
{
    /// <summary>
    /// Placement là điểm hiển thị ad trong game (vd. "level_end"); AdsOptions.Placements map placement -&gt; ad unit.
    /// Placement chưa map dùng ad unit đầu tiên cùng format.
    /// </summary>
    public abstract class AdPlacement
    {
        protected AdPlacement(string id, AdFormat format)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("Placement id is required.", nameof(id));
            Id = id;
            Format = format;
        }

        public string Id { get; }
        public AdFormat Format { get; }

        public override string ToString() => Format + ":" + Id;
    }

    public abstract class FullScreenPlacement : AdPlacement
    {
        protected FullScreenPlacement(string id, AdFormat format) : base(id, format) { }
    }

    public sealed class InterstitialPlacement : FullScreenPlacement
    {
        public InterstitialPlacement(string id) : base(id, AdFormat.Interstitial) { }
    }

    public sealed class RewardedPlacement : FullScreenPlacement
    {
        public RewardedPlacement(string id) : base(id, AdFormat.Rewarded) { }
    }

    public sealed class AppOpenPlacement : FullScreenPlacement
    {
        public AppOpenPlacement(string id) : base(id, AdFormat.AppOpen) { }
    }

    public sealed class BannerPlacement : AdPlacement
    {
        public BannerPlacement(string id) : base(id, AdFormat.Banner) { }
    }

    public sealed class MrecPlacement : AdPlacement
    {
        public MrecPlacement(string id) : base(id, AdFormat.MRec) { }
    }
}
