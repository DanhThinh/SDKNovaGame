#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Entitlements;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.RemoteConfig;
using NovaGames.Mobile.Tracking;

namespace NovaGames.Mobile.Ads
{
    public sealed record AdsDependencies(ISdkProperty<ConsentSnapshot> Consent)
    {
        public IRemoteConfigService? RemoteConfig { get; init; }
        public AdsConfigKeys ConfigKeys { get; init; } = AdsConfigKeys.Default;
        public IEntitlementProvider Entitlements { get; init; } = NoEntitlements.Instance;
        public IRevenuePipeline? Revenue { get; init; }
        public IApplicationLifecycle? Lifecycle { get; init; }
        public INetworkStatus? Network { get; init; }
        // true khi Force Update = Required/Maintenance: chặn toàn bộ Ads.
        public ISdkProperty<bool>? ForceUpdateBlocking { get; init; }
        // iOS: Unity không tự pause khi ad hiển thị nên tắt resume watchdog.
        public bool IsIos { get; init; }
        // Chờ người dùng trả lời ATT trước khi init ads (tối đa AttHoldTimeout). null = chờ khi IsIos.
        public bool? WaitForAtt { get; init; }
        public Func<double>? Random { get; init; }
    }

    // Orchestrator Ads: gate consent/ATT/force update, entitlement, capping, single full-screen lock,
    // app-open policy, banner/MREC, revenue identity. Chỉ phụ thuộc IAdsAdapter. Mọi API gọi trên main thread.
    public sealed partial class AdsManager : IAdsService, IFullScreenAds, IBannerAds, IMrecAds, IAppOpenAds, IDisposable
    {
        const string Op = "ads";
        // Resume của app ngay sau khi full-screen đóng là do chính ad activity (Android): không bật app-open.
        static readonly TimeSpan PostAdResumeWindow = TimeSpan.FromSeconds(2);
        static readonly TimeSpan ResumeWatchdog = TimeSpan.FromSeconds(3);
        static readonly TimeSpan NetworkCheckInterval = TimeSpan.FromSeconds(5);
        const int RecentOperationCapacity = 16;

        readonly IAdsAdapter? _adapter;
        readonly AdsOptions _options;
        readonly ModuleContext _ctx;
        readonly AdsDependencies _deps;
        readonly ISdkLogger _log;
        readonly AdsCapping _capping;
        readonly Func<double> _random;
        readonly string _providerId;

        readonly SdkProperty<AdsModuleState> _state;
        readonly SdkEvent<AdImpression> _impressions;
        readonly SdkEvent<FullScreenAdPresentation> _presentation;
        readonly SdkEvent<AdAvailabilityChanged> _availabilityChanged;
        readonly SdkEvent<BannerLayoutChanged> _layoutChanged;
        readonly SdkEvent<MrecSizeChanged> _mrecSizeChanged;

        readonly Dictionary<string, AdUnit> _unitsByKey = new Dictionary<string, AdUnit>(StringComparer.Ordinal);
        readonly Dictionary<string, AdUnit> _unitsByPlacement = new Dictionary<string, AdUnit>(StringComparer.Ordinal);
        // Unit đầu tiên của mỗi format, cho placement không khai báo trong AdsOptions.Placements.
        readonly Dictionary<AdFormat, AdUnit> _defaultUnits = new Dictionary<AdFormat, AdUnit>();
        readonly Dictionary<string, AdUnitSlot> _slots = new Dictionary<string, AdUnitSlot>(StringComparer.Ordinal);
        readonly Dictionary<string, FullScreenPlacement> _knownPlacements = new Dictionary<string, FullScreenPlacement>(StringComparer.Ordinal);
        readonly Dictionary<string, AdViewEntry> _views = new Dictionary<string, AdViewEntry>(StringComparer.Ordinal);
        readonly Dictionary<string, AdAvailability> _lastAvailability = new Dictionary<string, AdAvailability>(StringComparer.Ordinal);
        readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        readonly HashSet<Suppression> _suppressions = new HashSet<Suppression>();
        readonly Queue<CompletedOperation> _recentOperations = new Queue<CompletedOperation>();
        readonly HashSet<Guid> _paidOperations = new HashSet<Guid>();
        readonly Queue<string> _recentImpressionIds = new Queue<string>();

        AdsPolicy _policy;
        IAdPresentationHandler? _presentationHandler;
        AppOpenPlacement? _autoAppOpen;
        ShowOperation? _current;
        Task<SdkResult>? _adapterInit;
        IDisposable? _initRetry;
        int _initAttempt;
        IDisposable? _attHoldTimer;
        IDisposable? _networkTimer;
        bool _attHoldExpired;
        bool _adapterReady;
        bool _initStarted;
        bool _started;
        bool _disposed;
        int _playerLevel;
        DateTime _lastFullScreenClosedUtc = DateTime.MinValue;
        DateTime _lastCollapsibleRequestUtc = DateTime.MinValue;
        DateTime _backgroundSinceUtc = DateTime.MinValue;
        bool _resumeCausedByAd;

        public AdsManager(IAdsAdapter? adapter, AdsOptions options, ModuleContext ctx, AdsDependencies deps)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _deps = deps ?? throw new ArgumentNullException(nameof(deps));
            _adapter = options.UsedProviders.Count == 0 ? null : adapter;
            _providerId = _adapter?.Id ?? "none";
            _log = ctx.Logs.Create("ads");
            _capping = new AdsCapping(ctx.Store, ctx.Clock, _log);
            _random = deps.Random ?? CreateRandom();
            _policy = deps.RemoteConfig != null ? deps.ConfigKeys.Read(deps.RemoteConfig.Current.Value) : AdsPolicy.Default;

            _state = new SdkProperty<AdsModuleState>(AdsModuleState.NotStarted, _log);
            _impressions = new SdkEvent<AdImpression>(_log);
            _presentation = new SdkEvent<FullScreenAdPresentation>(_log);
            _availabilityChanged = new SdkEvent<AdAvailabilityChanged>(_log);
            _layoutChanged = new SdkEvent<BannerLayoutChanged>(_log);
            _mrecSizeChanged = new SdkEvent<MrecSizeChanged>(_log);

            foreach (var unit in options.AdUnits)
            {
                _unitsByKey[unit.Key] = unit;
                if (!_defaultUnits.ContainsKey(unit.Format)) _defaultUnits[unit.Format] = unit;
            }
            foreach (var binding in options.Placements)
            {
                if (_unitsByKey.TryGetValue(binding.AdUnitKey, out var unit)) _unitsByPlacement[binding.PlacementId] = unit;
            }
        }

        public ISdkProperty<AdsModuleState> State => _state;
        public AdsPolicy Policy => _policy;
        public IFullScreenAds FullScreen => this;
        public IBannerAds Banners => this;
        public IMrecAds Mrec => this;
        public IAppOpenAds AppOpen => this;
        public ISdkEvent<AdImpression> Impressions => _impressions;
        public ISdkEvent<FullScreenAdPresentation> Presentation => _presentation;
        public ISdkEvent<AdAvailabilityChanged> AvailabilityChanged => _availabilityChanged;
        public ISdkEvent<BannerLayoutChanged> LayoutChanged => _layoutChanged;
        public ISdkEvent<MrecSizeChanged> SizeChanged => _mrecSizeChanged;

        public bool Supports(AdCapability capability) =>
            _adapter != null && capability != AdCapability.None && (_adapter.Capabilities & capability) == capability;

        public void SetPresentationHandler(IAdPresentationHandler? handler) => _presentationHandler = handler;

        public void SetPlayerLevel(int level)
        {
            if (_playerLevel == level) return;
            _playerLevel = level;
            RaiseAvailabilityChanges();
        }

        public void OpenDebugger()
        {
            if (!_ctx.Settings.IsDevelopment || _adapter is null || !_adapterReady) return;
            _log.TryRun("OpenDebugger", () => _adapter.OpenDebugger());
        }

        // ---------------- Dispose ----------------

        public void Dispose()
        {
            if (_disposed) return;
            // Đánh dấu trước để Complete -> EndShow không kích hoạt load mới.
            _disposed = true;
            var current = _current;
            if (current != null) Complete(current, ShowOutcome.Cancelled, SdkError.Disposed(Op));
            _initRetry?.Dispose();
            _attHoldTimer?.Dispose();
            _networkTimer?.Dispose();
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
            foreach (var slot in _slots.Values) slot.Dispose();
            _slots.Clear();
            foreach (var entry in new List<AdViewEntry>(_views.Values))
            {
                entry.RetryTimer?.Dispose();
                if (entry.Requested) _log.TryRun("DestroyAdView", () => _adapter!.DestroyAdView(entry.Unit));
            }
            _views.Clear();
            _suppressions.Clear();
            if (_adapter != null) _log.TryRun("Dispose adapter", _adapter.Dispose);
            _state.Set(AdsModuleState.Disposed);
            _state.ClearSubscribers();
            _impressions.Clear();
            _presentation.Clear();
            _availabilityChanged.Clear();
            _layoutChanged.Clear();
            _mrecSizeChanged.Clear();
        }

        static Func<double> CreateRandom()
        {
            var random = new Random();
            return random.NextDouble;
        }

        static T[] ToArray<T>(IReadOnlyList<T> list)
        {
            var array = new T[list.Count];
            for (int i = 0; i < array.Length; i++) array[i] = list[i];
            return array;
        }
    }
}
