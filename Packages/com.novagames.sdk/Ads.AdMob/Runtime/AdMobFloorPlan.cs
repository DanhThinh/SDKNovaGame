#nullable enable
using System;
using System.Collections.Generic;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.RemoteConfig;

namespace NovaGames.Mobile.Ads.AdMob
{
    // Các cascade đang bật lúc init adapter. Thuần, không gọi GMA. Cờ A/B dùng chung tên với MAX
    // (ad_inter_floor_enabled, ad_rewarded_floor_enabled): format nào chạy mediation nào thì cờ áp cho mediation đó.
    internal sealed class AdMobFloorPlan
    {
        internal static readonly BoolKey InterstitialEnabled = new BoolKey("ad_inter_floor_enabled", false);
        internal static readonly BoolKey RewardedEnabled = new BoolKey("ad_rewarded_floor_enabled", false);

        AdMobFloorPlan(List<(AdUnit Unit, AdUnit[] Tiers)> cascades) => Cascades = cascades;

        /// <summary>Tiers: [HIGH, MEDIUM, ..., MAIN]; tier floor là bản sao của unit main (cùng Key/Format) với AdUnitId của floor.</summary>
        public IReadOnlyList<(AdUnit Unit, AdUnit[] Tiers)> Cascades { get; }

        static bool IsEnabled(AdFormat format, IConfigValues values) => format switch
        {
            AdFormat.Interstitial => InterstitialEnabled.Read(values),
            AdFormat.Rewarded => RewardedEnabled.Read(values),
            _ => false,
        };

        public static AdMobFloorPlan Create(IReadOnlyList<AdUnit> units, AdMobAdsSettings? settings, IConfigValues remoteConfig, ISdkLogger log)
        {
            var cascades = new List<(AdUnit, AdUnit[])>();
            if (settings is null) return new AdMobFloorPlan(cascades);

            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var unit in units) known.Add(unit.AdUnitId);

            foreach (var spec in settings.FloorCascades)
            {
                var main = FindUnit(units, spec.MainAdUnitId);
                if (main is null || (main.Format != AdFormat.Interstitial && main.Format != AdFormat.Rewarded))
                {
                    log.Warning("Bid floor: main ad unit '" + spec.MainAdUnitId + "' is not an interstitial/rewarded unit of this build; skipped");
                    continue;
                }
                if (!IsEnabled(main.Format, remoteConfig)) continue;

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
                }
                if (tiers.Count == 0) continue;
                tiers.Add(main);
                cascades.Add((main, tiers.ToArray()));
                log.Info("Bid floor cascade enabled for " + main + ": " + string.Join(" > ", tiers.ConvertAll(t => t.AdUnitId)));
            }
            return new AdMobFloorPlan(cascades);
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
