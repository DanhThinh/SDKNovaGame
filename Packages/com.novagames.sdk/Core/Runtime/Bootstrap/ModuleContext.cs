#nullable enable
using System;
using System.Collections.Generic;
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.RemoteConfig;
using NovaGames.Mobile.Tracking;

namespace NovaGames.Mobile.Bootstrap
{
    /// <summary>Cấu hình runtime của SDK: môi trường, mức log, Remote Config, Analytics, Ads, cấu hình riêng của tracking sink.</summary>
    public sealed record RuntimeSdkSettings(
        bool IsDevelopment,
        SdkLogLevel LogLevel,
        RemoteConfigOptions RemoteConfig,
        AnalyticsOptions Analytics)
    {
        /// <summary>Mặc định tắt Ads.</summary>
        public AdsOptions Ads { get; init; } = AdsOptions.Disabled;

        /// <summary>Cấu hình riêng theo sink id (TrackingSinkIds). Sink không có entry tự báo lỗi Configuration khi init.</summary>
        public IReadOnlyDictionary<string, ITrackingSinkSettings> SinkSettings { get; init; } = NoSinkSettings;

        static readonly IReadOnlyDictionary<string, ITrackingSinkSettings> NoSinkSettings =
            new Dictionary<string, ITrackingSinkSettings>(StringComparer.Ordinal);

        public T? GetSinkSettings<T>(string sinkId) where T : class, ITrackingSinkSettings =>
            SinkSettings.TryGetValue(sinkId, out var settings) ? settings as T : null;

        public RuntimeSdkSettings WithSinkSettings(string sinkId, ITrackingSinkSettings settings)
        {
            if (string.IsNullOrEmpty(sinkId)) throw new ArgumentException("Sink id is required.", nameof(sinkId));
            if (settings is null) throw new ArgumentNullException(nameof(settings));
            var copy = new Dictionary<string, ITrackingSinkSettings>(StringComparer.Ordinal);
            foreach (var pair in SinkSettings) copy[pair.Key] = pair.Value;
            copy[sinkId] = settings;
            return this with { SinkSettings = copy };
        }

        public static RuntimeSdkSettings Production { get; } = new RuntimeSdkSettings(
            false, SdkLogLevel.Warning, RemoteConfigOptions.Default, AnalyticsOptions.Default);

        public static RuntimeSdkSettings Development { get; } = new RuntimeSdkSettings(
            true, SdkLogLevel.Debug, RemoteConfigOptions.Development, AnalyticsOptions.Default);
    }

    public sealed class ModuleContext
    {
        public ModuleContext(
            RuntimeSdkSettings settings, IMainThreadDispatcher main, IClock clock,
            IScheduler scheduler, IKeyValueStore store, ISdkLoggerFactory logs)
        {
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Main = main ?? throw new ArgumentNullException(nameof(main));
            Clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            Store = store ?? throw new ArgumentNullException(nameof(store));
            Logs = logs ?? throw new ArgumentNullException(nameof(logs));
        }

        public RuntimeSdkSettings Settings { get; }
        public IMainThreadDispatcher Main { get; }
        public IClock Clock { get; }
        public IScheduler Scheduler { get; }
        public IKeyValueStore Store { get; }
        public ISdkLoggerFactory Logs { get; }

        /// <summary>Hạ tầng Unity mặc định. Phải gọi trên main thread.</summary>
        public static ModuleContext CreateDefault(RuntimeSdkSettings settings)
        {
            var logs = new UnitySdkLoggerFactory(settings.LogLevel);
            var main = SynchronizationContextDispatcher.CaptureCurrent(logs.Create("dispatcher"));
            return new ModuleContext(settings, main, new SystemClock(), new TimerScheduler(main),
                new PlayerPrefsKeyValueStore(), logs);
        }
    }
}
