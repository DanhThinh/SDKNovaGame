#nullable enable
using NovaGames.Mobile.Bootstrap;
using UnityEngine;

namespace NovaGames.Mobile.Ads.Max
{
    /// <summary>Module AppLovin MAX: đăng ký adapter quảng cáo id max.</summary>
    [AddComponentMenu("NovaGames/Modules/AppLovin MAX")]
    public sealed class MaxModule : NovaModule
    {
        protected override void Register() => AdapterRegistry.RegisterAds(AdProviderIds.Max, ctx => new MaxAdsAdapter(ctx));
    }
}
