#nullable enable
using NovaGames.Mobile.Bootstrap;
using UnityEngine;

namespace NovaGames.Mobile.Ads.AdMob
{
    /// <summary>Module AdMob (Google Mobile Ads): đăng ký adapter quảng cáo id admob.</summary>
    [AddComponentMenu("NovaGames/Modules/AdMob")]
    public sealed class AdMobModule : NovaModule
    {
        protected override void Register() => AdapterRegistry.RegisterAds(AdProviderIds.AdMob, ctx => new AdMobAdsAdapter(ctx));
    }
}
