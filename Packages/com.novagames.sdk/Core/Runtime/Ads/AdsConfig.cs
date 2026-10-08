#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace NovaGames.Mobile.Ads
{
    /// <summary>
    /// Asset cấu hình Ads của một mediation: ad unit ID từng format theo platform. Mỗi provider một asset riêng
    /// (MaxAdsConfig / AdMobAdsConfig). Build dùng cả hai thì kéo cả hai asset vào và chọn provider cho từng format
    /// (AdsMediationSelection + AdsMediation.ToOptions); chỉ một provider thì gọi ToOptions của asset đó.
    /// Field serialize nằm ở lớp con (Unity luôn vẽ field lớp nền trước) để mọi asset Ads có cùng thứ tự Inspector:
    /// "Ad Unit ID" (interstitial, rewarded, appOpen, banner, mrec) -&gt; "Banner" -&gt; phần riêng của provider -&gt; "Development".
    /// </summary>
    public abstract class AdsConfig : ScriptableObject
    {
        /// <summary>Provider suy ra từ loại asset.</summary>
        public abstract AdsProvider Provider { get; }

        /// <summary>true = ToOptions trả test ad unit ID của provider thay cho ID đã nhập (AdMob: Use Google Test Ids).</summary>
        public virtual bool UsesTestAdUnitIds => false;

        /// <summary>Mọi format lấy từ asset này. Mỗi placement được gắn vào ad unit của format tương ứng; format không có ID bị bỏ qua.</summary>
        public AdsOptions ToOptions(IEnumerable<AdPlacement> placements, bool isDevelopment, bool isIos) =>
            AdsMediation.ToOptions(AdsMediationSelection.All(Provider),
                Provider == AdsProvider.Max ? this : null, Provider == AdsProvider.AdMob ? this : null,
                placements, isDevelopment, isIos);

        internal string ResolvedId(AdFormat format, bool isDevelopment, bool isIos) => ResolveId(format, isDevelopment, isIos);

        internal BannerPosition DefaultBannerPosition => BannerPosition;

        internal AdsProviderOptions ProviderOptions(bool isDevelopment, bool isIos) => new AdsProviderOptions(Provider)
        {
            TestDeviceIds = isDevelopment ? NonBlank(TestDeviceIds) : new List<string>(),
            VerboseLogging = isDevelopment && VerboseLogging,
            Settings = ProviderSettings(isDevelopment, isIos),
        };

        /// <summary>Lỗi cấu hình theo platform (thiếu ID, ID sai provider, test ID trong Production, ...).</summary>
        public IReadOnlyList<string> Validate(bool isDevelopment, bool isIos)
        {
            var issues = new List<string>();
            bool any = false;
            foreach (var format in AdsConfigOptions.Formats)
            {
                var id = ConfiguredId(format, isIos);
                if (string.IsNullOrEmpty(id)) continue;
                any = true;
                ValidateId(format.ToString(), id, isDevelopment, issues);
            }
            ValidateExtra(isDevelopment, isIos, issues);
            if (!any && !HasBuiltInIds(isDevelopment)) issues.Add("No ad unit ID for " + (isIos ? "iOS" : "Android"));
            return issues;
        }

        // Field "Ad Unit ID" của lớp con theo format.
        protected abstract PlatformAdUnitId UnitId(AdFormat format);
        // Vị trí banner khi game gọi Banners.Show(placement) không truyền options.
        protected abstract BannerPosition BannerPosition { get; }
        protected abstract IReadOnlyList<string> TestDeviceIds { get; }
        protected abstract bool VerboseLogging { get; }

        // ID nhập trong Inspector cho platform.
        protected string ConfiguredId(AdFormat format, bool isIos) => UnitId(format).For(isIos);

        // ID dùng cho build; AdMob bật Use Google Test Ids thì thay bằng test ID của Google.
        protected virtual string ResolveId(AdFormat format, bool isDevelopment, bool isIos) => ConfiguredId(format, isIos);

        // Cấu hình riêng của provider (vd. bid floor của MAX), chuyển nguyên vẹn tới adapter.
        protected virtual IAdsProviderSettings? ProviderSettings(bool isDevelopment, bool isIos) => null;

        protected static bool IsGoogleAdUnitId(string id) => GoogleAdUnitIds.IsGoogleId(id);

        // true khi build có ID dù Inspector để trống (test ID của Google).
        protected virtual bool HasBuiltInIds(bool isDevelopment) => false;

        protected abstract void ValidateId(string label, string id, bool isDevelopment, List<string> issues);

        protected virtual void ValidateExtra(bool isDevelopment, bool isIos, List<string> issues) { }

        static List<string> NonBlank(IReadOnlyList<string> values)
        {
            var result = new List<string>();
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) result.Add(value.Trim());
            }
            return result;
        }
    }

    /// <summary>Ad unit ID theo platform; Inspector vẽ bằng PlatformAdUnitIdDrawer (Android/iOS hiện sẵn, không foldout).</summary>
    [Serializable]
    public sealed class PlatformAdUnitId
    {
        [SerializeField] string android = string.Empty;
        [SerializeField] string ios = string.Empty;

        public string For(bool isIos) => (isIos ? ios : android)?.Trim() ?? string.Empty;
    }

    // Phần thuần của ToOptions (không đụng ScriptableObject) để test được ngoài Unity.
    internal static class AdsConfigOptions
    {
        public static readonly AdFormat[] Formats =
            { AdFormat.Interstitial, AdFormat.Rewarded, AdFormat.AppOpen, AdFormat.Banner, AdFormat.MRec };

        public static string UnitKey(AdFormat format) => format switch
        {
            AdFormat.Interstitial => "inter",
            AdFormat.Rewarded => "rewarded",
            AdFormat.AppOpen => "app_open",
            AdFormat.Banner => "banner",
            AdFormat.MRec => "mrec",
            _ => format.ToString().ToLowerInvariant(),
        };

        /// <summary>ids: ad unit ID + provider đã chọn cho từng format; ID rỗng hoặc provider None = format không dùng.</summary>
        public static AdsOptions Build(IReadOnlyDictionary<AdFormat, (AdsProvider Provider, string Id)> ids, IEnumerable<AdPlacement> placements)
        {
            var units = new List<AdUnit>();
            foreach (var format in Formats)
            {
                if (ids.TryGetValue(format, out var entry) && entry.Provider != AdsProvider.None && !string.IsNullOrEmpty(entry.Id))
                    units.Add(new AdUnit(UnitKey(format), format, entry.Id, entry.Provider));
            }

            var bindings = new List<AdPlacementBinding>();
            foreach (var placement in placements)
            {
                if (units.Exists(u => u.Format == placement.Format)) bindings.Add(new AdPlacementBinding(placement.Id, UnitKey(placement.Format)));
            }
            return new AdsOptions(units, bindings);
        }
    }

    // Ad unit ID của Google AdMob (SDK không hỗ trợ Google Ad Manager).
    internal static class GoogleAdUnitIds
    {
        const string AdMobPublisher = "ca-app-pub-3940256099942544";

        /// <summary>https://developers.google.com/admob/android/test-ads, https://developers.google.com/admob/ios/test-ads</summary>
        public static string TestId(AdFormat format, bool isIos) => format switch
        {
            AdFormat.Interstitial => isIos ? AdMobPublisher + "/4411468910" : AdMobPublisher + "/1033173712",
            AdFormat.Rewarded => isIos ? AdMobPublisher + "/1712485313" : AdMobPublisher + "/5224354917",
            AdFormat.AppOpen => isIos ? AdMobPublisher + "/5575463023" : AdMobPublisher + "/9257395921",
            AdFormat.Banner => isIos ? AdMobPublisher + "/2435281174" : AdMobPublisher + "/9214589741",
            AdFormat.MRec => isIos ? AdMobPublisher + "/2934735716" : AdMobPublisher + "/6300978111",
            _ => string.Empty,
        };
        public static bool IsTestId(string id) => id.StartsWith(AdMobPublisher, StringComparison.Ordinal);

        /// <summary>ca-app-pub-&lt;publisher&gt;/&lt;unit&gt;.</summary>
        public static bool IsGoogleId(string id) => id.StartsWith("ca-app-pub-", StringComparison.Ordinal);
    }
}
