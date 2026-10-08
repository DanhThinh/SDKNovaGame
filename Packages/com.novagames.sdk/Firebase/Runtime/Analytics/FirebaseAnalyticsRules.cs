#nullable enable
using System;
using System.Collections.Generic;
using Firebase.Analytics;
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.Privacy;

namespace NovaGames.Mobile.Firebase
{
    // Giới hạn của Firebase Analytics (https://support.google.com/firebase/answer/9237506).
    internal static class FirebaseAnalyticsRules
    {
        public const int MaxEventNameLength = 40;
        public const int MaxParamsPerEvent = 25;
        public const int MaxParamNameLength = 40;
        public const int MaxParamStringLength = 100;
        public const int MaxUserPropertyNameLength = 24;
        public const int MaxUserPropertyValueLength = 36;
        public const int MaxUserIdLength = 256;

        static readonly string[] ReservedPrefixes = { "firebase_", "google_", "ga_" };

        static readonly HashSet<string> ReservedEventNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "ad_activeview", "ad_click", "ad_exposure", "ad_query", "ad_reward", "adunit_exposure",
            "app_background", "app_clear_data", "app_exception", "app_remove", "app_store_refund",
            "app_store_subscription_cancel", "app_store_subscription_convert", "app_store_subscription_renew",
            "app_uninstall", "app_update", "app_upgrade", "dynamic_link_app_open", "dynamic_link_app_update",
            "dynamic_link_first_open", "error", "first_open", "first_visit", "in_app_purchase",
            "notification_dismiss", "notification_foreground", "notification_open", "notification_receive",
            "os_update", "session_start", "session_start_with_rollout", "user_engagement",
        };

        static readonly HashSet<string> ReservedUserPropertyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "first_open_after_install", "first_open_time", "first_visit_time", "last_deep_link_referrer", "user_id",
        };

        public static bool IsValidEventName(string? name, out string reason) =>
            IsValidName(name, MaxEventNameLength, out reason)
            && Check(!ReservedEventNames.Contains(name!), "reserved event name", out reason);

        public static bool IsValidParamName(string? name, out string reason) =>
            IsValidName(name, MaxParamNameLength, out reason);

        public static bool IsValidUserPropertyName(string? name, out string reason) =>
            IsValidName(name, MaxUserPropertyNameLength, out reason)
            && Check(!ReservedUserPropertyNames.Contains(name!), "reserved user property name", out reason);

        public static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value.Substring(0, maxLength);

        // Tên: bắt đầu bằng chữ cái ASCII, chỉ gồm chữ/số/underscore, không dùng prefix reserved.
        static bool IsValidName(string? name, int maxLength, out string reason)
        {
            if (string.IsNullOrEmpty(name)) { reason = "empty name"; return false; }
            if (name!.Length > maxLength) { reason = "longer than " + maxLength + " characters"; return false; }
            if (!IsAsciiLetter(name[0])) { reason = "must start with a letter"; return false; }
            for (int i = 1; i < name.Length; i++)
            {
                char c = name[i];
                if (!IsAsciiLetter(c) && !(c >= '0' && c <= '9') && c != '_')
                {
                    reason = "invalid character '" + c + "'";
                    return false;
                }
            }
            foreach (var prefix in ReservedPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal)) { reason = "reserved prefix " + prefix; return false; }
            }
            reason = string.Empty;
            return true;
        }

        static bool Check(bool ok, string failure, out string reason)
        {
            reason = ok ? string.Empty : failure;
            return ok;
        }

        static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        public static string FormatName(AdFormat format) => format switch
        {
            AdFormat.Interstitial => "interstitial",
            AdFormat.Rewarded => "rewarded",
            AdFormat.RewardedInterstitial => "rewarded_interstitial",
            AdFormat.AppOpen => "app_open",
            AdFormat.Banner => "banner",
            AdFormat.MRec => "mrec",
            AdFormat.Native => "native",
            _ => "unknown",
        };
    }

    // Consent Mode v2: Unknown không gửi (giữ default khai báo trong manifest/plist).
    internal static class FirebaseConsentMapper
    {
        public static Dictionary<ConsentType, ConsentStatus> Map(ConsentSnapshot snapshot)
        {
            var map = new Dictionary<ConsentType, ConsentStatus>(4);
            Add(map, ConsentType.AnalyticsStorage, snapshot.AnalyticsStorage);
            Add(map, ConsentType.AdStorage, snapshot.AdStorage);
            Add(map, ConsentType.AdUserData, snapshot.AdUserData);
            Add(map, ConsentType.AdPersonalization, snapshot.AdPersonalization);
            // Trẻ em (COPPA/Families): không bao giờ cho dùng dữ liệu để quảng cáo cá nhân hóa.
            if (snapshot.IsUnderAge)
            {
                map[ConsentType.AdUserData] = ConsentStatus.Denied;
                map[ConsentType.AdPersonalization] = ConsentStatus.Denied;
            }
            return map;
        }

        static void Add(Dictionary<ConsentType, ConsentStatus> map, ConsentType type, ConsentState state)
        {
            switch (state)
            {
                case ConsentState.Granted:
                case ConsentState.NotRequired:
                    map[type] = ConsentStatus.Granted;
                    break;
                case ConsentState.Denied:
                    map[type] = ConsentStatus.Denied;
                    break;
            }
        }
    }
}
