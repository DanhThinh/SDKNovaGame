#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Firebase.RemoteConfig;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.RemoteConfig;
using NovaGames.Mobile.Testing;
using NUnit.Framework;

namespace NovaGames.Mobile.Firebase.Tests
{
    public sealed class FirebaseRemoteConfigSourceTests
    {
        TestHarness _h = null!;
        FakeFirebaseAppApi _appApi = null!;
        FakeRemoteConfigApi _api = null!;

        [SetUp]
        public void SetUp()
        {
            _h = new TestHarness();
            _appApi = new FakeFirebaseAppApi();
            _api = new FakeRemoteConfigApi();
        }

        [TearDown] public void TearDown() => _h.Dispose();

        FirebaseRemoteConfigSource CreateSource() =>
            new FirebaseRemoteConfigSource(
                new FirebaseAppInitializer(_appApi, _h.Main, _h.Scheduler, _h.Log),
                _api, _h.Main, _h.Scheduler, _h.Clock, _h.Log, RemoteConfigOptions.Development);

        [Test]
        public void Fetch_Success_ReturnsOnlyRemoteValues()
        {
            _api.Values["inter_capping"] = "45";
            var source = CreateSource();

            var result = _h.Run(source.FetchAndActivateAsync(TimeSpan.FromSeconds(3), CancellationToken.None));

            Assert.IsTrue(result.TryGetValue(out var fetched), result.ToString());
            Assert.AreEqual("45", fetched!.Values["inter_capping"]);
            Assert.IsTrue(fetched.ActivatedNewValues);
            Assert.AreEqual(TimeSpan.Zero, _api.LastMinimumFetchInterval, "development settings fetch without throttle interval");
            Assert.IsTrue(_h.Log.Entries.Any(entry =>
                entry.Level == SdkLogLevel.Debug &&
                entry.Message == "Remote config 'inter_capping' = '45'"));
        }

        [Test]
        public void Fetch_Throttled_MapsToThrottledError()
        {
            _api.Fetch = () => Task.FromException(new Exception("throttled"));
            _api.Info = new RemoteFetchInfo(LastFetchStatus.Failure, FetchFailureReason.Throttled, DateTime.MinValue, DateTime.UtcNow.AddHours(1));
            var source = CreateSource();

            var result = _h.Run(source.FetchAndActivateAsync(TimeSpan.FromSeconds(3), CancellationToken.None));

            StringAssert.EndsWith(".throttled", result.Error!.Code);
            Assert.IsFalse(result.Error.IsRetryable);
        }

        [Test]
        public void Fetch_CompletesButInfoReportsFailure_IsNetworkError()
        {
            _api.Info = new RemoteFetchInfo(LastFetchStatus.Failure, FetchFailureReason.Error, DateTime.MinValue, DateTime.MinValue);
            var source = CreateSource();

            var result = _h.Run(source.FetchAndActivateAsync(TimeSpan.FromSeconds(3), CancellationToken.None));

            Assert.AreEqual(SdkErrorCategory.Network, result.Error!.Category);
            Assert.IsTrue(result.Error.IsRetryable);
        }

        [Test]
        public void Fetch_Hangs_TimesOutWithinDeadline()
        {
            var hanging = new TaskCompletionSource<bool>();
            _api.Fetch = () => hanging.Task;
            var source = CreateSource();

            var task = source.FetchAndActivateAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            _h.Main.Drain();
            Assert.AreEqual(1, _api.FetchCalls);
            _h.Scheduler.Advance(TimeSpan.FromSeconds(3));
            var result = _h.Run(task);

            Assert.AreEqual(SdkErrorCategory.Timeout, result.Error!.Category);
        }

        [Test]
        public void Fetch_FirebaseUnavailable_DoesNotTouchRemoteConfig()
        {
            _appApi.Result = Task.FromResult(global::Firebase.DependencyStatus.UnavailableInvalid);
            var source = CreateSource();

            var result = _h.Run(source.FetchAndActivateAsync(TimeSpan.FromSeconds(3), CancellationToken.None));

            Assert.AreEqual(SdkErrorCategory.Unavailable, result.Error!.Category);
            Assert.AreEqual(0, _api.FetchCalls);
        }

        [Test]
        public void Initialize_RetriesAfterFailure()
        {
            _appApi.Result = Task.FromResult(global::Firebase.DependencyStatus.UnavailableUpdating);
            var source = CreateSource();
            Assert.IsFalse(_h.Run(source.InitializeAsync(CancellationToken.None)).IsSuccess);

            _appApi.Result = Task.FromResult(global::Firebase.DependencyStatus.Available);
            Assert.IsTrue(_h.Run(source.InitializeAsync(CancellationToken.None)).IsSuccess);
            Assert.AreEqual(2, _appApi.Calls);
        }

        [Test]
        public void EndToEnd_WithRemoteConfigService()
        {
            _api.Values["inter_enabled"] = "false";
            var key = new BoolKey("inter_enabled", true);
            var service = new RemoteConfigService(CreateSource(), _h.Store, _h.Clock, _h.Log,
                RemoteConfigOptions.Development, new ConfigKey[] { key });

            var result = _h.Run(service.InitializeAsync(CancellationToken.None));

            Assert.IsTrue(result.IsSuccess, result.ToString());
            Assert.IsFalse(service.Get(key));
            Assert.AreEqual(ConfigSource.Remote, service.Current.Value.Source);
        }
    }
}
