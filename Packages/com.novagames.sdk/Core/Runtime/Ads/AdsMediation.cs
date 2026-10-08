#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace NovaGames.Mobile.Ads
{
    /// <summary>
    /// Chọn mediation cho từng loại ads (vẽ trong Inspector của composition root). None = tắt format đó.
    /// Ví dụ: full-screen qua MAX, banner qua AdMob để có collapsible banner.
    /// </summary>
    [Serializable]
    public sealed class AdsMediationSelection
    {
        [SerializeField] AdsProvider interstitial = AdsProvider.Max;
        [SerializeField] AdsProvider rewarded = AdsProvider.Max;
        [SerializeField] AdsProvider appOpen = AdsProvider.Max;
        [SerializeField] AdsProvider banner = AdsProvider.Max;
        [SerializeField] AdsProvider mrec = AdsProvider.Max;

        public static AdsMediationSelection All(AdsProvider provider) =>
            new AdsMediationSelection().Set(AdFormat.Interstitial, provider).Set(AdFormat.Rewarded, provider)
                .Set(AdFormat.AppOpen, provider).Set(AdFormat.Banner, provider).Set(AdFormat.MRec, provider);

        public AdsProvider For(AdFormat format) => format switch
        {
            AdFormat.Interstitial => interstitial,
            AdFormat.Rewarded => rewarded,
            AdFormat.AppOpen => appOpen,
            AdFormat.Banner => banner,
            AdFormat.MRec => mrec,
            _ => AdsProvider.None,
        };

        public AdsMediationSelection Set(AdFormat format, AdsProvider provider)
        {
            switch (format)
            {
                case AdFormat.Interstitial: interstitial = provider; break;
                case AdFormat.Rewarded: rewarded = provider; break;
                case AdFormat.AppOpen: appOpen = provider; break;
                case AdFormat.Banner: banner = provider; break;
                case AdFormat.MRec: mrec = provider; break;
                default: throw new ArgumentOutOfRangeException(nameof(format), format, "Format not supported");
            }
            return this;
        }
    }

    /// <summary>Ghép MaxAdsConfig + AdMobAdsConfig theo AdsMediationSelection thành AdsOptions của build.</summary>
    public static class AdsMediation
    {
        /// <summary>Format chọn provider không có asset (hoặc asset không có ID cho format đó) bị bỏ qua; xem Validate.</summary>
        public static AdsOptions ToOptions(AdsMediationSelection selection, AdsConfig? max, AdsConfig? admob,
                                           IEnumerable<AdPlacement> placements, bool isDevelopment, bool isIos)
        {
            if (selection is null) throw new ArgumentNullException(nameof(selection));
            if (placements is null) throw new ArgumentNullException(nameof(placements));
            CheckProvider(max, AdsProvider.Max, nameof(max));
            CheckProvider(admob, AdsProvider.AdMob, nameof(admob));

            var ids = new Dictionary<AdFormat, (AdsProvider, string)>();
            foreach (var format in AdsConfigOptions.Formats)
            {
                var provider = selection.For(format);
                var config = ConfigFor(provider, max, admob);
                if (config != null) ids[format] = (provider, config.ResolvedId(format, isDevelopment, isIos));
            }

            var options = AdsConfigOptions.Build(ids, placements);
            // Cấu hình của mọi provider được chọn (kể cả khi chưa có ID nào) để adapter luôn nhận settings của nó.
            var providers = new List<AdsProviderOptions>();
            foreach (var format in AdsConfigOptions.Formats)
            {
                var provider = selection.For(format);
                var config = ConfigFor(provider, max, admob);
                if (config != null && !providers.Exists(p => p.Provider == provider))
                    providers.Add(config.ProviderOptions(isDevelopment, isIos));
            }

            // Vị trí banner mặc định lấy từ asset của provider phục vụ banner.
            var bannerConfig = ConfigFor(selection.For(AdFormat.Banner), max, admob);
            return options with
            {
                Providers = providers,
                DefaultBanner = new BannerOptions(bannerConfig?.DefaultBannerPosition ?? BannerPosition.Bottom),
            };
        }

        /// <summary>
        /// Lỗi cấu hình: format chọn provider chưa kéo asset, format chọn provider mà asset không có ID,
        /// cộng lỗi riêng của từng asset đang được dùng (ID sai provider, test ID trong Production, ...).
        /// </summary>
        public static IReadOnlyList<string> Validate(AdsMediationSelection selection, AdsConfig? max, AdsConfig? admob,
                                                     bool isDevelopment, bool isIos)
        {
            var issues = new List<string>();
            if (max != null && max.Provider != AdsProvider.Max) issues.Add("MAX slot holds a " + max.Provider + " config");
            if (admob != null && admob.Provider != AdsProvider.AdMob) issues.Add("AdMob slot holds a " + admob.Provider + " config");

            bool usesMax = false, usesAdMob = false;
            foreach (var format in AdsConfigOptions.Formats)
            {
                var provider = selection.For(format);
                if (provider == AdsProvider.None) continue;
                usesMax |= provider == AdsProvider.Max;
                usesAdMob |= provider == AdsProvider.AdMob;
                var config = ConfigFor(provider, max, admob);
                if (config is null) issues.Add(format + " uses " + provider + " but no " + provider + " Ads Config is assigned");
                else if (string.IsNullOrEmpty(config.ResolvedId(format, isDevelopment, isIos)))
                    issues.Add(format + " uses " + provider + " but its Ads Config has no " + (isIos ? "iOS" : "Android") + " ad unit ID");
            }

            if (usesMax && max != null) Prefix("MAX", max.Validate(isDevelopment, isIos), issues);
            if (usesAdMob && admob != null) Prefix("AdMob", admob.Validate(isDevelopment, isIos), issues);
            return issues;
        }

        static AdsConfig? ConfigFor(AdsProvider provider, AdsConfig? max, AdsConfig? admob) => provider switch
        {
            AdsProvider.Max => max,
            AdsProvider.AdMob => admob,
            _ => null,
        };

        static void CheckProvider(AdsConfig? config, AdsProvider expected, string name)
        {
            if (config != null && config.Provider != expected)
                throw new ArgumentException("Expected a " + expected + " Ads Config, got " + config.Provider, name);
        }

        static void Prefix(string label, IReadOnlyList<string> source, List<string> issues)
        {
            foreach (var issue in source) issues.Add(label + ": " + issue);
        }
    }
}
