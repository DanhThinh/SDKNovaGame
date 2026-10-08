#nullable enable
using NovaGames.Mobile.NoInternet;
using NovaGames.Mobile.Rating;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM && NOVA_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace NovaGames.Mobile
{
    /// <summary>
    /// Khởi động SDK bằng prefab: kéo <c>Bootstrap/Prefabs/NovaSdk</c> vào scene đầu tiên, gán asset NovaSdkSettings là
    /// xong. Component gọi <see cref="NovaSdk.InitializeAsync"/> một lần, tạo popup mất mạng + popup đánh giá, tạo
    /// EventSystem nếu scene chưa có, và áp Remote Config vào hai popup. Sống qua mọi scene; đặt thêm ở scene khác cũng
    /// chỉ giữ một bản.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    [AddComponentMenu("NovaGames/Nova SDK")]
    public sealed class NovaSdkBootstrap : MonoBehaviour
    {
        [Tooltip("Create > NovaGames > SDK Settings. Bắt buộc.")]
        [SerializeField] NovaSdkSettings? settings;

        [Header("No Internet")]
        [Tooltip("Hiện popup che màn hình và dừng game khi mất mạng. Tắt nếu game chơi offline được.")]
        [SerializeField] bool noInternetPopup = true;
        [Tooltip("Prefab popup mất mạng (mặc định NoInternet/Prefabs/NoInternetPopup; đổi giao diện bằng Prefab Variant).")]
        [SerializeField] NoInternetPopup? noInternetPopupPrefab;
        [Tooltip("Key Remote Config kiểu Bool để tắt popup từ xa. Bỏ qua nếu asset Remote Config không có key này.")]
        [SerializeField] string noInternetRemoteKey = "no_internet_popup_on";

        [Header("Rating")]
        [Tooltip("Prefab popup đánh giá (mặc định Rating/Prefabs/RatingPopup). Để trống nếu tự đặt popup trong scene.")]
        [SerializeField] RatingPopup? ratingPopupPrefab;
        [Tooltip("Key Remote Config kiểu Int: level bắt đầu hỏi đánh giá. Bỏ qua nếu asset Remote Config không có key này.")]
        [SerializeField] string ratingMinLevelRemoteKey = "level_show_rate";

        [Header("UI")]
        [Tooltip("Tạo EventSystem khi scene chưa có, để bấm được nút trên popup.")]
        [SerializeField] bool createEventSystem = true;

        static NovaSdkBootstrap? s_instance;
        EventSystem? _eventSystem;

        /// <summary>Bản đang chạy (null nếu chưa có prefab NovaSdk trong scene).</summary>
        public static NovaSdkBootstrap? Instance => s_instance;

        void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                // Đã có NovaSdk từ scene trước: giữ một bản.
                Destroy(gameObject);
                return;
            }
            s_instance = this;
            transform.SetParent(null);
            DontDestroyOnLoad(gameObject);

            if (settings == null)
            {
                Debug.LogError("[Nova] NovaSdk prefab: kéo asset NovaSdkSettings vào ô Settings (Create > NovaGames > SDK Settings)", this);
                return;
            }
            _ = NovaSdk.InitializeAsync(settings);

            SpawnPopups();
            if (createEventSystem)
            {
                EnsureEventSystem();
                SceneManager.sceneLoaded += OnSceneLoaded;
            }

            NovaRemoteConfig.Updated += ApplyRemoteConfig;
            ApplyRemoteConfig();
            ApplyWhenReady();
        }

        void OnDestroy()
        {
            if (s_instance != this) return;
            NovaRemoteConfig.Updated -= ApplyRemoteConfig;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            s_instance = null;
        }

        void SpawnPopups()
        {
            NovaNoInternet.Enabled = noInternetPopup;
            if (noInternetPopup)
            {
                if (noInternetPopupPrefab != null && NoInternetPopup.Instance == null) Instantiate(noInternetPopupPrefab, transform);
                else if (noInternetPopupPrefab == null) Debug.LogWarning("[Nova] NovaSdk prefab: No Internet Popup is on but the prefab is not assigned", this);
            }
            // Popup là con của NovaSdk nên sống qua mọi scene; popup đặt riêng trong scene (nếu có) được ưu tiên khi hiện.
            if (ratingPopupPrefab != null) Instantiate(ratingPopupPrefab, transform);
        }

        async void ApplyWhenReady()
        {
            // Lần đầu Ready có thể chỉ dùng cache/default mà không phát Updated.
            await NovaSdk.WhenReady;
            if (this != null) ApplyRemoteConfig();
        }

        void ApplyRemoteConfig()
        {
            bool remoteOn = !NovaRemoteConfig.TryGet(noInternetRemoteKey, out bool on) || on;
            NovaNoInternet.Enabled = noInternetPopup && remoteOn;
            if (NovaRemoteConfig.TryGet(ratingMinLevelRemoteKey, out int minLevel)) NovaRating.MinLevel = minLevel;
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode) => EnsureEventSystem();

        // Scene mới có EventSystem riêng thì tắt bản của SDK, tránh hai EventSystem cùng chạy.
        void EnsureEventSystem()
        {
            bool sceneHasOwn = false;
            foreach (var system in FindObjectsByType<EventSystem>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (system != _eventSystem) sceneHasOwn = true;

            if (sceneHasOwn)
            {
                if (_eventSystem != null) _eventSystem.gameObject.SetActive(false);
                return;
            }
            if (_eventSystem == null)
            {
                var go = new GameObject("EventSystem");
                go.transform.SetParent(transform, false);
                _eventSystem = go.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM && NOVA_INPUT_SYSTEM
                go.AddComponent<InputSystemUIInputModule>();
#else
                go.AddComponent<StandaloneInputModule>();
#endif
            }
            _eventSystem.gameObject.SetActive(true);
        }
    }
}
