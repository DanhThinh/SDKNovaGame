#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.RemoteConfig;
using NovaGames.Mobile.Testing;
using NUnit.Framework;

namespace NovaGames.Mobile.Tests
{
    public sealed class RemoteConfigServiceTests
    {
        static readonly IntKey Capping = new IntKey("inter_capping", 30, min: 0, max: 300);
        static readonly BoolKey InterEnabled = new BoolKey("inter_enabled", true);

        TestHarness _h = null!;
        FakeSource _source = null!;

        [SetUp]
        public void SetUp()
        {
            _h = new TestHarness();
            _source = new FakeSource();
        }

        [TearDown] public void TearDown() => _h.Dispose();

        RemoteConfigService CreateService() =>
            new RemoteConfigService(_source, _h.Store, _h.Clock, _h.Log, RemoteConfigOptions.Default,
                new ConfigKey[] { Capping, InterEnabled });

        [Test]
        public void Initialize_FetchSucceeds_ActivatesRemoteAndPersistsLastKnownGood()
        {
            _source.Enqueue(("inter_capping", "45"), ("inter_enabled", "false"));
            var service = CreateService();

            var result = _h.Run(service.InitializeAsync(CancellationToken.None));

            Assert.IsTrue(result.IsSuccess, result.ToString());
            Assert.AreEqual(ConfigSource.Remote, service.Current.Value.Source);
            Assert.AreEqual(45, service.Get(Capping));
            Assert.IsFalse(service.Get(InterEnabled));
            Assert.IsTrue(_h.Store.Values.ContainsKey(StorageKeys.RemoteConfigLastGood));
        }

        [Test]
        public void Initialize_FetchFails_RunsOnCache()
        {
            _source.Enqueue(("inter_capping", "45"));
            _h.Run(CreateService().InitializeAsync(CancellationToken.None));

            // Lần chạy sau: offline.
            _source.EnqueueError(new SdkError("fetch", SdkErrorCategory.Network, "offline", true));
            var service = CreateService();
            var result = _h.Run(service.InitializeAsync(CancellationToken.None));

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(ConfigSource.Cache, service.Current.Value.Source);
            Assert.AreEqual(45, service.Get(Capping));
        }

        [Test]
        public void Fetch_NoCacheAndFailure_UsesDefaults()
        {
            _source.EnqueueError(SdkError.Timeout("fetch"));
            var service = CreateService();

            _h.Run(service.InitializeAsync(CancellationToken.None));

            Assert.AreEqual(ConfigSource.Default, service.Current.Value.Source);
            Assert.AreEqual(30, service.Get(Capping));
            Assert.IsTrue(service.Get(InterEnabled));
        }

        [Test]
        public void InvalidRemoteValue_DoesNotOverrideLastKnownGood()
        {
            var service = CreateService();
            _source.Enqueue(("inter_capping", "45"));
            _h.Run(service.FetchAndActivateAsync(TimeSpan.FromSeconds(3), CancellationToken.None));

            _source.Enqueue(("inter_capping", "9999"), ("new_key", "x"));
            _h.Run(service.FetchAndActivateAsync(TimeSpan.FromSeconds(3), CancellationToken.None));

            Assert.AreEqual(45, service.Get(Capping));
            Assert.IsTrue(service.Current.Value.TryGetRaw("new_key", out _));
        }

        [Test]
        public void UnchangedFetch_DoesNotRaiseChange()
        {
            var service = CreateService();
            int changes = 0;
            service.Current.Subscribe(_ => changes++, emitCurrent: false);

            _source.Enqueue(("inter_capping", "45"));
            _h.Run(service.FetchAndActivateAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
            _source.Enqueue(("inter_capping", "45"));
            _h.Run(service.FetchAndActivateAsync(TimeSpan.FromSeconds(3), CancellationToken.None));

            Assert.AreEqual(1, changes);
        }

        [Test]
        public void ConcurrentFetches_ShareOneSourceCall()
        {
            var pending = new TaskCompletionSource<SdkResult<RemoteConfigFetchResult>>();
            _source.Pending = pending;
            var service = CreateService();

            var first = service.FetchAndActivateAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            var second = service.FetchAndActivateAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            pending.SetResult(SdkResult<RemoteConfigFetchResult>.Ok(
                new RemoteConfigFetchResult(new Dictionary<string, string> { ["inter_capping"] = "60" }, true, null)));

            Assert.IsTrue(_h.Run(first).IsSuccess);
            Assert.IsTrue(_h.Run(second).IsSuccess);
            Assert.AreEqual(1, _source.FetchCount);
            Assert.AreEqual(60, service.Get(Capping));
        }

        [Test]
        public void CallerCancellation_DoesNotCancelSharedFetch()
        {
            var pending = new TaskCompletionSource<SdkResult<RemoteConfigFetchResult>>();
            _source.Pending = pending;
            var service = CreateService();
            using var cts = new CancellationTokenSource();

            var cancelled = service.FetchAndActivateAsync(TimeSpan.FromSeconds(3), cts.Token);
            cts.Cancel();
            Assert.AreEqual(SdkErrorCategory.Cancelled, _h.Run(cancelled).Error!.Category);

            pending.SetResult(SdkResult<RemoteConfigFetchResult>.Ok(
                new RemoteConfigFetchResult(new Dictionary<string, string> { ["inter_capping"] = "60" }, true, null)));
            _h.Main.Drain();

            Assert.AreEqual(60, service.Get(Capping));
        }

        [Test]
        public void CorruptCache_IsDiscarded()
        {
            _h.Store.SetString(StorageKeys.RemoteConfigLastGood, "{ not json");
            var service = CreateService();

            service.LoadCache();

            Assert.AreEqual(ConfigSource.Default, service.Current.Value.Source);
            Assert.IsFalse(_h.Store.Values.ContainsKey(StorageKeys.RemoteConfigLastGood));
        }

        [Test]
        public void PersistFailure_StillActivates()
        {
            _h.Store.FailWrites = true;
            _source.Enqueue(("inter_capping", "45"));
            var service = CreateService();

            var result = _h.Run(service.FetchAndActivateAsync(TimeSpan.FromSeconds(3), CancellationToken.None));

            Assert.IsTrue(result.IsSuccess);
            Assert.AreEqual(45, service.Get(Capping));
            Assert.Greater(_h.Log.Count(SdkLogLevel.Error), 0);
        }

        // ---------------- Fetch nền, retry, refresh ----------------

        RemoteConfigService CreateTimedService() =>
            new RemoteConfigService(_source, _h.Store, _h.Clock, _h.Log, RemoteConfigOptions.Default,
                new ConfigKey[] { Capping, InterEnabled }, _h.Main, _h.Scheduler);

        static SdkResult<RemoteConfigFetchResult> Values(string capping) => SdkResult<RemoteConfigFetchResult>.Ok(
            new RemoteConfigFetchResult(new Dictionary<string, string> { ["inter_capping"] = capping }, true, null));

        [Test]
        public void SlowFetch_CallerTimesOut_LateResultIsStillActivated()
        {
            var pending = new TaskCompletionSource<SdkResult<RemoteConfigFetchResult>>();
            _source.Pending = pending;
            var service = CreateTimedService();
            int changes = 0;
            service.Current.Subscribe(_ => changes++, emitCurrent: false);

            var init = service.InitializeAsync(CancellationToken.None);
            _h.Scheduler.Advance(RemoteConfigOptions.Default.InitTimeout + RemoteConfigOptions.Default.FetchTimeout);
            var result = _h.Run(init);

            Assert.AreEqual(SdkErrorCategory.Timeout, result.Error!.Category, "Ready does not wait past the startup budget");
            Assert.AreEqual(ConfigSource.Default, service.Current.Value.Source);

            pending.SetResult(Values("60"));
            _h.Main.Drain();

            Assert.AreEqual(ConfigSource.Remote, service.Current.Value.Source, "late fetch is activated, not dropped");
            Assert.AreEqual(60, service.Get(Capping));
            Assert.AreEqual(1, changes);
            Assert.IsTrue(_h.Store.Values.ContainsKey(StorageKeys.RemoteConfigLastGood));
        }

        [Test]
        public void SlowFetch_SourceGetsWorkBudgetNotCallerTimeout()
        {
            _source.Pending = new TaskCompletionSource<SdkResult<RemoteConfigFetchResult>>();
            var service = CreateTimedService();

            service.FetchAndActivateAsync(TimeSpan.FromSeconds(3), CancellationToken.None);

            Assert.AreEqual(RemoteConfigOptions.Default.WorkTimeout, _source.LastTimeout);
        }

        [Test]
        public void RetryableFailure_RetriesInBackgroundAndActivates()
        {
            _source.EnqueueError(new SdkError("fetch", SdkErrorCategory.Network, "offline", true));
            _source.Enqueue(("inter_capping", "45"));
            var service = CreateTimedService();

            Assert.IsFalse(_h.Run(service.InitializeAsync(CancellationToken.None)).IsSuccess);
            Assert.AreEqual(1, _source.FetchCount);

            _h.Scheduler.Advance(RemoteConfigOptions.Default.RetryDelays[0]);
            _h.Main.Drain();

            Assert.AreEqual(2, _source.FetchCount);
            Assert.AreEqual(ConfigSource.Remote, service.Current.Value.Source);
            Assert.AreEqual(45, service.Get(Capping));
        }

        [Test]
        public void SourceInitFailure_IsRetriedToo()
        {
            _source.InitResults.Enqueue(SdkError.Timeout("init"));
            _source.Enqueue(("inter_capping", "45"));
            var service = CreateTimedService();

            Assert.IsFalse(_h.Run(service.InitializeAsync(CancellationToken.None)).IsSuccess);
            Assert.AreEqual(0, _source.FetchCount, "no fetch while the source is not initialized");

            _h.Scheduler.Advance(RemoteConfigOptions.Default.RetryDelays[0]);
            _h.Main.Drain();

            Assert.AreEqual(45, service.Get(Capping));
        }

        [Test]
        public void NonRetryableFailure_IsNotRetried_AndRetriesAreBounded()
        {
            _source.EnqueueError(new SdkError("throttled", SdkErrorCategory.Unavailable, "throttled", false));
            var service = CreateTimedService();
            _h.Run(service.InitializeAsync(CancellationToken.None));
            Assert.AreEqual(0, _h.Scheduler.PendingCount);

            var retrying = CreateTimedService();
            foreach (var _ in RemoteConfigOptions.Default.RetryDelays) _source.EnqueueError(SdkError.Timeout("fetch"));
            _source.EnqueueError(SdkError.Timeout("fetch"));
            _h.Run(retrying.FetchAndActivateAsync(TimeSpan.FromSeconds(3), CancellationToken.None));
            foreach (var delay in RemoteConfigOptions.Default.RetryDelays)
            {
                _h.Scheduler.Advance(delay);
                _h.Main.Drain();
            }

            Assert.AreEqual(2 + RemoteConfigOptions.Default.RetryDelays.Count, _source.FetchCount);
            Assert.AreEqual(0, _h.Scheduler.PendingCount, "gives up after the last retry delay");
        }

        [Test]
        public void RefreshIfNotRemote_FetchesOnlyWhenNotOnRemoteValues()
        {
            _source.EnqueueError(new SdkError("throttled", SdkErrorCategory.Unavailable, "throttled", false));
            _source.Enqueue(("inter_capping", "45"));
            var service = CreateTimedService();
            _h.Run(service.InitializeAsync(CancellationToken.None));

            service.RefreshIfNotRemote();
            _h.Main.Drain();
            Assert.AreEqual(45, service.Get(Capping));
            Assert.AreEqual(2, _source.FetchCount);

            service.RefreshIfNotRemote();
            Assert.AreEqual(2, _source.FetchCount, "already on remote values");
        }

        sealed class FakeSource : IRemoteConfigSource
        {
            readonly Queue<SdkResult<RemoteConfigFetchResult>> _results = new Queue<SdkResult<RemoteConfigFetchResult>>();

            public TaskCompletionSource<SdkResult<RemoteConfigFetchResult>>? Pending { get; set; }
            public int FetchCount { get; private set; }
            public string Id => "fake";

            public void Enqueue(params (string Key, string Value)[] values)
            {
                var dict = new Dictionary<string, string>();
                foreach (var (key, value) in values) dict[key] = value;
                _results.Enqueue(SdkResult<RemoteConfigFetchResult>.Ok(new RemoteConfigFetchResult(dict, true, null)));
            }

            public void EnqueueError(SdkError error) => _results.Enqueue(error);

            public Queue<SdkResult> InitResults { get; } = new Queue<SdkResult>();
            public TimeSpan LastTimeout { get; private set; }

            public Task<SdkResult> InitializeAsync(CancellationToken ct) =>
                Task.FromResult(InitResults.Count > 0 ? InitResults.Dequeue() : SdkResult.Ok);

            public Task<SdkResult<RemoteConfigFetchResult>> FetchAndActivateAsync(TimeSpan timeout, CancellationToken ct)
            {
                FetchCount++;
                LastTimeout = timeout;
                return Pending?.Task ?? Task.FromResult(_results.Dequeue());
            }

            public void Dispose() { }
        }
    }
}
