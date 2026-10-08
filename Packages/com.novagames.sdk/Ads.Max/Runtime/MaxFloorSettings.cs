#nullable enable
using System;
using System.Collections.Generic;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.RemoteConfig;

namespace NovaGames.Mobile.Ads.Max
{
    /// <summary>Một bid floor cascade: unit main mà AdsManager biết + các unit floor theo thứ tự HIGH -&gt; MEDIUM.</summary>
    public sealed record MaxFloorCascadeSettings(string MainAdUnitId, IReadOnlyList<string> FloorAdUnitIds);

    /// <summary>Cấu hình riêng của MAX, đi qua AdsProviderOptions.Settings -&gt; AdsAdapterInitOptions.For(AdsProvider.Max).Settings.</summary>
    public sealed record MaxAdsSettings : IAdsProviderSettings
    {
        public IReadOnlyList<MaxFloorCascadeSettings> FloorCascades { get; init; } = Array.Empty<MaxFloorCascadeSettings>();
        /// <summary>Timeout của từng unit floor; unit main không có timeout riêng (LoadTimeout của AdsManager bao cả lượt).</summary>
        public TimeSpan FloorTierLoadTimeout { get; init; } = TimeSpan.FromSeconds(15);
        public MaxFloorKeys Keys { get; init; } = MaxFloorKeys.Default;
    }

    /// <summary>Cờ A/B bật cascade theo format. Đọc một lần lúc init adapter: disable_b2b_ad_unit_ids phải đặt trước InitializeSdk.</summary>
    public sealed class MaxFloorKeys
    {
        public static MaxFloorKeys Default { get; } = new MaxFloorKeys("ad_inter_floor_enabled", "ad_rewarded_floor_enabled");

        public MaxFloorKeys(string interstitialEnabled, string rewardedEnabled)
        {
            InterstitialEnabled = new BoolKey(interstitialEnabled, false);
            RewardedEnabled = new BoolKey(rewardedEnabled, false);
            All = new ConfigKey[] { InterstitialEnabled, RewardedEnabled };
        }

        public BoolKey InterstitialEnabled { get; }
        public BoolKey RewardedEnabled { get; }
        /// <summary>Đăng ký vào RemoteConfigService để validate giá trị remote.</summary>
        public IReadOnlyList<ConfigKey> All { get; }

        public bool IsEnabled(AdFormat format, IConfigValues values) => format switch
        {
            AdFormat.Interstitial => InterstitialEnabled.Read(values),
            AdFormat.Rewarded => RewardedEnabled.Read(values),
            _ => false,
        };
    }

    // Những gì adapter cần làm lúc init cho các cascade đang bật. Thuần, không gọi MaxSdk.
    internal sealed class MaxFloorPlan
    {
        public const string DisableBackToBackKey = "disable_b2b_ad_unit_ids";
        public const string DisableAutoRetriesKey = "disable_auto_retries";

        MaxFloorPlan(List<(AdUnit Unit, AdUnit[] Tiers)> cascades, List<string> initIds)
        {
            Cascades = cascades;
            InitAdUnitIds = initIds.ToArray();
            var tierIds = new List<string>();
            var tierUnits = new List<AdUnit>();
            foreach (var cascade in cascades)
            {
                foreach (var tier in cascade.Tiers)
                {
                    tierIds.Add(tier.AdUnitId);
                    tierUnits.Add(tier);
                }
            }
            DisableBackToBackIds = string.Join(",", tierIds);
            DisableAutoRetryUnits = tierUnits;
        }

        /// <summary>Tiers: [HIGH, MEDIUM, ..., MAIN]; tier floor là bản sao của unit main (cùng Key/Format) với AdUnitId của floor.</summary>
        public IReadOnlyList<(AdUnit Unit, AdUnit[] Tiers)> Cascades { get; }
        /// <summary>Selective init: mọi unit của AdsManager + unit floor của cascade đang bật.</summary>
        public string[] InitAdUnitIds { get; }
        /// <summary>"" = không đặt. Nối bằng dấu phẩy, không khoảng trắng.</summary>
        public string DisableBackToBackIds { get; }
        public IReadOnlyList<AdUnit> DisableAutoRetryUnits { get; }

        public static MaxFloorPlan Create(IReadOnlyList<AdUnit> units, MaxAdsSettings? settings, IConfigValues remoteConfig, ISdkLogger log)
        {
            var initIds = new List<string>();
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var unit in units)
            {
                if (known.Add(unit.AdUnitId)) initIds.Add(unit.AdUnitId);
            }

            var cascades = new List<(AdUnit, AdUnit[])>();
            if (settings is null) return new MaxFloorPlan(cascades, initIds);

            foreach (var spec in settings.FloorCascades)
            {
                var main = FindUnit(units, spec.MainAdUnitId);
                if (main is null || (main.Format != AdFormat.Interstitial && main.Format != AdFormat.Rewarded))
                {
                    log.Warning("Bid floor: main ad unit '" + spec.MainAdUnitId + "' is not an interstitial/rewarded unit of this build; skipped");
                    continue;
                }
                if (!settings.Keys.IsEnabled(main.Format, remoteConfig)) continue;

                var tiers = new List<AdUnit>();
                foreach (var id in spec.FloorAdUnitIds)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    if (!known.Add(id))
                    {
                        log.Warning("Bid floor: ad unit '" + id + "' of " + main + " is already used by another unit; skipped");
                        continue;
                    }
                    tiers.Add(main with { AdUnitId = id });
                    initIds.Add(id);
                }
                if (tiers.Count == 0) continue;
                tiers.Add(main);
                cascades.Add((main, tiers.ToArray()));
                log.Info("Bid floor cascade enabled for " + main + ": " + string.Join(" > ", tiers.ConvertAll(t => t.AdUnitId)));
            }
            return new MaxFloorPlan(cascades, initIds);
        }

        static AdUnit? FindUnit(IReadOnlyList<AdUnit> units, string adUnitId)
        {
            foreach (var unit in units)
            {
                if (unit.AdUnitId == adUnitId) return unit;
            }
            return null;
        }
    }
}
