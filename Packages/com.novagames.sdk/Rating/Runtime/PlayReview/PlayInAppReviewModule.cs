#nullable enable
using NovaGames.Mobile.Bootstrap;
using UnityEngine;

namespace NovaGames.Mobile.Rating
{
    /// <summary>Module Google Play In-App Review cho NovaRating.</summary>
    [AddComponentMenu("NovaGames/Modules/Play In-App Review")]
    public sealed class PlayInAppReviewModule : NovaModule
    {
        protected override void Register() => NovaRating.SetInAppReviewProvider(new PlayInAppReviewProvider());
    }
}
