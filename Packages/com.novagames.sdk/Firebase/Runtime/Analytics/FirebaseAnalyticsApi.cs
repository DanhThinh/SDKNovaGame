#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Firebase.Analytics;
using NovaGames.Mobile.Tracking;

namespace NovaGames.Mobile.Firebase
{
    // Chỉ gọi sau khi FirebaseAppInitializer báo Available. Param đã được validate bởi sink.
    internal interface IFirebaseAnalyticsApi
    {
        void LogEvent(string name, IReadOnlyList<TrackingParam> parameters);
        void SetConsent(IDictionary<ConsentType, ConsentStatus> consent);
        void SetUserProperty(string name, string? value);
        void SetUserId(string? id);
        void SetSessionTimeout(TimeSpan timeout);
        void SetCollectionEnabled(bool enabled);
        Task LogAppleTransactionAsync(string transactionId);
    }

    internal sealed class FirebaseAnalyticsApi : IFirebaseAnalyticsApi
    {
        public void LogEvent(string name, IReadOnlyList<TrackingParam> parameters)
        {
            if (parameters.Count == 0)
            {
                FirebaseAnalytics.LogEvent(name);
                return;
            }

            var array = new Parameter[parameters.Count];
            for (int i = 0; i < array.Length; i++)
            {
                var p = parameters[i];
                array[i] = p.Kind switch
                {
                    TrackingParamKind.String => new Parameter(p.Name, p.StringValue ?? string.Empty),
                    TrackingParamKind.Long => new Parameter(p.Name, p.LongValue),
                    _ => new Parameter(p.Name, p.DoubleValue),
                };
            }
            FirebaseAnalytics.LogEvent(name, array);
        }

        public void SetConsent(IDictionary<ConsentType, ConsentStatus> consent) => FirebaseAnalytics.SetConsent(consent);

        // null xóa user property / user id.
        public void SetUserProperty(string name, string? value) => FirebaseAnalytics.SetUserProperty(name, value);
        public void SetUserId(string? id) => FirebaseAnalytics.SetUserId(id);

        public void SetSessionTimeout(TimeSpan timeout) => FirebaseAnalytics.SetSessionTimeoutDuration(timeout);
        public void SetCollectionEnabled(bool enabled) => FirebaseAnalytics.SetAnalyticsCollectionEnabled(enabled);
        public Task LogAppleTransactionAsync(string transactionId) => FirebaseAnalytics.LogAppleTransactionAsync(transactionId);
    }
}
