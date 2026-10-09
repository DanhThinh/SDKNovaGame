#nullable enable
using System;
using System.Collections.Generic;

namespace NovaGames.Mobile.Ads
{
    /// <summary>Một bid floor cascade của AdMob: unit main mà AdsManager biết + các unit floor theo thứ tự HIGH -&gt; MEDIUM.</summary>
    public sealed record AdMobFloorCascadeSettings(string MainAdUnitId, IReadOnlyList<string> FloorAdUnitIds);

    /// <summary>
    /// Cấu hình riêng của AdMob, đi qua AdsProviderOptions.Settings -&gt; AdsAdapterInitOptions.For(AdsProvider.AdMob).Settings.
    /// Logic cascade nằm trong adapter AdMob; Core chỉ chuyển nguyên vẹn.
    /// </summary>
    public sealed record AdMobAdsSettings : IAdsProviderSettings
    {
        public IReadOnlyList<AdMobFloorCascadeSettings> FloorCascades { get; init; } = Array.Empty<AdMobFloorCascadeSettings>();
        /// <summary>Timeout của từng unit floor; unit main không có timeout riêng (LoadTimeout của AdsManager bao cả lượt).</summary>
        public TimeSpan FloorTierLoadTimeout { get; init; } = TimeSpan.FromSeconds(15);
    }
}
