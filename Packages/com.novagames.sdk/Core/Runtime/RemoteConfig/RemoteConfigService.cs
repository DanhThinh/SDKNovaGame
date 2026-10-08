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
        readonly Dictionary<string, ConfigKey> _keys = new Dictionary<string, ConfigKey>(StringComparer.Ordinal);
        readonly SdkProperty<RemoteConfigSnapshot> _current;

        Task<SdkResult<RemoteConfigSnapshot>>? _inflight;
        long _version;
        bool _cacheLoaded;
        bool _disposed;

        public RemoteConfigService(
            IRemoteConfigSource? source, IKeyValueStore store, IClock clock, ISdkLogger log,
            RemoteConfigOptions settings, IEnumerable<ConfigKey>? registeredKeys = null)
        {
            _source = source;
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
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

        /// <summary>Load cache, init source, fetch với timeout cấu hình. Thất bại =&gt; vẫn chạy bằng cache/default.</summary>
        public async Task<SdkResult<RemoteConfigSnapshot>> InitializeAsync(CancellationToken ct)
        {
            if (_disposed) return SdkError.Disposed(Op);
            LoadCache();
            if (_source is null)
                return new SdkError(Op + ".no_source", SdkErrorCategory.Configuration, "No remote config source registered", false);

            SdkResult init;
            try
            {
                init = await _source.InitializeAsync(ct);
            }
            catch (Exception e)
            {
                init = SdkError.FromException(Op + ".init", e, _source.Id);
            }
            if (!init.IsSuccess)
            {
                _log.Warning("Remote config source init failed, running on " + _current.Value.Source + ": " + init.Error);
                return init.Error!;
            }

            return await FetchAndActivateAsync(_settings.FetchTimeout, ct);
        }

        public Task<SdkResult<RemoteConfigSnapshot>> FetchAndActivateAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (_disposed) return Task.FromResult<SdkResult<RemoteConfigSnapshot>>(SdkError.Disposed(Op));
            if (_source is null)
                return Task.FromResult<SdkResult<RemoteConfigSnapshot>>(
                    new SdkError(Op + ".no_source", SdkErrorCategory.Configuration, "No remote config source registered", false));

            LoadCache();

            // Single-flight: fetch dùng chung không bị hủy bởi token của caller.
            var task = _inflight;
            if (task is null || task.IsCompleted)
            {
                task = RunFetchAsync(_source, timeout);
                _inflight = task;
            }
            return SdkTasks.WaitAsync(task, Op + ".fetch", ct);
        }

        async Task<SdkResult<RemoteConfigSnapshot>> RunFetchAsync(IRemoteConfigSource source, TimeSpan timeout)
        {
            SdkResult<RemoteConfigFetchResult> fetched;
            try
            {
                fetched = await source.FetchAndActivateAsync(timeout, CancellationToken.None);
            }
            catch (Exception e)
            {
                fetched = SdkError.FromException(Op + ".fetch", e, source.Id);
            }

            if (_disposed) return SdkError.Disposed(Op);
            if (!fetched.TryGetValue(out var result))
            {
                _log.Warning("Fetch failed, keeping " + _current.Value + ": " + fetched.Error);
                return fetched.Error!;
            }

            return SdkResult<RemoteConfigSnapshot>.Ok(Activate(result.Values));
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
