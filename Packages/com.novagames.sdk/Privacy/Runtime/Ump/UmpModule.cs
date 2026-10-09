#nullable enable
using NovaGames.Mobile.Bootstrap;
using UnityEngine;

namespace NovaGames.Mobile.Privacy.Ump
{
    /// <summary>Module Google UMP: đăng ký nền tảng consent.</summary>
    [AddComponentMenu("NovaGames/Modules/Google UMP")]
    public sealed class UmpModule : NovaModule
    {
        protected override void Register() =>
            AdapterRegistry.RegisterConsentPlatform(ConsentPlatformIds.GoogleUmp, ctx => new UmpConsentPlatform(ctx));
    }
}
