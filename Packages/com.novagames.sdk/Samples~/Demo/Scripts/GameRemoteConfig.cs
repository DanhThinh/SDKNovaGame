#nullable enable
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.RemoteConfig;
using UnityEngine;

namespace NovaGames.Mobile.Samples
{
    // Tạo asset: Create > NovaGames > Remote Config Definitions, rồi kéo vào ô Remote Config của NovaSdkSettings.
    // IAdsConfigKeysSource: NovaSdk lấy key Ads từ đây (xem RemoteKeys.Ads trong RemoteKey.cs).
    [CreateAssetMenu(menuName = "NovaGames/Remote Config Definitions", fileName = "GameRemoteConfig")]
    public sealed class GameRemoteConfig : RemoteConfigDefinitions<RemoteKey>, IAdsConfigKeysSource
    {
        public AdsConfigKeys AdsKeys => RemoteKeys.Ads(this);
    }
}
