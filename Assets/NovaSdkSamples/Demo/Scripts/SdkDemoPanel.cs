#nullable enable
using System.Collections.Generic;
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.Iap;
using NovaGames.Mobile.Notifications;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.Tracking;
using UnityEngine;
using UnityEngine.UI;

namespace NovaGames.Mobile.Samples
{
    // Panel demo mọi chức năng SDK trên thiết bị: trạng thái tự cập nhật ở trên, nút chia theo mục ở dưới.
    // Mỗi nút gọi một hàm public bên dưới, mỗi hàm chỉ dùng API tĩnh Nova* (đúng cách game thật gọi SDK).
    public sealed class SdkDemoPanel : MonoBehaviour
    {
        // Product Id trong Samples/Data/IapConfig.asset.
        const string RemoveAdsProduct = "remove_ads";
        const string GemsProduct = "gem_pack_1";
        const int GemsPerPack = 100;
        const int MaxLogLines = 6;
        const float StatusInterval = 0.5f;

        [Header("Scene references")]
        [SerializeField] Text? statusText;
        [SerializeField] Text? logText;

        readonly Queue<string> _log = new Queue<string>();
        int _level = 10;
        int _gems;
        bool _autoAppOpen;
        string _lastDeepLink = "none";
        float _nextStatus;

        void Awake()
        {
            // Đăng ký trước khi SDK init xong để nhận cả giao dịch chưa trao và deep link lúc mở app.
            NovaIap.SetConsumableHandler(GrantConsumable);
            NovaIap.OnPurchased += OnPurchased;
            NovaAttribution.DeepLinkReceived += OnDeepLink;
            NovaPrivacy.ConsentChanged += OnConsentChanged;
            NovaRemoteConfig.Updated += OnRemoteConfigUpdated;
            NovaNotifications.Opened += OnNotificationOpened;
            NovaRating.Closed += OnRatingClosed;
            // Interstitial chỉ show từ level ad_inter_start_level (Remote Config): bắt đầu ở level cao để test ngay,
            // dùng nút Level -1 / +1 để thấy gate hoạt động.
            NovaAds.SetLevel(_level);
        }

        async void Start()
        {
            Log("Initializing SDK...");
            await NovaSdk.WhenReady;
            if (this == null) return;
            Log("SDK ready");
        }

        void OnDestroy()
        {
            NovaIap.SetConsumableHandler(null);
            NovaIap.OnPurchased -= OnPurchased;
            NovaAttribution.DeepLinkReceived -= OnDeepLink;
            NovaPrivacy.ConsentChanged -= OnConsentChanged;
            NovaRemoteConfig.Updated -= OnRemoteConfigUpdated;
            NovaNotifications.Opened -= OnNotificationOpened;
            NovaRating.Closed -= OnRatingClosed;
        }

        void Update()
        {
            if (Time.unscaledTime < _nextStatus) return;
            _nextStatus = Time.unscaledTime + StatusInterval;
            RefreshStatus();
        }

        // ---------------- Privacy / Remote Config ----------------

        public void ShowPrivacyOptions()
        {
            if (!NovaPrivacy.IsPrivacyOptionsRequired) Log("Privacy options not required here (only EEA/UK/US states)");
            NovaPrivacy.ShowPrivacyOptions(() => Log("Privacy options closed"));
        }

        public async void FetchRemoteConfig()
        {
            Log("Fetching Remote Config...");
            bool ok = await NovaRemoteConfig.FetchAsync();
            if (this == null) return;
            Log(ok ? "Remote Config fetched (" + NovaRemoteConfig.Source + ")" : "Remote Config fetch failed, using " + NovaRemoteConfig.Source);
        }

        // ---------------- Analytics ----------------

        public void SendAnalyticsEvent()
        {
            // Firebase luôn nhận; Adjust chỉ nhận nếu Adjust Config có token cho "sdk_test_button".
            NovaAnalytics.LogEvent("sdk_test_button",
                ("platform", Application.platform.ToString()),
                ("level", _level),
                ("development_build", Debug.isDebugBuild));
            Log("Event sent: sdk_test_button");
        }

        public void SetUserProperty()
        {
            NovaAnalytics.SetUserProperty("player_segment", _gems > 0 ? "payer" : "free");
            Log("User property player_segment = " + (_gems > 0 ? "payer" : "free"));
        }

        public void SetUserId()
        {
            NovaAnalytics.SetUserId("demo_user_1");
            Log("User id = demo_user_1");
        }

        // ---------------- Ads: full-screen ----------------

        public void ShowInterstitial()
        {
            Log("Showing interstitial...");
            NovaAds.ShowInterstitial(Placements.LevelEnd, () => Log("Interstitial done (shown or skipped)"));
        }

        public void ShowRewarded()
        {
            Log("Showing rewarded...");
            NovaAds.ShowRewarded(Placements.Revive,
                onRewarded: () => Log("Rewarded: reward granted"),
                onFailed: () => Log("Rewarded: no reward (not ready, closed early or failed)"));
        }

        public void ShowAppOpen()
        {
            Log("Showing app open...");
            NovaAds.ShowAppOpen(() => Log("App open done"), Placements.AppOpen);
        }

        public void ToggleAutoAppOpen()
        {
            _autoAppOpen = !_autoAppOpen;
            NovaAds.SetAutoShowAppOpen(_autoAppOpen, Placements.AppOpen);
            Log("Auto app open on resume: " + (_autoAppOpen ? "ON (background the app, then return)" : "OFF"));
        }

        public void LevelDown() => SetLevel(Mathf.Max(1, _level - 1));
        public void LevelUp() => SetLevel(_level + 1);

        void SetLevel(int level)
        {
            _level = level;
            NovaAds.SetLevel(level);
            Log("Level " + level + " (interstitial from level " + NovaRemoteConfig.GetInt(RemoteKey.ad_inter_start_level) + ")");
        }

        // ---------------- Ads: banner / MREC ----------------

        public void ShowBannerBottom()
        {
            NovaAds.ShowBanner(BannerPosition.Bottom, Placements.HomeBanner);
            Log("Banner requested (bottom)");
        }

        public void ShowBannerTop()
        {
            NovaAds.ShowBanner(BannerPosition.Top, Placements.HomeBanner);
            Log("Banner requested (top)");
        }

        public void HideBanner()
        {
            NovaAds.HideBanner(Placements.HomeBanner);
            Log("Banner hidden");
        }

        public void ShowMrec()
        {
            NovaAds.ShowMRec(MrecPosition.Centered, Placements.ResultMrec);
            Log("MREC requested (center)");
        }

        public void HideMrec()
        {
            NovaAds.HideMRec(Placements.ResultMrec);
            Log("MREC hidden");
        }

        public void OpenAdsDebugger()
        {
            NovaAds.OpenDebugger();
            Log("Mediation debugger requested (Development build only)");
        }

        // ---------------- IAP ----------------

        public void BuyRemoveAds() => Buy(RemoveAdsProduct);
        public void BuyGems() => Buy(GemsProduct);

        public void RestorePurchases()
        {
            Log("Restoring purchases...");
            NovaIap.Restore(ok => Log(ok ? "Restore finished" : "Restore failed"));
        }

        void Buy(string productId)
        {
            Log("Buying " + productId + "...");
            NovaIap.Purchase(productId, result => Log("Purchase " + result));
        }

        // Trao consumable: game cộng thưởng + lưu save game rồi trả true (false = SDK giữ giao dịch, gọi lại sau).
        bool GrantConsumable(IapGrant grant)
        {
            if (grant.ProductId == GemsProduct) _gems += GemsPerPack;
            return true;
        }

        void OnPurchased(IapGrant grant) => Log("Received " + grant.ProductId);

        // ---------------- Notifications ----------------

        public void RequestNotificationPermission()
        {
            Log("Requesting notification permission...");
            NovaNotifications.RequestPermission(allowed =>
                Log(allowed ? "Notifications allowed" : "Notifications not allowed (" + NovaNotifications.Permission + ")"));
        }

        // Android chỉ hiện thông báo khi game ở nền: bấm nút rồi về màn hình chính trong 10 giây.
        public void ScheduleTestNotification()
        {
            NovaNotifications.Schedule("demo_test", "NovaGames SDK", "Test notification: tap to open the demo",
                System.TimeSpan.FromSeconds(10), data: "from_demo_button");
            Log("Notification in 10 s: go to the home screen now");
        }

        public void ScheduleDailyNotification()
        {
            NovaNotifications.ScheduleDaily("demo_daily", "Daily reward", "Your daily gift is waiting!", hour: 19);
            Log("Daily notification at 19:00");
        }

        public void CancelAllNotifications()
        {
            NovaNotifications.CancelAll();
            Log("All notifications cancelled (reminders return when the game goes to background)");
        }

        public void OpenNotificationSettings()
        {
            NovaNotifications.OpenSettings();
            Log("Opening notification settings");
        }

        void OnNotificationOpened(NotificationOpened opened) =>
            Log("Opened by notification '" + opened.Id + "'" + (opened.Data != null ? " data=" + opened.Data : string.Empty) +
                (opened.IsColdStart ? " (cold start)" : string.Empty));

        // ---------------- No Internet ----------------

        public void ToggleNoInternet()
        {
            NovaNoInternet.Enabled = !NovaNoInternet.Enabled;
            Log("No Internet popup: " + (NovaNoInternet.Enabled ? "ON" : "OFF"));
        }

        // Giả lập mất mạng (Editor/Development build): popup hiện sau ~2 s.
        public void ToggleSimulateOffline()
        {
            StopAllCoroutines();
            NovaNoInternet.SimulateOffline = !NovaNoInternet.SimulateOffline;
            Log("Simulate offline: " + (NovaNoInternet.SimulateOffline ? "ON (popup in ~2 s, closes ~8 s later)" : "OFF"));
            if (NovaNoInternet.SimulateOffline) StartCoroutine(StopSimulatingOffline());
        }

        // Popup chặn mọi nút phía sau: tự tắt giả lập sau 8 s (thời gian thật) để demo đóng popup.
        System.Collections.IEnumerator StopSimulatingOffline()
        {
            yield return new WaitForSecondsRealtime(8f);
            NovaNoInternet.SimulateOffline = false;
            Log("Simulate offline: OFF (network back)");
        }

        // ---------------- Rating ----------------

        // Gọi ở màn thắng level trong game thật: chỉ hiện khi đủ level, chưa đánh giá, hết thời gian chờ.
        public void RatingShowIfEligible()
        {
            var eligibility = NovaRating.CheckEligibility(_level);
            if (!NovaRating.ShowIfEligible(_level)) Log("Rating not shown at level " + _level + ": " + eligibility);
        }

        public void RatingShowNow()
        {
            if (!NovaRating.Show()) Log("No RatingPopup in the scene");
        }

        public void RatingStoreReview()
        {
            Log("Requesting store review...");
            NovaRating.RequestStoreReview(() => Log("Store review flow finished"));
        }

        public void RatingReset()
        {
            NovaRating.ResetForTesting();
            Log("Rating state reset");
        }

        void OnRatingClosed(NovaGames.Mobile.Rating.RatingResult result) => Log("Rating closed: " + result);

        // ---------------- Crashlytics ----------------

        public void CrashLogAndKey()
        {
            NovaCrash.Log("demo button pressed at level " + _level);
            NovaCrash.SetCustomKey("level", _level);
            Log("Crashlytics breadcrumb + key level=" + _level + " (shown with the next crash)");
        }

        // Non-fatal hiện ở mục Non-fatals trên Firebase console sau vài phút.
        public void CrashTestNonFatal()
        {
            NovaCrash.TestNonFatal();
            Log("Non-fatal sent (Firebase console > Crashlytics > Non-fatals)");
        }

        // Development build trên máy: app tắt ngay; mở lại app để Crashlytics gửi báo cáo.
        public void CrashTestCrash()
        {
            Log("Crashing now (Development build on device only)...");
            NovaCrash.TestCrash();
        }

        // ---------------- Status ----------------

        void OnDeepLink(DeepLink link)
        {
            _lastDeepLink = link.Url + (link.IsDeferred ? " (deferred)" : string.Empty);
            Log("Deep link: " + _lastDeepLink);
        }

        void OnConsentChanged(ConsentSnapshot consent) =>
            Log("Consent: " + consent.Jurisdiction + ", can request ads: " + consent.CanRequestAds);

        void OnRemoteConfigUpdated() => Log("Remote Config updated (" + NovaRemoteConfig.Source + ")");

        void RefreshStatus()
        {
            if (statusText == null) return;
            var consent = NovaPrivacy.Consent;
            var attribution = NovaAttribution.Current;
            statusText.text =
                "<b>SDK</b> " + NovaSdk.State + "  |  " + Application.platform + (Debug.isDebugBuild ? " (development)" : string.Empty) + "\n" +
                "<b>Consent</b> " + consent.Jurisdiction + ", ads allowed: " + consent.CanRequestAds +
                ", privacy button: " + NovaPrivacy.IsPrivacyOptionsRequired + "\n" +
                "<b>Remote Config</b> " + NovaRemoteConfig.Source + ": ads " + NovaRemoteConfig.GetBool(RemoteKey.ad_enabled) +
                ", inter every " + NovaRemoteConfig.GetInt(RemoteKey.ad_inter_interval) + "s from level " +
                NovaRemoteConfig.GetInt(RemoteKey.ad_inter_start_level) + "\n" +
                "<b>Attribution</b> " + (attribution != null ? attribution.Network + " / " + attribution.Campaign : "none") +
                "  |  deep link: " + _lastDeepLink + "\n" +
                "<b>Ads</b> " + (NovaAds.IsInitialized ? "ready" : "not ready") + (NovaAds.IsRemoveAds ? " (remove_ads)" : string.Empty) +
                ", level " + _level + ", inter " + Ready(NovaAds.IsInterReady(Placements.LevelEnd)) +
                ", rewarded " + Ready(NovaAds.IsRewardReady(Placements.Revive)) +
                ", auto app open " + (_autoAppOpen ? "on" : "off") + ", MREC " + (NovaAds.IsMRecShowing(Placements.ResultMrec) ? "shown" : "hidden") + "\n" +
                "<b>IAP</b> " + (NovaIap.IsReady ? "ready" : "not ready") + ", gems " + _gems +
                ", remove_ads " + (NovaIap.IsOwned(RemoveAdsProduct) ? "owned" : NovaIap.GetPriceText(RemoveAdsProduct, "-")) +
                ", gem pack " + NovaIap.GetPriceText(GemsProduct, "-") + "\n" +
                "<b>Notifications</b> " + NovaNotifications.Permission + ", " + NovaNotifications.ScheduledIds.Count + " scheduled" +
                (NovaNotifications.LastOpened != null ? ", opened by " + NovaNotifications.LastOpened.Id : string.Empty) + "\n" +
                "<b>No Internet</b> popup " + (NovaNoInternet.Enabled ? "on" : "off") +
                ", network " + (NovaNoInternet.IsInternetReachable ? "online" : "offline") +
                (NovaNoInternet.SimulateOffline ? " (simulated)" : string.Empty) + (NovaNoInternet.IsShowing ? ", showing" : string.Empty) + "\n" +
                "<b>Rating</b> " + (NovaRating.IsRated ? "rated" : "not rated") + ", level " + _level + ": " + NovaRating.CheckEligibility(_level) +
                (NovaRating.HasPopup ? string.Empty : " (no popup in scene)") + "\n" +
                "<b>Crashlytics</b> " + (NovaCrash.IsReady ? "ready" : "not ready");
        }

        static string Ready(bool ready) => ready ? "ready" : "not ready";

        void Log(string message)
        {
            Debug.Log("[SdkDemo] " + message);
            _log.Enqueue(System.DateTime.Now.ToString("HH:mm:ss") + "  " + message);
            while (_log.Count > MaxLogLines) _log.Dequeue();
            if (logText != null) logText.text = string.Join("\n", _log);
        }
    }
}
