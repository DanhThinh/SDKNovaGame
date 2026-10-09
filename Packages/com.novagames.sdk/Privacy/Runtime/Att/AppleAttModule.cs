#nullable enable
using NovaGames.Mobile.Bootstrap;
using UnityEngine;

namespace NovaGames.Mobile.Privacy.Att
{
    /// <summary>Module ATT của iOS. Chỉ đăng ký trên máy iOS thật; Editor không có ATT.</summary>
    [AddComponentMenu("NovaGames/Modules/Apple ATT")]
    public sealed class AppleAttModule : NovaModule
    {
        protected override void Register()
        {
#if UNITY_IOS && !UNITY_EDITOR
            AdapterRegistry.RegisterAttPlatform(AttPlatformIds.Apple, ctx => new AppleAttPlatform(ctx));
#endif
        }
    }
}
