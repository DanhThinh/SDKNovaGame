#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace NovaGames.Mobile.Ads.Max
{
    /// <summary>Ad unit ID của AppLovin MAX. SDK key cấu hình ở AppLovin Integration Manager, không nằm ở đây.</summary>
    [CreateAssetMenu(menuName = "NovaGames/Ads Config (MAX)", fileName = "MaxAdsConfig", order = 0)]
    public sealed class MaxAdsConfig : AdsConfig
    {
        [Header("Ad Unit ID (để trống = không dùng format đó)")]
        [Tooltip("Unit main của Interstitial. Khi chạy bid floor test, đây là unit cuối cùng (không floor).")]
        [SerializeField] PlatformAdUnitId interstitial = new PlatformAdUnitId();
        [Tooltip("Unit main của Rewarded. Khi chạy bid floor test, đây là unit cuối cùng (không floor).")]
        [SerializeField] PlatformAdUnitId rewarded = new PlatformAdUnitId();
        [SerializeField] PlatformAdUnitId appOpen = new PlatformAdUnitId();
        [SerializeField] PlatformAdUnitId banner = new PlatformAdUnitId();
        [SerializeField] PlatformAdUnitId mrec = new PlatformAdUnitId();

        [Header("Banner")]
        [Tooltip("Vị trí banner khi game gọi Banners.Show(placement). Game vẫn có thể truyền BannerOptions để đổi riêng từng lần.")]
        [SerializeField] BannerPosition bannerPosition = BannerPosition.Bottom;

        [Header("Bid Floor Test (để trống = không test)")]
        [Tooltip("Unit floor cao, chỉ bidder. Dùng chung network placement ID với unit main.")]
        [SerializeField] PlatformAdUnitId interstitialHigh = new PlatformAdUnitId();
        [Tooltip("Unit floor = ½ HIGH, chỉ bidder.")]
        [SerializeField] PlatformAdUnitId interstitialMedium = new PlatformAdUnitId();
        [Tooltip("Unit floor cao, chỉ bidder. Dùng chung network placement ID với unit main.")]
        [SerializeField] PlatformAdUnitId rewardedHigh = new PlatformAdUnitId();
        [Tooltip("Unit floor = ½ HIGH, chỉ bidder.")]
        [SerializeField] PlatformAdUnitId rewardedMedium = new PlatformAdUnitId();
        [Tooltip("Timeout load của từng unit floor (giây). Unit main dùng LoadTimeout của AdsManager.")]
        [SerializeField, Min(1)] int floorTierLoadTimeoutSeconds = 15;

        [Header("Development")]
        [Tooltip("Advertising ID (GAID/IDFA) của thiết bị test, cho SetTestDeviceAdvertisingIdentifiers. Chỉ áp dụng Development.")]
        [SerializeField] List<string> testDeviceIds = new List<string>();
        [Tooltip("Bật log chi tiết của SDK quảng cáo. Chỉ áp dụng Development.")]
        [SerializeField] bool verboseLogging;

        public override AdsProvider Provider => AdsProvider.Max;

        protected override PlatformAdUnitId UnitId(AdFormat format) => format switch
        {
            AdFormat.Interstitial => interstitial,
            AdFormat.Rewarded => rewarded,
            AdFormat.AppOpen => appOpen,
            AdFormat.Banner => banner,
            _ => mrec,
        };

        protected override BannerPosition BannerPosition => bannerPosition;
        protected override IReadOnlyList<string> TestDeviceIds => testDeviceIds;
        protected override bool VerboseLogging => verboseLogging;

        // Cascade chỉ chạy khi Remote Config ad_<format>_floor_enabled = true lúc init; tắt thì MAX load unit main như thường.
        protected override IAdsProviderSettings? ProviderSettings(bool isDevelopment, bool isIos)
        {
            var cascades = new List<MaxFloorCascadeSettings>();
            foreach (var pair in FloorIds(isIos))
            {
                var main = ConfiguredId(pair.Key, isIos);
                var floors = Array.FindAll(pair.Value, id => !string.IsNullOrEmpty(id));
                if (!string.IsNullOrEmpty(main) && floors.Length > 0) cascades.Add(new MaxFloorCascadeSettings(main, floors));
            }
            return new MaxAdsSettings
            {
                FloorCascades = cascades,
                FloorTierLoadTimeout = TimeSpan.FromSeconds(Math.Max(1, floorTierLoadTimeoutSeconds)),
            };
        }

        Dictionary<AdFormat, string[]> FloorIds(bool isIos) => new Dictionary<AdFormat, string[]>
        {
            [AdFormat.Interstitial] = new[] { interstitialHigh.For(isIos), interstitialMedium.For(isIos) },
            [AdFormat.Rewarded] = new[] { rewardedHigh.For(isIos), rewardedMedium.For(isIos) },
        };

        protected override void ValidateId(string label, string id, bool isDevelopment, List<string> issues)
        {
            if (IsGoogleAdUnitId(id)) issues.Add(label + ": '" + id + "' is an AdMob ID but this is a MAX config");
        }

        protected override void ValidateExtra(bool isDevelopment, bool isIos, List<string> issues)
        {
            foreach (var pair in FloorIds(isIos))
            {
                var main = ConfiguredId(pair.Key, isIos);
                var seen = new HashSet<string> { main };
                for (int i = 0; i < pair.Value.Length; i++)
                {
                    var id = pair.Value[i];
                    if (string.IsNullOrEmpty(id)) continue;
                    var label = pair.Key + (i == 0 ? " high" : " medium");
                    ValidateId(label, id, isDevelopment, issues);
                    if (string.IsNullOrEmpty(main)) issues.Add(label + ": floor unit set but " + pair.Key + " (main) has no ad unit ID");
                    else if (!seen.Add(id)) issues.Add(label + ": '" + id + "' is reused; each cascade tier needs its own ad unit");
                }
            }
        }
    }
}
