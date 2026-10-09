#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace NovaGames.Mobile.RemoteConfig
{
    public enum ConfigSource : byte { Default, Cache, Remote }

    /// <summary>Snapshot immutable; swap atomic sau validation.</summary>
    public sealed class RemoteConfigSnapshot : IConfigValues
    {
        static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>(0);

        public static RemoteConfigSnapshot Empty { get; } =
            new RemoteConfigSnapshot(0, ConfigSource.Default, DateTime.MinValue, NoValues, copy: false);

        readonly IReadOnlyDictionary<string, string> _values;

        public RemoteConfigSnapshot(long version, ConfigSource source, DateTime activatedUtc, IReadOnlyDictionary<string, string> values)
            : this(version, source, activatedUtc, values, copy: true) { }

        RemoteConfigSnapshot(long version, ConfigSource source, DateTime activatedUtc, IReadOnlyDictionary<string, string> values, bool copy)
        {
            Version = version;
            Source = source;
            ActivatedUtc = activatedUtc;
            _values = copy ? Copy(values) : values;
        }

        public long Version { get; }
        public ConfigSource Source { get; }
        public DateTime ActivatedUtc { get; }
        public IReadOnlyDictionary<string, string> Values => _values;

        public bool TryGetRaw(string key, [NotNullWhen(true)] out string? raw)
        {
            if (_values.TryGetValue(key, out var value))
            {
                raw = value;
                return true;
            }
            raw = null;
            return false;
        }

        public bool HasSameValues(IReadOnlyDictionary<string, string> other)
        {
            if (other.Count != _values.Count) return false;
            foreach (var pair in other)
            {
                if (!_values.TryGetValue(pair.Key, out var value) || !string.Equals(value, pair.Value, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        public override string ToString() => $"RemoteConfigSnapshot(v{Version}, {Source}, {_values.Count} keys)";

        static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string> values)
        {
            var copy = new Dictionary<string, string>(values.Count, StringComparer.Ordinal);
            foreach (var pair in values) copy[pair.Key] = pair.Value;
            return copy;
        }
    }

    public interface IRemoteConfigService
    {
        ISdkProperty<RemoteConfigSnapshot> Current { get; }
        Task<SdkResult<RemoteConfigSnapshot>> FetchAndActivateAsync(TimeSpan timeout, CancellationToken ct);
    }

    public static class RemoteConfigExtensions
    {
        public static T Get<T>(this IRemoteConfigService service, ConfigKey<T> key) => key.Read(service.Current.Value);
    }

    public sealed class RemoteConfigFetchResult
    {
        public RemoteConfigFetchResult(IReadOnlyDictionary<string, string> values, bool activatedNewValues, DateTime? fetchTimeUtc)
        {
            Values = values ?? throw new ArgumentNullException(nameof(values));
            ActivatedNewValues = activatedNewValues;
            FetchTimeUtc = fetchTimeUtc;
        }

        /// <summary>Toàn bộ giá trị remote đang active (không gồm default của vendor).</summary>
        public IReadOnlyDictionary<string, string> Values { get; }
        public bool ActivatedNewValues { get; }
        public DateTime? FetchTimeUtc { get; }
    }

    /// <summary>SPI cho adapter (Firebase, ...). Adapter không giữ default/cache; việc đó thuộc RemoteConfigService.</summary>
    public interface IRemoteConfigSource : IDisposable
    {
        string Id { get; }

        /// <summary>Idempotent. Token chỉ hủy việc chờ của caller.</summary>
        Task<SdkResult> InitializeAsync(CancellationToken ct);

        /// <summary>Fetch + activate trong `timeout`; tự init nếu chưa init.</summary>
        Task<SdkResult<RemoteConfigFetchResult>> FetchAndActivateAsync(TimeSpan timeout, CancellationToken ct);
    }

    public static class RemoteConfigSourceIds
    {
        public const string Firebase = "firebase";
    }

    public sealed record RemoteConfigOptions(
        TimeSpan InitTimeout,
        TimeSpan FetchTimeout,
        TimeSpan MinimumFetchInterval)
    {
        /// <summary>
        /// Ngân sách của một lượt init + fetch + activate chạy nền. Caller (lúc khởi động: InitTimeout + FetchTimeout; game
        /// gọi FetchAsync: FetchTimeout) chỉ chờ phần đầu; kết quả về muộn trong ngân sách này vẫn được activate.
        /// </summary>
        public TimeSpan WorkTimeout { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>Chờ trước mỗi lần tự thử lại khi init/fetch lỗi có thể thử lại (timeout, mạng, Play services đang cập nhật).</summary>
        public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
            new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2) };

        /// <summary>Caller chờ init tối đa 5 s, fetch tối đa 3 s; minimum fetch interval 12 giờ = default của Firebase.</summary>
        public static RemoteConfigOptions Default { get; } =
            new RemoteConfigOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3), TimeSpan.FromHours(12));

        public static RemoteConfigOptions Development { get; } =
            Default with { MinimumFetchInterval = TimeSpan.Zero };
    }
}
