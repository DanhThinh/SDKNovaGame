#nullable enable
using NovaGames.Mobile.NoInternet;
using UnityEngine;

namespace NovaGames.Mobile.Samples
{
    // Khởi động SDK cho scene Demo. Game thật làm y hệt trong scene boot của mình:
    // kéo asset NovaSdkSettings vào, gọi NovaSdk.InitializeAsync một lần, sau đó dùng NovaAds / NovaRemoteConfig /
    // NovaAnalytics / NovaAttribution / NovaPrivacy / NovaIap / NovaNotifications ở bất kỳ đâu. Nút demo: SdkDemoPanel.
    public sealed class GameService : MonoBehaviour
    {
        [Tooltip("Create > NovaGames > SDK Settings")]
        [SerializeField] NovaSdkSettings? settings;

        [Header("No Internet")]
        [Tooltip("Hiện popup che màn hình và dừng game khi mất mạng. Tắt nếu game chơi offline được.")]
        [SerializeField] bool noInternetPopup = true;
        [Tooltip("Kéo prefab Packages/NovaGames Mobile SDK/NoInternet/Prefabs/NoInternetPopup vào đây (hoặc prefab đã chỉnh giao diện).")]
        [SerializeField] NoInternetPopup? noInternetPopupPrefab;

        void Awake()
        {
            if (settings == null)
            {
                Debug.LogError("[GameService] NovaSdkSettings is not assigned");
                return;
            }

            // Consent do SDK lo theo "Consent Source" trong settings (Google UMP cho bản phát hành). Asset sample để
            // "Assume granted (testing only)" để test ads không cần form.
            _ = NovaSdk.InitializeAsync(settings);

            SetUpNoInternet();
        }

        void SetUpNoInternet()
        {
            NovaNoInternet.Enabled = noInternetPopup;
            if (noInternetPopupPrefab != null && NoInternetPopup.Instance == null) Instantiate(noInternetPopupPrefab);
            else if (noInternetPopupPrefab == null && noInternetPopup)
                Debug.LogWarning("[GameService] No Internet Popup is on but the prefab is not assigned");

            // Remote Config: tắt popup No Internet từ xa (no_internet_popup_on), level hỏi đánh giá (level_show_rate).
            // Áp lại mỗi lần config cập nhật.
            NovaRemoteConfig.Updated += ApplyRemoteConfig;
            ApplyRemoteConfig();
        }

        void ApplyRemoteConfig()
        {
            NovaNoInternet.Enabled = noInternetPopup && NovaRemoteConfig.GetBool(RemoteKey.no_internet_popup_on);
            // Popup đánh giá chỉ hiện từ level này (popup RatingPopup đặt trong scene cần hỏi).
            NovaRating.MinLevel = NovaRemoteConfig.GetInt(RemoteKey.level_show_rate);
        }

        void OnDestroy() => NovaRemoteConfig.Updated -= ApplyRemoteConfig;
    }
}
