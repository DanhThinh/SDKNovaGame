#nullable enable
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
        [SerializeField] PlatformAdUnitId interstitial = new PlatformAdUnitId();
        [SerializeField] PlatformAdUnitId rewarded = new PlatformAdUnitId();
        [SerializeField] PlatformAdUnitId appOpen = new PlatformAdUnitId();
        [SerializeField] PlatformAdUnitId banner = new PlatformAdUnitId();
        [SerializeField] PlatformAdUnitId mrec = new PlatformAdUnitId();

        [Header("Banner")]
        [Tooltip("Vị trí banner khi game gọi Banners.Show(placement). Game vẫn có thể truyền BannerOptions để đổi riêng từng lần.")]
        [SerializeField] BannerPosition bannerPosition = BannerPosition.Bottom;

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

        protected override void ValidateExtra(bool isDevelopment, bool isIos, List<string> issues)
        {
            if (useGoogleTestIds && !isDevelopment)
                issues.Add("Use Google Test Ids is on in a release build: ads use Google test IDs and earn no revenue");
        }

        // Production còn test ID của Google là lỗi.
        protected override void ValidateId(string label, string id, bool isDevelopment, List<string> issues)
        {
            if (!isDevelopment && GoogleAdUnitIds.IsTestId(id)) issues.Add(label + ": Google test ad unit ID in a production build");
            if (!GoogleAdUnitIds.IsGoogleId(id)) issues.Add(label + ": '" + id + "' is not an AdMob ad unit ID (ca-app-pub-...)");
        }
    }
}
