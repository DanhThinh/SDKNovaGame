#nullable enable
using System;
using System.Collections.Generic;
using AdjustSdk;
using NovaGames.Mobile.Tracking;

namespace NovaGames.Mobile.Attribution
{
    /// <summary>Cấu hình đã resolve theo platform/environment (từ AdjustTrackingConfig.ToSettings).</summary>
    public sealed record AdjustSinkSettings(string AppToken, bool IsSandbox) : ITrackingSinkSettings
    {
        public const string DefaultPurchaseEventName = "purchase";

        public static readonly IReadOnlyDictionary<string, string> NoEventTokens =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public AdjustLogLevel LogLevel { get; init; } = AdjustLogLevel.Error;

        /// <summary>Tên TrackingEvent -&gt; Adjust event token. Event không có token không gửi Adjust.</summary>
        public IReadOnlyDictionary<string, string> EventTokens { get; init; } = NoEventTokens;

        /// <summary>Tên event dùng tra token cho PurchaseRevenueEvent.</summary>
        public string PurchaseEventName { get; init; } = DefaultPurchaseEventName;

        public bool SendInBackground { get; init; }
        public bool CostDataInAttribution { get; init; }
        public string? DefaultTracker { get; init; }

        /// <summary>iOS: số giây Adjust chờ ATT trước khi gửi session đầu (0 = không chờ; Adjust giới hạn 360).</summary>
        public int AttConsentWaitingIntervalSeconds { get; init; }
    }
}
