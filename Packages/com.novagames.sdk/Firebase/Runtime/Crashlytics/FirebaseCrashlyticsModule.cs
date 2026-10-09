#nullable enable
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Diagnostics;
using UnityEngine;

namespace NovaGames.Mobile.Firebase
{
    /// <summary>Module Firebase Crashlytics: đăng ký crash reporter.</summary>
    [AddComponentMenu("NovaGames/Modules/Firebase Crashlytics")]
    public sealed class FirebaseCrashlyticsModule : NovaModule
    {
        protected override void Register() =>
            AdapterRegistry.RegisterCrashReporter(CrashReporterIds.FirebaseCrashlytics, ctx => new FirebaseCrashlyticsReporter(ctx));
    }
}
