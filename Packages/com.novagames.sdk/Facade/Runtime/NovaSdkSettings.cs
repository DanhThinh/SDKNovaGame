#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.Diagnostics;
using NovaGames.Mobile.Iap;
using NovaGames.Mobile.Notifications;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.RemoteConfig;
using NovaGames.Mobile.Tracking;
using UnityEngine;

namespace NovaGames.Mobile
{
    /// <summary>Ai cung cấp consent (GDPR/US) cho SDK.</summary>
    public enum NovaConsentSource : byte
    {
        /// <summary>SDK tự chạy Google UMP lúc khởi động (cần GMA plugin + message trên AdMob console).</summary>
        [InspectorName("Google UMP (recommended)")] GoogleUmp,
        /// <summary>Game tự lấy consent (CMP khác) và gọi <see cref="NovaSdk.SetConsent"/>.</summary>
        [InspectorName("Game calls NovaSdk.SetConsent")] Game,
        /// <summary>Coi như đồng ý mọi mục đích. CHỈ để test (Editor/Development build); bản release tự chuyển sang Google UMP.</summary>
        [InspectorName("Assume granted (testing only)")] AssumeGrantedForTesting,
    }

    /// <summary>
    /// Toàn bộ cấu hình SDK trong một asset: <i>Create > NovaGames > SDK Settings</i>, kéo các asset con vào rồi truyền
    /// cho <see cref="NovaSdk.InitializeAsync"/>. Ô nào để trống thì module tương ứng tắt.
    /// </summary>
    [CreateAssetMenu(menuName = "NovaGames/SDK Settings", fileName = "NovaSdkSettings", order = -100)]
    public sealed class NovaSdkSettings : ScriptableObject
    {
        [Header("Remote Config")]
        [Tooltip("Asset Remote Config Definitions của game (enum key + kiểu + default). Để trống = NovaRemoteConfig không dùng được.")]
        [SerializeField] RemoteConfigDefinitions? remoteConfig;

        [Header("Ads")]
        [Tooltip("Create > NovaGames > Ads Config (MAX). Để trống nếu không format nào dùng MAX.")]
        [SerializeField] AdsConfig? maxAds;
        [Tooltip("Create > NovaGames > Ads Config (AdMob). Để trống nếu không format nào dùng AdMob.")]
        [SerializeField] AdsConfig? admobAds;
        [Tooltip("Mediation cho từng loại ads (None = tắt). Ví dụ full-screen MAX, banner AdMob để có collapsible banner.")]
        [SerializeField] AdsMediationSelection adsMediation = new AdsMediationSelection();
        [Tooltip("Tự dừng game (Time.timeScale = 0, tắt âm thanh) khi ad full-screen hiển thị, khôi phục khi đóng.")]
        [SerializeField] bool pauseGameDuringFullScreenAds = true;

        [Header("Crashlytics")]
        [Tooltip("Gửi crash lên Firebase Crashlytics (cần package com.google.firebase.crashlytics).")]
        [SerializeField] bool crashReporting = true;
        [Tooltip("Exception C# không được bắt báo là crash (fatal) thay vì non-fatal.")]
        [SerializeField] bool uncaughtExceptionsAsFatal;

        [Header("Tracking")]
        [Tooltip("Create > NovaGames > Adjust Config. Để trống = không bật Adjust.")]
        [SerializeField] TrackingSinkConfig? adjust;

        [Header("IAP")]
        [Tooltip("Create > NovaGames > IAP Config. Để trống = không bật IAP.")]
        [SerializeField] IapConfig? iap;

        [Header("Notifications")]
        [Tooltip("Create > NovaGames > Notification Config. Để trống = không bật thông báo local.")]
        [SerializeField] NotificationConfig? notifications;

        [Header("Consent")]
        [Tooltip("Google UMP: SDK tự hiện form consent (GDPR/US) lúc khởi động. Game: game tự gọi NovaSdk.SetConsent. " +
                 "Assume granted: coi như người dùng đồng ý mọi mục đích, CHỈ để test.")]
        [SerializeField] NovaConsentSource consentSource = NovaConsentSource.GoogleUmp;
        [Tooltip("Game dành cho trẻ dưới tuổi đồng ý (GDPR): UMP không hiện form, ads không cá nhân hóa, Adjust bật COPPA.")]
        [SerializeField] bool underAgeOfConsent;
        [Tooltip("Chỉ Development build: giả lập vùng địa lý để test form UMP (máy thật cần thêm hashed device ID bên dưới).")]
        [SerializeField] ConsentDebugGeography debugGeography = ConsentDebugGeography.Disabled;
        [Tooltip("Chỉ Development build: hashed device ID mà UMP in ra log khi chạy lần đầu trên máy test.")]
        [SerializeField] string[] consentTestDeviceHashedIds = Array.Empty<string>();

        [Header("iOS App Tracking Transparency")]
        [Tooltip("iOS: hiện popup ATT (xin dùng IDFA cho quảng cáo cá nhân hóa) ngay sau form consent lúc khởi động. Tắt nếu muốn " +
                 "tự hỏi đúng lúc bằng NovaPrivacy.RequestTracking. Game dưới tuổi (Under Age Of Consent) không bao giờ hỏi.")]
        [SerializeField] bool requestAttOnStartup = true;
        [Tooltip("Câu giải thích hiện trong popup ATT (NSUserTrackingUsageDescription). Build iOS tự ghi vào Info.plist.")]
        [SerializeField] string attUsageDescription = DefaultAttUsageDescription;

        /// <summary>Câu giải thích mặc định trong popup ATT.</summary>
        public const string DefaultAttUsageDescription = "Your data will be used to deliver personalized ads to you.";

        public RemoteConfigDefinitions? RemoteConfig => remoteConfig;
        public AdsConfig? MaxAds => maxAds;
        public AdsConfig? AdMobAds => admobAds;
        public AdsMediationSelection Mediation => adsMediation;
        public TrackingSinkConfig? Adjust => adjust;
        public IapConfig? Iap => iap;
        public NotificationConfig? Notifications => notifications;
        public NovaConsentSource ConsentSource => consentSource;

        /// <summary>Game có dùng ATT không (game dưới tuổi thì không).</summary>
        public bool UsesAtt => !underAgeOfConsent;

        /// <summary>Câu giải thích trong popup ATT, ghi vào Info.plist lúc build iOS.</summary>
        public string AttUsageDescription => attUsageDescription?.Trim() ?? string.Empty;

        /// <summary>Lỗi cấu hình cho platform (asset kéo nhầm, thiếu ID, test ID trong release, ...). Rỗng = hợp lệ.</summary>
        public IReadOnlyList<string> Validate(bool isDevelopment, bool isIos) => ToSetup(isDevelopment, isIos).Issues;

        internal NovaSdkSetup ToSetup(bool isDevelopment, bool isIos, bool isEditor = false)
        {
            var setup = new NovaSdkSetup
            {
                IsDevelopment = isDevelopment,
                IsIos = isIos,
                IsEditor = isEditor,
                CrashReporting = crashReporting
                    ? new CrashReportingOptions { UncaughtExceptionsAsFatal = uncaughtExceptionsAsFatal }
                    : null,
                RemoteConfig = remoteConfig,
                PauseGameDuringAds = pauseGameDuringFullScreenAds,
                ConsentSource = consentSource,
                AttAllowed = !underAgeOfConsent,
                RequestAttOnStartup = requestAttOnStartup && !underAgeOfConsent,
                ConsentOptions = new ConsentGatherOptions(underAgeOfConsent, isDevelopment)
                {
                    DebugGeography = isDevelopment ? debugGeography : ConsentDebugGeography.Disabled,
                    TestDeviceHashedIds = isDevelopment && consentTestDeviceHashedIds != null
                        ? consentTestDeviceHashedIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).ToArray()
                        : Array.Empty<string>(),
                },
            };

            if (remoteConfig != null)
            {
                setup.RemoteKeys = remoteConfig.AllKeys;
                if (remoteConfig is IAdsConfigKeysSource keys) setup.AdsKeys = keys.AdsKeys;
                else setup.Issues.Add("Remote Config Definitions does not implement IAdsConfigKeysSource: ads use the SDK default " +
                                      "key names (ad_*); map them to your RemoteKey enum to manage every key in one place");
            }
            else
            {
                setup.Issues.Add("Remote Config Definitions is not assigned: NovaRemoteConfig and remote ad settings use defaults");
            }

            try
            {
                setup.Ads = NovaGames.Mobile.Ads.AdsMediation.ToOptions(adsMediation, maxAds, admobAds, Array.Empty<AdPlacement>(), isDevelopment, isIos);
                setup.Issues.AddRange(NovaGames.Mobile.Ads.AdsMediation.Validate(adsMediation, maxAds, admobAds, isDevelopment, isIos));
                setup.UsesGoogleTestIds = admobAds != null && admobAds.UsesTestAdUnitIds
                                          && setup.Ads.UsedProviders.Contains(AdsProvider.AdMob);
            }
            catch (ArgumentException e)
            {
                // Kéo nhầm asset (AdMob vào ô MAX hoặc ngược lại): tắt Ads, báo lỗi.
                setup.Issues.Add("Ads: " + e.Message);
            }

            if (adjust != null)
            {
                setup.AdjustSettings = adjust.ToSettings(isDevelopment, isIos);
                foreach (var issue in adjust.Validate(isDevelopment, isIos)) setup.Issues.Add("Adjust: " + issue);
            }

            if (iap != null)
            {
                setup.Iap = iap.ToOptions(isDevelopment, isIos, isEditor);
                foreach (var issue in iap.Validate(isDevelopment, isIos)) setup.Issues.Add("IAP: " + issue);
            }

            if (notifications != null)
            {
                setup.Notifications = notifications.ToOptions(isIos);
                foreach (var issue in notifications.Validate()) setup.Issues.Add("Notifications: " + issue);
            }

            if (isIos && !underAgeOfConsent && string.IsNullOrWhiteSpace(attUsageDescription))
                setup.Issues.Add("ATT Usage Description is empty: the app crashes when it asks for ATT (NSUserTrackingUsageDescription is required)");

            if (consentSource == NovaConsentSource.AssumeGrantedForTesting && !isDevelopment)
            {
                // Bản phát hành không bao giờ tự coi người dùng là đã đồng ý: dùng Google UMP thay thế.
                setup.ConsentSource = NovaConsentSource.GoogleUmp;
                setup.Issues.Add("Consent Source is 'Assume granted (testing only)' in a release build: using Google UMP instead");
            }
            return setup;
        }
    }

    // Cấu hình đã resolve theo platform/build, dạng thuần (không phải ScriptableObject) để test được ngoài Unity.
    internal sealed class NovaSdkSetup
    {
        public bool IsDevelopment;
        public bool IsIos;
        public RemoteConfigDefinitions? RemoteConfig;
        public IReadOnlyList<ConfigKey> RemoteKeys = Array.Empty<ConfigKey>();
        public AdsOptions Ads = AdsOptions.Disabled;
        public AdsConfigKeys AdsKeys = AdsConfigKeys.Default;
        public bool UsesGoogleTestIds;
        public bool PauseGameDuringAds = true;
        public ITrackingSinkSettings? AdjustSettings;
        public IapOptions Iap = IapOptions.Disabled;
        public NotificationOptions? Notifications;
        public bool IsEditor;
        public CrashReportingOptions? CrashReporting;
        public NovaConsentSource ConsentSource = NovaConsentSource.GoogleUmp;
        public ConsentGatherOptions ConsentOptions = new ConsentGatherOptions(false, false);
        // ATT (iOS): được hỏi không (game dưới tuổi thì không) và có tự hỏi lúc khởi động không.
        public bool AttAllowed = true;
        public bool RequestAttOnStartup;
        public readonly List<string> Issues = new List<string>();
    }
}
