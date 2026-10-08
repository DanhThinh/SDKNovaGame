#nullable enable
using System;
using System.Collections.Generic;
using GoogleMobileAds.Ump.Api;
using UnityEngine;

namespace NovaGames.Mobile.Privacy.Ump
{
    /// <summary>
    /// Mọi lời gọi Google UMP và đọc chuỗi IAB nằm ở đây để <see cref="UmpConsentPlatform"/> test được bằng fake.
    /// Gọi trên main thread. Callback nhận null khi thành công, hoặc mô tả lỗi; có thể chạy trên thread bất kỳ.
    /// </summary>
    internal interface IUmpApi
    {
        void RequestConsentInfoUpdate(ConsentGatherOptions options, Action<string?> onDone);
        void LoadAndShowConsentFormIfRequired(Action<string?> onDone);
        void ShowPrivacyOptionsForm(Action<string?> onDone);
        UmpConsentStatus ConsentStatus { get; }
        bool CanRequestAds { get; }
        bool IsPrivacyOptionsRequired { get; }
        IabConsentData ReadIabData();
    }

    internal sealed class GoogleUmpApi : IUmpApi
    {
        public void RequestConsentInfoUpdate(ConsentGatherOptions options, Action<string?> onDone)
        {
            var request = new ConsentRequestParameters { TagForUnderAgeOfConsent = options.IsUnderAge };
            // Giả lập vùng địa lý chỉ có tác dụng trên test device; không bao giờ gửi trong bản release.
            if (options.IsDevelopment
                && (options.DebugGeography != ConsentDebugGeography.Disabled || options.TestDeviceHashedIds.Count > 0))
            {
                request.ConsentDebugSettings = new ConsentDebugSettings
                {
                    DebugGeography = ToUmp(options.DebugGeography),
                    TestDeviceHashedIds = new List<string>(options.TestDeviceHashedIds),
                };
            }
            ConsentInformation.Update(request, error => onDone(Describe(error)));
        }

        public void LoadAndShowConsentFormIfRequired(Action<string?> onDone) =>
            ConsentForm.LoadAndShowConsentFormIfRequired(error => onDone(Describe(error)));

        public void ShowPrivacyOptionsForm(Action<string?> onDone) =>
            ConsentForm.ShowPrivacyOptionsForm(error => onDone(Describe(error)));

        public UmpConsentStatus ConsentStatus => ConsentInformation.ConsentStatus switch
        {
            GoogleMobileAds.Ump.Api.ConsentStatus.NotRequired => UmpConsentStatus.NotRequired,
            GoogleMobileAds.Ump.Api.ConsentStatus.Required => UmpConsentStatus.Required,
            GoogleMobileAds.Ump.Api.ConsentStatus.Obtained => UmpConsentStatus.Obtained,
            _ => UmpConsentStatus.Unknown,
        };

        public bool CanRequestAds => ConsentInformation.CanRequestAds();

        public bool IsPrivacyOptionsRequired =>
            ConsentInformation.PrivacyOptionsRequirementStatus == PrivacyOptionsRequirementStatus.Required;

        public IabConsentData ReadIabData() => IabStorage.Read();

        static string? Describe(FormError? error) => error is null ? null : error.ErrorCode + ": " + error.Message;

        static DebugGeography ToUmp(ConsentDebugGeography geography) => geography switch
        {
            ConsentDebugGeography.Eea => DebugGeography.EEA,
            ConsentDebugGeography.Other => DebugGeography.Other,
            ConsentDebugGeography.RegulatedUsState => DebugGeography.RegulatedUSState,
            _ => DebugGeography.Disabled,
        };
    }

    /// <summary>
    /// UMP ghi chuỗi IAB vào default SharedPreferences (Android) / NSUserDefaults (iOS). Trên iOS PlayerPrefs chính là
    /// NSUserDefaults nên đọc thẳng; trên Android PlayerPrefs dùng file riêng nên phải đọc qua JNI. Editor: không có dữ liệu.
    /// </summary>
    internal static class IabStorage
    {
        const string GdprApplies = "IABTCF_gdprApplies";
        const string PurposeConsents = "IABTCF_PurposeConsents";
        const string TcString = "IABTCF_TCString";
        const string GppString = "IABGPP_HDR_GppString";
        const string GppSectionIds = "IABGPP_GppSID";

        public static IabConsentData Read()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            string file = activity.Call<string>("getPackageName") + "_preferences";
            using var prefs = activity.Call<AndroidJavaObject>("getSharedPreferences", file, 0);
            return new IabConsentData(
                prefs.Call<bool>("contains", GdprApplies) ? AndroidInt(prefs, GdprApplies) : null,
                AndroidString(prefs, PurposeConsents),
                AndroidString(prefs, TcString),
                AndroidString(prefs, GppString),
                AndroidString(prefs, GppSectionIds));
#elif UNITY_IOS && !UNITY_EDITOR
            return new IabConsentData(
                PlayerPrefs.HasKey(GdprApplies) ? PlayerPrefs.GetInt(GdprApplies) : (int?)null,
                PlayerPrefs.GetString(PurposeConsents, string.Empty),
                PlayerPrefs.GetString(TcString, string.Empty),
                PlayerPrefs.GetString(GppString, string.Empty),
                PlayerPrefs.GetString(GppSectionIds, string.Empty));
#else
            return IabConsentData.Empty;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // CMP khác có thể lưu sai kiểu (vd. gdprApplies là chuỗi): bỏ qua key đó thay vì làm hỏng cả snapshot.
        static int? AndroidInt(AndroidJavaObject prefs, string key)
        {
            try
            {
                return prefs.Call<int>("getInt", key, -1);
            }
            catch (AndroidJavaException)
            {
                return int.TryParse(AndroidString(prefs, key), out var value) ? value : (int?)null;
            }
        }

        static string AndroidString(AndroidJavaObject prefs, string key)
        {
            try
            {
                return prefs.Call<string>("getString", key, string.Empty) ?? string.Empty;
            }
            catch (AndroidJavaException)
            {
                return string.Empty;
            }
        }
#endif
    }
}
