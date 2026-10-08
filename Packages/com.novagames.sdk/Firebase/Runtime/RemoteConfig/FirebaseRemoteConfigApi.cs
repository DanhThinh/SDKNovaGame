#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Firebase.RemoteConfig;

namespace NovaGames.Mobile.Firebase
{
    internal readonly struct RemoteFetchInfo
    {
        public RemoteFetchInfo(LastFetchStatus status, FetchFailureReason failureReason, DateTime fetchTime, DateTime throttledEndTime)
        {
            Status = status;
            FailureReason = failureReason;
            FetchTime = fetchTime;
            ThrottledEndTime = throttledEndTime;
        }

        public LastFetchStatus Status { get; }
        public FetchFailureReason FailureReason { get; }
        public DateTime FetchTime { get; }
        public DateTime ThrottledEndTime { get; }
    }

    // Chỉ gọi sau khi FirebaseAppInitializer báo Available.
    internal interface IFirebaseRemoteConfigApi
    {
        Task EnsureInitializedAsync();
        Task FetchAsync(TimeSpan minimumFetchInterval);
        Task<bool> ActivateAsync();
        RemoteFetchInfo GetInfo();

        // Chỉ giá trị có Source = RemoteValue (bỏ default/static của vendor).
        IReadOnlyDictionary<string, string> GetRemoteValues();
    }

    internal sealed class FirebaseRemoteConfigApi : IFirebaseRemoteConfigApi
    {
        static FirebaseRemoteConfig Instance => FirebaseRemoteConfig.DefaultInstance;

        public Task EnsureInitializedAsync() => Instance.EnsureInitializedAsync();

        // FetchAsync(cacheExpiration) dùng cacheExpiration làm minimum fetch interval.
        public Task FetchAsync(TimeSpan minimumFetchInterval) => Instance.FetchAsync(minimumFetchInterval);

        public Task<bool> ActivateAsync() => Instance.ActivateAsync();

        public RemoteFetchInfo GetInfo()
        {
            var info = Instance.Info;
            return new RemoteFetchInfo(info.LastFetchStatus, info.LastFetchFailureReason, info.FetchTime, info.ThrottledEndTime);
        }

        public IReadOnlyDictionary<string, string> GetRemoteValues()
        {
            var all = Instance.AllValues;
            var values = new Dictionary<string, string>(all.Count, StringComparer.Ordinal);
            foreach (var pair in all)
            {
                if (pair.Value.Source == ValueSource.RemoteValue) values[pair.Key] = pair.Value.StringValue;
            }
            return values;
        }
    }
}
