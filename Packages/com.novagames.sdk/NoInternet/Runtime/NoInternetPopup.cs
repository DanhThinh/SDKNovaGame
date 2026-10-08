#nullable enable
using System;
using NovaGames.Mobile.Connectivity;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NovaGames.Mobile.NoInternet
{
    /// <summary>
    /// Popup "No Internet": mất mạng thì che màn hình, dừng game, ẩn MREC, chặn app open; có mạng lại thì tự đóng và
    /// khôi phục. Dùng prefab <c>NoInternet/Prefabs/NoInternetPopup</c> (kéo vào GameService, hoặc đặt sẵn trong scene
    /// đầu tiên). Bật/tắt bằng <see cref="NovaNoInternet.Enabled"/>. Sửa chữ/màu trực tiếp trong prefab.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NoInternetPopup : MonoBehaviour
    {
        [Header("Scene references")]
        [Tooltip("Phần che màn hình (bật/tắt khi hiện/đóng popup).")]
        [SerializeField] GameObject? panel;
        [SerializeField] Button? settingsButton;
        [SerializeField] Button? retryButton;
        [Tooltip("Dòng trạng thái nhỏ (vd. \"Still offline\" sau khi bấm Retry). Có thể để trống.")]
        [SerializeField] Text? statusText;

        [Header("Behaviour")]
        [Tooltip("Mất mạng liên tục bao nhiêu giây mới hiện popup (tránh nháy khi đổi Wi-Fi/4G).")]
        [SerializeField, Min(0f)] float showAfterSeconds = 2f;
        [Tooltip("Có mạng lại liên tục bao nhiêu giây thì đóng popup.")]
        [SerializeField, Min(0f)] float hideAfterSeconds = 0.5f;
        [Tooltip("Chu kỳ kiểm tra mạng (giây, không phụ thuộc Time.timeScale).")]
        [SerializeField, Min(0.1f)] float checkInterval = 0.5f;
        [Tooltip("Dừng game (Time.timeScale = 0, tắt âm thanh) khi popup hiện.")]
        [SerializeField] bool pauseGame = true;
        [Tooltip("Giữ popup qua mọi scene (DontDestroyOnLoad).")]
        [SerializeField] bool keepAcrossScenes = true;
        [SerializeField] string stillOfflineMessage = "Still offline. Please check Wi-Fi or mobile data.";

        static NoInternetPopup? s_instance;

        OfflineDetector _detector = null!;
        float _nextCheck;
        bool _showing;
        float _savedTimeScale = 1f;
        bool _savedAudioPause;
        IDisposable? _mrecHidden;
        IDisposable? _appOpenSuppressed;

        /// <summary>Instance đang chạy (null nếu chưa tạo).</summary>
        public static NoInternetPopup? Instance => s_instance;

        void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                // Đã có popup từ scene trước (DontDestroyOnLoad): giữ một instance.
                Destroy(gameObject);
                return;
            }
            s_instance = this;
            if (keepAcrossScenes)
            {
                transform.SetParent(null);
                DontDestroyOnLoad(gameObject);
            }

            _detector = new OfflineDetector(TimeSpan.FromSeconds(showAfterSeconds), TimeSpan.FromSeconds(hideAfterSeconds));
            if (panel != null) panel.SetActive(false);
            if (settingsButton != null) settingsButton.onClick.AddListener(OpenNetworkSettings);
            if (retryButton != null) retryButton.onClick.AddListener(Retry);
            NovaNoInternet.EnabledChanged += OnEnabledChanged;
        }

        void OnDestroy()
        {
            if (s_instance != this) return;
            NovaNoInternet.EnabledChanged -= OnEnabledChanged;
            if (_showing) SetShowing(false);
            s_instance = null;
        }

        void Update()
        {
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + checkInterval;
            bool offline = _detector.Update(NovaNoInternet.IsInternetReachable, DateTime.UtcNow);
            bool shouldShow = offline && NovaNoInternet.Enabled;
            // Ad full-screen đang hiện (đã tự pause game): đợi ad đóng rồi mới che màn hình, tránh hai bên ghi đè
            // trạng thái pause của nhau.
            if (shouldShow && !_showing && NovaAds.IsShowingFullScreen) return;
            if (shouldShow != _showing) SetShowing(shouldShow);
        }

        /// <summary>Nút Retry: kiểm tra lại ngay, có mạng thì đóng popup.</summary>
        public void Retry()
        {
            bool reachable = NovaNoInternet.IsInternetReachable;
            _detector.ForceUpdate(reachable);
            if (reachable)
            {
                SetShowing(false);
                return;
            }
            if (statusText != null) statusText.text = stillOfflineMessage;
        }

        /// <summary>Nút Settings: Android mở cài đặt mạng, iOS mở Settings của app (iOS không cho mở thẳng Wi-Fi).</summary>
        public void OpenNetworkSettings()
        {
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var intent = new AndroidJavaObject("android.content.Intent", "android.settings.WIRELESS_SETTINGS"))
                {
                    activity.Call("startActivity", intent);
                }
#elif UNITY_IOS && !UNITY_EDITOR
                Application.OpenURL("app-settings:");
#else
                Debug.Log("[Nova][no_internet] Network settings open only on device");
#endif
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        void OnEnabledChanged(bool enabled)
        {
            if (!enabled && _showing) SetShowing(false);
            _nextCheck = 0f;
        }

        void SetShowing(bool showing)
        {
            if (_showing == showing) return;
            _showing = showing;
            if (panel != null) panel.SetActive(showing);
            if (statusText != null) statusText.text = string.Empty;

            if (showing)
            {
                if (EventSystem.current == null)
                    Debug.LogWarning("[Nova][no_internet] No EventSystem in the scene: popup buttons cannot be clicked");
                if (pauseGame)
                {
                    _savedTimeScale = Time.timeScale;
                    _savedAudioPause = AudioListener.pause;
                    Time.timeScale = 0f;
                    AudioListener.pause = true;
                }
                // MREC/app open là view native nằm trên UI Unity: ẩn/chặn để không che popup.
                _mrecHidden = NovaAds.HideMRecsTemporarily();
                _appOpenSuppressed = NovaAds.SuppressAppOpen("no_internet");
            }
            else
            {
                if (pauseGame)
                {
                    // Chỉ khôi phục nếu không ai khác đổi timeScale trong lúc popup hiện.
                    if (Time.timeScale == 0f) Time.timeScale = _savedTimeScale;
                    AudioListener.pause = _savedAudioPause;
                }
                _appOpenSuppressed?.Dispose();
                _appOpenSuppressed = null;
                _mrecHidden?.Dispose();
                _mrecHidden = null;
            }
            NovaNoInternet.ReportVisibility(showing);
        }
    }
}
