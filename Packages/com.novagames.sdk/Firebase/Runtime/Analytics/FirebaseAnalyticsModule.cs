#nullable enable
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Tracking;
using UnityEngine;

namespace NovaGames.Mobile.Firebase
{
    /// <summary>Module Firebase Analytics: đăng ký tracking sink Firebase.</summary>
    [AddComponentMenu("NovaGames/Modules/Firebase Analytics")]
    public sealed class FirebaseAnalyticsModule : NovaModule
    {
        protected override void Register() =>
            AdapterRegistry.RegisterTrackingSink(TrackingSinkIds.Firebase, ctx => new FirebaseAnalyticsSink(ctx));
    }
}
