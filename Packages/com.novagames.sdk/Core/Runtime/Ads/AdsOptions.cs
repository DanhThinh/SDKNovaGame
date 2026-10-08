#nullable enable
using System;
using System.Collections.Generic;

namespace NovaGames.Mobile.Ads
{
    /// <summary>Mediation phục vụ một format. None = tắt format đó.</summary>
    public enum AdsProvider : byte { None, Max, AdMob }

    public sealed record AdPlacementBinding(string PlacementId, string AdUnitKey);

    /// <summary>Cấu hình riêng của một mediation trong build (test device, log, settings riêng như bid floor của MAX).</summary>
    public sealed record AdsProviderOptions(AdsProvider Provider)
    {
        /// <summary>MAX: GAID/IDFA; AdMob: device ID hash từ logcat/console. Chỉ áp dụng Development.</summary>
        public IReadOnlyList<string> TestDeviceIds { get; init; } = Array.Empty<string>();
        public bool VerboseLogging { get; init; }
        /// <summary>Chuyển nguyên vẹn tới adapter qua AdsAdapterInitOptions.For(provider).</summary>
        public IAdsProviderSettings? Settings { get; init; }
    }

    /// <summary>
    /// Cấu hình Ads của build (tạo bằng AdsMediation.ToOptions hoặc AdsConfig.ToOptions): ad unit ID đã resolve theo
    /// platform. Mỗi format một provider (AdUnit.Provider); một build có thể dùng cả MAX và AdMob.
    /// </summary>
    public sealed record AdsOptions(
        IReadOnlyList<AdUnit> AdUnits,
        IReadOnlyList<AdPlacementBinding> Placements)
    {
        public static AdsOptions Disabled { get; } =
            new AdsOptions(Array.Empty<AdUnit>(), Array.Empty<AdPlacementBinding>());

        public IReadOnlyList<AdsProviderOptions> Providers { get; init; } = Array.Empty<AdsProviderOptions>();
        public TimeSpan InitTimeout { get; init; } = TimeSpan.FromSeconds(10);
        /// <summary>Sau Show, vendor phải báo displayed/display failed trong khoảng này, không thì coi là DisplayFailed.</summary>
        public TimeSpan DisplayTimeout { get; init; } = TimeSpan.FromSeconds(5);
        /// <summary>
        /// Ad đã hiển thị mà vendor không báo close trong khoảng này (tính lúc app ở foreground) thì coi như đã đóng.
        /// </summary>
        public TimeSpan MaxShowDuration { get; init; } = TimeSpan.FromMinutes(3);
        /// <summary>Load không có callback trong thời gian này -&gt; Failed + retry.</summary>
        public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(60);
        /// <summary>iOS: giữ Ads tối đa khoảng này để chờ người dùng trả lời ATT rồi init không IDFA.</summary>
        public TimeSpan AttHoldTimeout { get; init; } = TimeSpan.FromSeconds(30);
        /// <summary>TTL theo GMA: app open 4 giờ, interstitial/rewarded 1 giờ. Adapter tự quản TTL (MAX) thì bỏ qua.</summary>
        public TimeSpan AppOpenTtl { get; init; } = TimeSpan.FromHours(4);
        public TimeSpan FullScreenTtl { get; init; } = TimeSpan.FromHours(1);
        public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(64);
        /// <summary>Dùng cho IBannerAds.Show(placement) không truyền options (vị trí chọn trong Ads Config).</summary>
        public BannerOptions DefaultBanner { get; init; } = new BannerOptions(BannerPosition.Bottom);

        /// <summary>Provider có ít nhất một unit, theo thứ tự xuất hiện.</summary>
        public IReadOnlyList<AdsProvider> UsedProviders
        {
            get
            {
                var used = new List<AdsProvider>();
                foreach (var unit in AdUnits)
                {
                    if (unit.Provider != AdsProvider.None && !used.Contains(unit.Provider)) used.Add(unit.Provider);
                }
                return used;
            }
        }

        public AdsProviderOptions OptionsFor(AdsProvider provider)
        {
            foreach (var options in Providers)
            {
                if (options.Provider == provider) return options;
            }
            return new AdsProviderOptions(provider);
        }

        public static string ProviderId(AdsProvider provider) => provider switch
        {
            AdsProvider.Max => AdProviderIds.Max,
            AdsProvider.AdMob => AdProviderIds.AdMob,
            _ => string.Empty,
        };

        /// <summary>Lỗi cấu hình (placement trỏ unit không tồn tại, unit trùng key, unit không có provider, ...). Rỗng = hợp lệ.</summary>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            var units = new Dictionary<string, AdUnit>(StringComparer.Ordinal);
            foreach (var unit in AdUnits)
            {
                if (string.IsNullOrEmpty(unit.Key)) errors.Add("Ad unit with empty key");
                else if (units.ContainsKey(unit.Key)) errors.Add("Duplicate ad unit key: " + unit.Key);
                else units.Add(unit.Key, unit);
                if (string.IsNullOrEmpty(unit.AdUnitId)) errors.Add("Ad unit '" + unit.Key + "' has no ad unit id");
                if (unit.Provider == AdsProvider.None) errors.Add("Ad unit '" + unit.Key + "' has no provider");
            }

            var placements = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in Placements)
            {
                if (!placements.Add(binding.PlacementId)) errors.Add("Duplicate placement: " + binding.PlacementId);
                if (!units.ContainsKey(binding.AdUnitKey))
                    errors.Add("Placement '" + binding.PlacementId + "' references unknown ad unit '" + binding.AdUnitKey + "'");
            }
            return errors;
        }
    }
}
