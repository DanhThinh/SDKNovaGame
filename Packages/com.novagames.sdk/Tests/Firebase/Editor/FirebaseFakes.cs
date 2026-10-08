#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Firebase;
using Firebase.Analytics;
using Firebase.RemoteConfig;
using NovaGames.Mobile.Tracking;

namespace NovaGames.Mobile.Firebase.Tests
{
    sealed class FakeFirebaseAppApi : IFirebaseAppApi
    {
        public Task<DependencyStatus> Result { get; set; } = Task.FromResult(DependencyStatus.Available);
        public int Calls { get; private set; }

        public Task<DependencyStatus> CheckAndFixDependenciesAsync()
        {
            Calls++;
            return Result;
        }
    }

    sealed class FakeAnalyticsApi : IFirebaseAnalyticsApi
    {
        public readonly List<(string Name, TrackingParam[] Params)> Events = new List<(string, TrackingParam[])>();
        public readonly List<Dictionary<ConsentType, ConsentStatus>> Consents = new List<Dictionary<ConsentType, ConsentStatus>>();
        public readonly List<(string Name, string? Value)> UserProperties = new List<(string, string?)>();
        public readonly List<string?> UserIds = new List<string?>();
        public readonly List<string> AppleTransactions = new List<string>();
        public TimeSpan? SessionTimeout;
        public bool? CollectionEnabled;
        public bool ThrowOnLog;

        public void LogEvent(string name, IReadOnlyList<TrackingParam> parameters)
        {
            if (ThrowOnLog) throw new InvalidOperationException("native failure");
            Events.Add((name, parameters.ToArray()));
        }

        public void SetConsent(IDictionary<ConsentType, ConsentStatus> consent) =>
            Consents.Add(new Dictionary<ConsentType, ConsentStatus>(consent));

        public void SetUserProperty(string name, string? value) => UserProperties.Add((name, value));
        public void SetUserId(string? id) => UserIds.Add(id);
        public void SetSessionTimeout(TimeSpan timeout) => SessionTimeout = timeout;
        public void SetCollectionEnabled(bool enabled) => CollectionEnabled = enabled;

        public Task LogAppleTransactionAsync(string transactionId)
        {
            AppleTransactions.Add(transactionId);
            return Task.CompletedTask;
        }
    }

    sealed class FakeRemoteConfigApi : IFirebaseRemoteConfigApi
    {
        public Task Ensure { get; set; } = Task.CompletedTask;
        public Func<Task> Fetch { get; set; } = () => Task.CompletedTask;
        public Task<bool> Activate { get; set; } = Task.FromResult(true);
        public RemoteFetchInfo Info { get; set; } =
            new RemoteFetchInfo(LastFetchStatus.Success, FetchFailureReason.Invalid, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), DateTime.MinValue);
        public Dictionary<string, string> Values { get; } = new Dictionary<string, string>();
        public TimeSpan? LastMinimumFetchInterval { get; private set; }
        public int FetchCalls { get; private set; }

        public Task EnsureInitializedAsync() => Ensure;

        public Task FetchAsync(TimeSpan minimumFetchInterval)
        {
            FetchCalls++;
            LastMinimumFetchInterval = minimumFetchInterval;
            return Fetch();
        }

        public Task<bool> ActivateAsync() => Activate;
        public RemoteFetchInfo GetInfo() => Info;
        public IReadOnlyDictionary<string, string> GetRemoteValues() => new Dictionary<string, string>(Values);
    }
}
