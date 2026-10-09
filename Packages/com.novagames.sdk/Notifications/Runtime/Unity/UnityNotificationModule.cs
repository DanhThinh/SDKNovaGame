#nullable enable
using NovaGames.Mobile.Bootstrap;
using UnityEngine;

namespace NovaGames.Mobile.Notifications
{
    /// <summary>Module Unity Mobile Notifications: đăng ký nền tảng thông báo local.</summary>
    [AddComponentMenu("NovaGames/Modules/Mobile Notifications")]
    public sealed class UnityNotificationModule : NovaModule
    {
        protected override void Register() =>
            AdapterRegistry.RegisterNotifications(NotificationPlatformIds.Unity, ctx => new UnityNotificationPlatform(ctx));
    }
}
