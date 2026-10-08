#nullable enable
using System;
using System.Collections.Generic;
using AdjustSdk;
using NovaGames.Mobile.Tracking;
using UnityEngine;

namespace NovaGames.Mobile.Attribution
{
    /// <summary>App token + event token của Adjust. Environment suy ra từ build: Development = Sandbox, release = Production.</summary>
    [CreateAssetMenu(menuName = "NovaGames/Adjust Config", fileName = "AdjustTrackingConfig", order = 10)]
    public sealed class AdjustTrackingConfig : TrackingSinkConfig
    {
        [Header("App Token (Adjust dashboard > App settings)")]
        [SerializeField] string androidAppToken = string.Empty;
        [SerializeField] string iosAppToken = string.Empty;

        [Header("Event Token")]
        [Tooltip("Tên TrackingEvent game gọi -> token Adjust. Event không có trong bảng chỉ gửi Firebase.")]
        [SerializeField] List<AdjustEventToken> events = new List<AdjustEventToken>();
        [Tooltip("Tên event trong bảng trên dùng cho purchase revenue (SetRevenue + transaction id).")]
        [SerializeField] string purchaseEventName = AdjustSinkSettings.DefaultPurchaseEventName;

        [Header("SDK")]
        [Tooltip("Log level của Adjust ở bản release. Development luôn Verbose.")]
        [SerializeField] AdjustLogLevel releaseLogLevel = AdjustLogLevel.Error;
        [SerializeField] bool sendInBackground;
        [Tooltip("Trả cost data (cost type/amount/currency) trong attribution. Cần bật trên dashboard.")]
        [SerializeField] bool costDataInAttribution;
        [Tooltip("Tracker mặc định cho bản phát hành ngoài store (để trống nếu không dùng).")]
        [SerializeField] string defaultTracker = string.Empty;

        [Header("iOS")]
        [Tooltip("Số giây Adjust chờ người dùng trả lời ATT trước khi gửi install (0 = không chờ).")]
        [SerializeField, Range(0, AdjustConfigRules.MaxAttWaitingSeconds)] int attConsentWaitingIntervalSeconds;

        public override string SinkId => TrackingSinkIds.Adjust;

        public override ITrackingSinkSettings ToSettings(bool isDevelopment, bool isIos)
        {
            var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in events)
            {
                if (entry is null) continue;
                var name = entry.EventName;
                var token = entry.TokenFor(isIos);
                if (name.Length > 0 && token.Length > 0) tokens[name] = token;
            }

            return new AdjustSinkSettings(isIos ? iosAppToken.Trim() : androidAppToken.Trim(), isDevelopment)
            {
                LogLevel = isDevelopment ? AdjustLogLevel.Verbose : releaseLogLevel,
                EventTokens = tokens,
                PurchaseEventName = string.IsNullOrWhiteSpace(purchaseEventName)
                    ? AdjustSinkSettings.DefaultPurchaseEventName
                    : purchaseEventName.Trim(),
                SendInBackground = sendInBackground,
                CostDataInAttribution = costDataInAttribution,
                DefaultTracker = string.IsNullOrWhiteSpace(defaultTracker) ? null : defaultTracker.Trim(),
                AttConsentWaitingIntervalSeconds = isIos ? attConsentWaitingIntervalSeconds : 0,
            };
        }

        public override IReadOnlyList<string> Validate(bool isDevelopment, bool isIos)
        {
            var entries = new List<(string Name, string Token)>();
            foreach (var entry in events)
            {
                if (entry != null) entries.Add((entry.EventName, entry.TokenFor(isIos)));
            }
            return AdjustConfigRules.Validate(isIos ? iosAppToken : androidAppToken, entries, isIos);
        }
    }

    [Serializable]
    public sealed class AdjustEventToken
    {
        [SerializeField] string eventName = string.Empty;
        [SerializeField] string android = string.Empty;
        [SerializeField] string ios = string.Empty;

        public string EventName => eventName?.Trim() ?? string.Empty;
        public string TokenFor(bool isIos) => (isIos ? ios : android)?.Trim() ?? string.Empty;
    }

    // Phần thuần của Validate (không đụng ScriptableObject) để test được ngoài Unity.
    internal static class AdjustConfigRules
    {
        public const int MaxAttWaitingSeconds = 360;
        const int AppTokenLength = 12;
        const int EventTokenLength = 6;

        public static IReadOnlyList<string> Validate(string? appToken, IReadOnlyList<(string Name, string Token)> events, bool isIos)
        {
            var platform = isIos ? "iOS" : "Android";
            var issues = new List<string>();
            var token = appToken?.Trim() ?? string.Empty;
            if (token.Length == 0) issues.Add("No Adjust app token for " + platform);
            else if (!IsToken(token, AppTokenLength)) issues.Add("App token '" + token + "' is not a 12-character Adjust token");

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, eventToken) in events)
            {
                if (name.Length == 0)
                {
                    if (eventToken.Length > 0) issues.Add("Event token '" + eventToken + "' has no event name");
                    continue;
                }
                if (!names.Add(name)) issues.Add("Event '" + name + "' is listed more than once");
                if (eventToken.Length == 0) issues.Add("Event '" + name + "' has no " + platform + " token");
                else if (!IsToken(eventToken, EventTokenLength)) issues.Add("Event '" + name + "': '" + eventToken + "' is not a 6-character Adjust token");
            }
            return issues;
        }

        static bool IsToken(string value, int length)
        {
            if (value.Length != length) return false;
            foreach (var c in value)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))) return false;
            }
            return true;
        }
    }
}
