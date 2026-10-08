#nullable enable
using System;
using System.Collections.Generic;
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.Diagnostics;
using NovaGames.Mobile.Iap;
using NovaGames.Mobile.Notifications;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.RemoteConfig;
using NovaGames.Mobile.Tracking;
using UnityEngine;

namespace NovaGames.Mobile.Bootstrap
{
    /// <summary>
    /// Adapter tự đăng ký factory ở AfterAssembliesLoaded; composition root (NovaSdk) lấy Snapshot() rồi
    /// chỉ tạo adapter cần dùng.
    /// </summary>
    public static class AdapterRegistry
    {
        static readonly object Gate = new object();
        static readonly Dictionary<string, Func<ModuleContext, IRemoteConfigSource>> RemoteConfigSources =
            new Dictionary<string, Func<ModuleContext, IRemoteConfigSource>>(StringComparer.Ordinal);
        static readonly Dictionary<string, Func<ModuleContext, ITrackingSink>> TrackingSinks =
            new Dictionary<string, Func<ModuleContext, ITrackingSink>>(StringComparer.Ordinal);
        static readonly Dictionary<string, Func<ModuleContext, IAdsAdapter>> AdsAdapters =
            new Dictionary<string, Func<ModuleContext, IAdsAdapter>>(StringComparer.Ordinal);
        static readonly Dictionary<string, Func<ModuleContext, IConsentPlatform>> ConsentPlatforms =
            new Dictionary<string, Func<ModuleContext, IConsentPlatform>>(StringComparer.Ordinal);
        static readonly Dictionary<string, Func<ModuleContext, IStoreAdapter>> StoreAdapters =
            new Dictionary<string, Func<ModuleContext, IStoreAdapter>>(StringComparer.Ordinal);
        static readonly Dictionary<string, Func<ModuleContext, INotificationPlatform>> NotificationPlatforms =
            new Dictionary<string, Func<ModuleContext, INotificationPlatform>>(StringComparer.Ordinal);
        static readonly Dictionary<string, Func<ModuleContext, ICrashReporter>> CrashReporters =
            new Dictionary<string, Func<ModuleContext, ICrashReporter>>(StringComparer.Ordinal);
        static readonly Dictionary<string, Func<ModuleContext, IAttPlatform>> AttPlatforms =
            new Dictionary<string, Func<ModuleContext, IAttPlatform>>(StringComparer.Ordinal);

        public static void RegisterRemoteConfig(string sourceId, Func<ModuleContext, IRemoteConfigSource> factory)
        {
            if (string.IsNullOrEmpty(sourceId)) throw new ArgumentException("Source id is required.", nameof(sourceId));
            if (factory is null) throw new ArgumentNullException(nameof(factory));
            lock (Gate) RemoteConfigSources[sourceId] = factory;
        }

        public static void RegisterTrackingSink(string sinkId, Func<ModuleContext, ITrackingSink> factory)
        {
            if (string.IsNullOrEmpty(sinkId)) throw new ArgumentException("Sink id is required.", nameof(sinkId));
            if (factory is null) throw new ArgumentNullException(nameof(factory));
            lock (Gate) TrackingSinks[sinkId] = factory;
        }

        /// <summary>Factory chỉ được gọi cho provider có format được chọn; adapter còn lại không được tạo.</summary>
        public static void RegisterAds(string providerId, Func<ModuleContext, IAdsAdapter> factory)
        {
            if (string.IsNullOrEmpty(providerId)) throw new ArgumentException("Provider id is required.", nameof(providerId));
            if (factory is null) throw new ArgumentNullException(nameof(factory));
            lock (Gate) AdsAdapters[providerId] = factory;
        }

        public static void RegisterConsentPlatform(string platformId, Func<ModuleContext, IConsentPlatform> factory)
        {
            if (string.IsNullOrEmpty(platformId)) throw new ArgumentException("Platform id is required.", nameof(platformId));
            if (factory is null) throw new ArgumentNullException(nameof(factory));
            lock (Gate) ConsentPlatforms[platformId] = factory;
        }

        public static void RegisterStore(string storeId, Func<ModuleContext, IStoreAdapter> factory)
        {
            if (string.IsNullOrEmpty(storeId)) throw new ArgumentException("Store id is required.", nameof(storeId));
            if (factory is null) throw new ArgumentNullException(nameof(factory));
            lock (Gate) StoreAdapters[storeId] = factory;
        }

        public static void RegisterNotifications(string platformId, Func<ModuleContext, INotificationPlatform> factory)
        {
            if (string.IsNullOrEmpty(platformId)) throw new ArgumentException("Platform id is required.", nameof(platformId));
            if (factory is null) throw new ArgumentNullException(nameof(factory));
            lock (Gate) NotificationPlatforms[platformId] = factory;
        }

        public static void RegisterCrashReporter(string reporterId, Func<ModuleContext, ICrashReporter> factory)
        {
            if (string.IsNullOrEmpty(reporterId)) throw new ArgumentException("Reporter id is required.", nameof(reporterId));
            if (factory is null) throw new ArgumentNullException(nameof(factory));
            lock (Gate) CrashReporters[reporterId] = factory;
        }

        public static void RegisterAttPlatform(string platformId, Func<ModuleContext, IAttPlatform> factory)
        {
            if (string.IsNullOrEmpty(platformId)) throw new ArgumentException("Platform id is required.", nameof(platformId));
            if (factory is null) throw new ArgumentNullException(nameof(factory));
            lock (Gate) AttPlatforms[platformId] = factory;
        }

        public static AdapterRegistrySnapshot Snapshot()
        {
            lock (Gate)
            {
                return new AdapterRegistrySnapshot(
                    new Dictionary<string, Func<ModuleContext, IRemoteConfigSource>>(RemoteConfigSources, StringComparer.Ordinal),
                    new Dictionary<string, Func<ModuleContext, ITrackingSink>>(TrackingSinks, StringComparer.Ordinal),
                    new Dictionary<string, Func<ModuleContext, IAdsAdapter>>(AdsAdapters, StringComparer.Ordinal),
                    new Dictionary<string, Func<ModuleContext, IConsentPlatform>>(ConsentPlatforms, StringComparer.Ordinal),
                    new Dictionary<string, Func<ModuleContext, IStoreAdapter>>(StoreAdapters, StringComparer.Ordinal),
                    new Dictionary<string, Func<ModuleContext, INotificationPlatform>>(NotificationPlatforms, StringComparer.Ordinal),
                    new Dictionary<string, Func<ModuleContext, ICrashReporter>>(CrashReporters, StringComparer.Ordinal),
                    new Dictionary<string, Func<ModuleContext, IAttPlatform>>(AttPlatforms, StringComparer.Ordinal));
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            lock (Gate)
            {
                RemoteConfigSources.Clear();
                TrackingSinks.Clear();
                AdsAdapters.Clear();
                ConsentPlatforms.Clear();
                StoreAdapters.Clear();
                NotificationPlatforms.Clear();
                CrashReporters.Clear();
                AttPlatforms.Clear();
            }
        }
    }

    public sealed class AdapterRegistrySnapshot
    {
        internal AdapterRegistrySnapshot(
            IReadOnlyDictionary<string, Func<ModuleContext, IRemoteConfigSource>> remoteConfigSources,
            IReadOnlyDictionary<string, Func<ModuleContext, ITrackingSink>> trackingSinks,
            IReadOnlyDictionary<string, Func<ModuleContext, IAdsAdapter>> adsAdapters,
            IReadOnlyDictionary<string, Func<ModuleContext, IConsentPlatform>>? consentPlatforms = null,
            IReadOnlyDictionary<string, Func<ModuleContext, IStoreAdapter>>? storeAdapters = null,
            IReadOnlyDictionary<string, Func<ModuleContext, INotificationPlatform>>? notificationPlatforms = null,
            IReadOnlyDictionary<string, Func<ModuleContext, ICrashReporter>>? crashReporters = null,
            IReadOnlyDictionary<string, Func<ModuleContext, IAttPlatform>>? attPlatforms = null)
        {
            RemoteConfigSources = remoteConfigSources;
            TrackingSinks = trackingSinks;
            AdsAdapters = adsAdapters;
            ConsentPlatforms = consentPlatforms ?? new Dictionary<string, Func<ModuleContext, IConsentPlatform>>(StringComparer.Ordinal);
            StoreAdapters = storeAdapters ?? new Dictionary<string, Func<ModuleContext, IStoreAdapter>>(StringComparer.Ordinal);
            NotificationPlatforms = notificationPlatforms ?? new Dictionary<string, Func<ModuleContext, INotificationPlatform>>(StringComparer.Ordinal);
            CrashReporters = crashReporters ?? new Dictionary<string, Func<ModuleContext, ICrashReporter>>(StringComparer.Ordinal);
            AttPlatforms = attPlatforms ?? new Dictionary<string, Func<ModuleContext, IAttPlatform>>(StringComparer.Ordinal);
        }

        public IReadOnlyDictionary<string, Func<ModuleContext, IRemoteConfigSource>> RemoteConfigSources { get; }
        public IReadOnlyDictionary<string, Func<ModuleContext, ITrackingSink>> TrackingSinks { get; }
        public IReadOnlyDictionary<string, Func<ModuleContext, IAdsAdapter>> AdsAdapters { get; }
        public IReadOnlyDictionary<string, Func<ModuleContext, IConsentPlatform>> ConsentPlatforms { get; }
        public IReadOnlyDictionary<string, Func<ModuleContext, IStoreAdapter>> StoreAdapters { get; }
        public IReadOnlyDictionary<string, Func<ModuleContext, INotificationPlatform>> NotificationPlatforms { get; }
        public IReadOnlyDictionary<string, Func<ModuleContext, ICrashReporter>> CrashReporters { get; }
        public IReadOnlyDictionary<string, Func<ModuleContext, IAttPlatform>> AttPlatforms { get; }

        /// <summary>
        /// Chỉ tạo adapter của provider có unit trong options. Một provider -&gt; adapter đó; nhiều provider (hoặc có provider
        /// chưa cài) -&gt; RoutingAdsAdapter. null = không format nào dùng provider đã cài.
        /// missing: provider được chọn nhưng adapter chưa cài (format của nó thành Unsupported).
        /// </summary>
        public IAdsAdapter? CreateAds(AdsOptions options, ModuleContext ctx, out IReadOnlyList<AdsProvider> missing)
        {
            var used = options.UsedProviders;
            var created = new Dictionary<AdsProvider, IAdsAdapter>();
            var notInstalled = new List<AdsProvider>();
            foreach (var provider in used)
            {
                if (AdsAdapters.TryGetValue(AdsOptions.ProviderId(provider), out var factory)) created[provider] = factory(ctx);
                else notInstalled.Add(provider);
            }
            missing = notInstalled;

            if (created.Count == 0) return null;
            if (created.Count == 1 && notInstalled.Count == 0)
            {
                foreach (var adapter in created.Values) return adapter;
            }
            return new RoutingAdsAdapter(created, options.AdUnits);
        }
    }
}
