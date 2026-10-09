#nullable enable
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Tracking;
using UnityEngine;

namespace NovaGames.Mobile.Attribution
{
    /// <summary>Module Adjust: đăng ký tracking sink Adjust.</summary>
    [AddComponentMenu("NovaGames/Modules/Adjust")]
    public sealed class AdjustModule : NovaModule
    {
        protected override void Register() =>
            AdapterRegistry.RegisterTrackingSink(TrackingSinkIds.Adjust, ctx => new AdjustSink(ctx));
    }
}
