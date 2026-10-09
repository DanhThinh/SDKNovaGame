#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace NovaGames.Mobile.Ads
{
    /// <summary>Ad unit ID của Google AdMob. App ID cấu hình ở Google Mobile Ads Settings, không nằm ở đây.</summary>
    [CreateAssetMenu(menuName = "NovaGames/Ads Config (AdMob)", fileName = "AdMobAdsConfig", order = 1)]
    public sealed class AdMobAdsConfig : AdsConfig
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
        [Tooltip("Unit floor cao (eCPM floor cao nhất).")]
        [SerializeField] PlatformAdUnitId interstitialHigh = new PlatformAdUnitId();
        [Tooltip("Unit floor = ½ HIGH.")]
        [SerializeField] PlatformAdUnitId interstitialMedium = new PlatformAdUnitId();
        [Tooltip("Unit floor cao (eCPM floor cao nhất).")]
        [SerializeField] PlatformAdUnitId rewardedHigh = new PlatformAdUnitId();
        [Tooltip("Unit floor = ½ HIGH.")]
        [SerializeField] PlatformAdUnitId rewardedMedium = new PlatformAdUnitId();
        [Tooltip("Timeout load của từng unit floor (giây). Unit main dùng LoadTimeout của AdsManager.")]
        [SerializeField, Min(1)] int floorTierLoadTimeoutSeconds = 15;

        [Header("Test ID")]
        [Tooltip("Dùng test ad unit ID chính thức của AdMob (ca-app-pub-3940256099942544/...) thay cho các ID ở trên, "
                 + "ở MỌI bản build (không cần Development Build). Bỏ tick trước khi phát hành: test ID không có doanh thu.")]
        [FormerlySerializedAs("useGoogleTestIdsInDevelopment")]
        [SerializeField] bool useGoogleTestIds;

        [Header("Development")]
        [Tooltip("Device ID của thiết bị test (AdMob RequestConfiguration.TestDeviceIds, lấy từ logcat/console). Chỉ áp dụng Development.")]
        [SerializeField] List<string> testDeviceIds = new List<string>();
        [Tooltip("Bật log chi tiết của SDK quảng cáo. Chỉ áp dụng Development.")]
        [SerializeField] bool verboseLogging;

        public override AdsProvider Provider => AdsProvider.AdMob;

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

        public override bool UsesTestAdUnitIds => useGoogleTestIds;

        protected override string ResolveId(AdFormat format, bool isDevelopment, bool isIos) =>
            useGoogleTestIds ? GoogleAdUnitIds.TestId(format, isIos) : base.ResolveId(format, isDevelopment, isIos);

        protected override bool HasBuiltInIds(bool isDevelopment) => useGoogleTestIds;

        // Cascade chỉ chạy khi Remote Config ad_<format>_floor_enabled = true lúc init; tắt thì AdMob load unit main như thường.
        // Đang dùng test ID của Google thì không cascade (unit floor là ID thật).
        protected override IAdsProviderSettings? ProviderSettings(bool isDevelopment, bool isIos)
        {
            var cascades = new List<AdMobFloorCascadeSettings>();
            if (!useGoogleTestIds)
            {
                foreach (var pair in FloorIds(isIos))
                {
                    var main = ConfiguredId(pair.Key, isIos);
                    var floors = Array.FindAll(pair.Value, id => !string.IsNullOrEmpty(id));
                    if (!string.IsNullOrEmpty(main) && floors.Length > 0) cascades.Add(new AdMobFloorCascadeSettings(main, floors));
                }
            }
            return new AdMobAdsSettings
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

        protected override void ValidateExtra(bool isDevelopment, bool isIos, List<string> issues)
        {
            if (useGoogleTestIds && !isDevelopment)
                issues.Add("Use Google Test Ids is on in a release build: ads use Google test IDs and earn no revenue");

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

        // Production còn test ID của Google là lỗi.
        protected override void ValidateId(string label, string id, bool isDevelopment, List<string> issues)
        {
            if (!isDevelopment && GoogleAdUnitIds.IsTestId(id)) issues.Add(label + ": Google test ad unit ID in a production build");
            if (!GoogleAdUnitIds.IsGoogleId(id)) issues.Add(label + ": '" + id + "' is not an AdMob ad unit ID (ca-app-pub-...)");
        }
    }
}
