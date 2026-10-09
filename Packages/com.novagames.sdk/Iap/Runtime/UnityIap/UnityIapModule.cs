#nullable enable
using NovaGames.Mobile.Bootstrap;
using UnityEngine;

namespace NovaGames.Mobile.Iap
{
    /// <summary>Module Unity IAP: đăng ký store adapter.</summary>
    [AddComponentMenu("NovaGames/Modules/Unity IAP")]
    public sealed class UnityIapModule : NovaModule
    {
        protected override void Register() => AdapterRegistry.RegisterStore(StoreAdapterIds.UnityIap, ctx => new UnityIapStoreAdapter(ctx));
    }
}
