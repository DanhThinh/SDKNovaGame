#nullable enable
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.RemoteConfig;
using UnityEngine;

namespace NovaGames.Mobile.Firebase
{
    /// <summary>Module Firebase Remote Config: đăng ký nguồn Remote Config.</summary>
    [AddComponentMenu("NovaGames/Modules/Firebase Remote Config")]
    public sealed class FirebaseRemoteConfigModule : NovaModule
    {
        protected override void Register() =>
            AdapterRegistry.RegisterRemoteConfig(RemoteConfigSourceIds.Firebase, ctx => new FirebaseRemoteConfigSource(ctx));
    }
}
