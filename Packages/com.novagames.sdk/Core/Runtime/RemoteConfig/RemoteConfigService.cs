#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Infrastructure;

namespace NovaGames.Mobile.RemoteConfig
{
    /// <summary>
    /// Pipeline: Bundled defaults -&gt; Last-known-good cache -&gt; Fetched -&gt; Validation -&gt; Atomic activation.
    /// Mỗi lượt init + fetch chạy nền trong WorkTimeout; caller chỉ chờ tới timeout của mình, kết quả về muộn vẫn được
    /// activate (Current đổi, Updated bắn). Có main + scheduler thì lỗi có thể thử lại được tự retry theo RetryDelays.
    /// Gọi trên main thread.
    /// </summary>
    public sealed class RemoteConfigService : IRemoteConfigService, IDisposable
    {
        const string Op = "remote_config";

        readonly IRemoteConfigSource? _source;
        readonly IKeyValueStore _store;
        readonly IClock _clock;
        readonly ISdkLogger _log;
        readonly RemoteConfigOptions _settings;
        readonly IMainThreadDispatcher? _main;
        readonly IScheduler? _scheduler;
        readonly Dictionary<string, ConfigKey> _keys = new Dictionary<string, ConfigKey>(StringComparer.Ordinal);
        readonly SdkProperty<RemoteConfigSnapshot> _current;

        Task<SdkResult<RemoteConfigSnapshot>>? _inflight;
        IDisposable? _retryTimer;
        int _retryAttempt;
        long _version;
        bool _cacheLoaded;
        bool _disposed;

        /// <param name="main">Cùng với scheduler: caller chờ có timeout và tự retry. null = chờ tới khi lượt fetch xong, không retry.</param>
        public RemoteConfigService(
            IRemoteConfigSource? source, IKeyValueStore store, IClock clock, ISdkLogger log,
            RemoteConfigOptions settings, IEnumerable<ConfigKey>? registeredKeys = null,
            IMainThreadDispatcher? main = null, IScheduler? scheduler = null)
        {
            _source = source;
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _main = main;
            _scheduler = scheduler;
            _current = new SdkProperty<RemoteConfigSnapshot>(RemoteConfigSnapshot.Empty, log, ReferenceEqualityComparer.Instance);

            if (registeredKeys != null)
            {
                foreach (var key in registeredKeys)
                {
                    if (_keys.ContainsKey(key.Name)) throw new ArgumentException("Duplicate config key: " + key.Name, nameof(registeredKeys));
                    _keys.Add(key.Name, key);
                }
            }
        }

        public ISdkProperty<RemoteConfigSnapshot> Current => _current;

        /// <summary>Đồng bộ: nạp last-known-good đã lưu. Idempotent.</summary>
        public void LoadCache()
        {
            if (_cacheLoaded || _disposed) return;
            _cacheLoaded = true;
            if (_current.Value.Source != ConfigSource.Default) return;
            if (!RemoteConfigCache.TryLoad(_store, _log, out var values, out var activatedUtc)) return;

            DropInvalidCachedValues(values);
            var snapshot = new RemoteConfigSnapshot(++_version, ConfigSource.Cache, activatedUtc, values);
            _current.Set(snapshot);
            _log.Info("Loaded " + snapshot);
        }

        /// <summary>
        /// Load cache, init source, fetch. Chờ tối đa InitTimeout + FetchTimeout; hết giờ thì vẫn chạy bằng cache/default,
        /// lượt fetch tiếp tục chạy nền và activate khi xong.
        /// </summary>
        public Task<SdkResult<RemoteConfigSnapshot>> InitializeAsync(CancellationToken ct) =>
            Start(_settings.InitTimeout + _settings.FetchTimeout, ct);

        /// <summary>Fetch lại; chờ tối đa `timeout`, lượt fetch tiếp tục chạy nền sau đó.</summary>
        public Task<SdkResult<RemoteConfigSnapshot>> FetchAndActivateAsync(TimeSpan timeout, CancellationToken ct) => Start(timeout, ct);

        /// <summary>Chưa có giá trị remote (vd. lần khởi động offline) thì fetch nền, không ai chờ. NovaSdk gọi khi app quay lại.</summary>
        public void RefreshIfNotRemote()
        {
            if (_disposed || _source is null || _current.Value.Source == ConfigSource.Remote) return;
            _retryAttempt = 0;
            StartRound();
        }

        Task<SdkResult<RemoteConfigSnapshot>> Start(TimeSpan timeout, CancellationToken ct)
        {
            if (_disposed) return Task.FromResult<SdkResult<RemoteConfigSnapshot>>(SdkError.Disposed(Op));
            LoadCache();
            if (_source is null)
                return Task.FromResult<SdkResult<RemoteConfigSnapshot>>(
                    new SdkError(Op + ".no_source", SdkErrorCategory.Configuration, "No remote config source registered", false));

            var round = StartRound();
            if (round.IsCompleted || _main is null || _scheduler is null) return SdkTasks.WaitAsync(round, Op + ".fetch", ct);

            // Chỉ giới hạn việc chờ của caller; kết quả giao trên main thread.
            var wait = new VendorOperation<RemoteConfigSnapshot>(Op + ".fetch", _main, _scheduler, timeout, ct);
            round.ContinueWith(t => wait.Complete(t.IsFaulted ? SdkError.FromException(Op + ".fetch", t.Exception!.GetBaseException()) : t.Result),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return wait.Task;
        }

        // Single-flight: một lượt init + fetch dùng chung, không bị hủy bởi caller.
        Task<SdkResult<RemoteConfigSnapshot>> StartRound()
        {
            var task = _inflight;
            if (task is null || task.IsCompleted)
            {
                _retryTimer?.Dispose();
                _retryTimer = null;
                task = RunRoundAsync(_source!);
                _inflight = task;
            }
            return task;
        }

        async Task<SdkResult<RemoteConfigSnapshot>> RunRoundAsync(IRemoteConfigSource source)
        {
            SdkResult init;
            try
            {
                init = await source.InitializeAsync(CancellationToken.None);
            }
            catch (Exception e)
            {
                init = SdkError.FromException(Op + ".init", e, source.Id);
            }
            if (_disposed) return SdkError.Disposed(Op);
            if (!init.IsSuccess)
            {
                _log.Warning("Remote config source init failed, running on " + _current.Value.Source + ": " + init.Error);
                ScheduleRetry(init.Error!);
                return init.Error!;
            }

            SdkResult<RemoteConfigFetchResult> fetched;
            try
            {
                fetched = await source.FetchAndActivateAsync(_settings.WorkTimeout, CancellationToken.None);
            }
            catch (Exception e)
            {
                fetched = SdkError.FromException(Op + ".fetch", e, source.Id);
            }

            if (_disposed) return SdkError.Disposed(Op);
            if (!fetched.TryGetValue(out var result))
            {
                _log.Warning("Fetch failed, keeping " + _current.Value + ": " + fetched.Error);
                ScheduleRetry(fetched.Error!);
                return fetched.Error!;
            }

            _retryAttempt = 0;
            return SdkResult<RemoteConfigSnapshot>.Ok(Activate(result.Values));
        }

        void ScheduleRetry(SdkError error)
        {
            if (_scheduler is null || _disposed || !error.IsRetryable) return;
            var delays = _settings.RetryDelays;
            if (_retryAttempt >= delays.Count) return;
            var delay = delays[_retryAttempt++];
            _log.Info("Retrying remote config in " + delay.TotalSeconds + " s");
            _retryTimer?.Dispose();
            _retryTimer = _scheduler.Schedule(delay, () =>
            {
                _retryTimer = null;
                if (!_disposed) StartRound();
            });
        }

        RemoteConfigSnapshot Activate(IReadOnlyDictionary<string, string> fetched)
        {
            var previous = _current.Value;
            var merged = new Dictionary<string, string>(fetched.Count, StringComparer.Ordinal);

            foreach (var pair in fetched)
            {
                if (pair.Key is null || pair.Value is null) continue;
                if (_keys.TryGetValue(pair.Key, out var key) && !key.IsRawValid(pair.Value))
                {
                    // Giá trị invalid không ghi đè last-known-good.
                    if (previous.TryGetRaw(pair.Key, out var lastGood) && key.IsRawValid(lastGood))
                    {
                        merged[pair.Key] = lastGood;
                        _log.Warning($"Invalid remote value for '{pair.Key}', keeping last-known-good");
                    }
                    else
                    {
                        _log.Warning($"Invalid remote value for '{pair.Key}', using default");
                    }
                    continue;
                }
                merged[pair.Key] = pair.Value;
            }

            if (previous.Source == ConfigSource.Remote && previous.HasSameValues(merged))
            {
                _log.Debug("Fetched config unchanged");
                return previous;
            }

            var snapshot = new RemoteConfigSnapshot(++_version, ConfigSource.Remote, _clock.UtcNow, merged);
            RemoteConfigCache.Save(_store, _log, snapshot);
            _current.Set(snapshot);
            _log.Info("Activated " + snapshot);
            return snapshot;
        }

        void DropInvalidCachedValues(Dictionary<string, string> values)
        {
            List<string>? invalid = null;
            foreach (var pair in values)
            {
                if (_keys.TryGetValue(pair.Key, out var key) && !key.IsRawValid(pair.Value))
                    (invalid ??= new List<string>()).Add(pair.Key);
            }
            if (invalid is null) return;
            foreach (var name in invalid)
            {
                values.Remove(name);
                _log.Warning($"Invalid cached value for '{name}', using default");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _retryTimer?.Dispose();
            _retryTimer = null;
            _current.ClearSubscribers();
        }

        sealed class ReferenceEqualityComparer : IEqualityComparer<RemoteConfigSnapshot>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public bool Equals(RemoteConfigSnapshot? x, RemoteConfigSnapshot? y) => ReferenceEquals(x, y);
            public int GetHashCode(RemoteConfigSnapshot obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
