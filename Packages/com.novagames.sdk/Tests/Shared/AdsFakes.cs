#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.Entitlements;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.Tracking;

namespace NovaGames.Mobile.Testing
{
    // Adapter giả: ghi lại lệnh từ AdsManager; test bắn callback thủ công (kể cả sai thứ tự/trùng/không bao giờ).
    public sealed class FakeAdsAdapter : IAdsAdapter
    {
        readonly HashSet<string> _loaded = new HashSet<string>(StringComparer.Ordinal);
        TaskCompletionSource<SdkResult>? _init;
        IAdsAdapterListener? _listener;

        public FakeAdsAdapter(string id = AdProviderIds.Max, AdCapability capabilities =
            AdCapability.Banner | AdCapability.MRec | AdCapability.Interstitial | AdCapability.Rewarded | AdCapability.AppOpen)
        {
            Id = id;
            Capabilities = capabilities;
        }

        public string Id { get; }
        public AdCapability Capabilities { get; set; }
        public bool ManagesFullScreenExpiry { get; set; }
        public bool ManagesExpiry(AdUnit unit) => ManagesFullScreenExpiry;
        // false: InitializeAsync treo tới khi test gọi CompleteInit.
        public bool AutoInitialize { get; set; } = true;

        public int InitializeCount { get; private set; }
        public AdsAdapterInitOptions? LastInitOptions { get; private set; }
        public bool Disposed { get; private set; }
        public readonly List<ConsentSnapshot> AppliedConsents = new List<ConsentSnapshot>();
        public readonly List<string> Calls = new List<string>();
        public readonly List<AdUnit> Loads = new List<AdUnit>();
        public readonly List<(AdUnit Unit, string PlacementId, Guid OperationId)> Shows = new List<(AdUnit, string, Guid)>();
        public readonly List<AdUnit> Discards = new List<AdUnit>();
        public readonly Dictionary<string, AdViewRequest> AdViews = new Dictionary<string, AdViewRequest>(StringComparer.Ordinal);
        public readonly List<AdViewRequest> AdViewRequests = new List<AdViewRequest>();

        public Guid LastOperationId => Shows.Count == 0 ? Guid.Empty : Shows[Shows.Count - 1].OperationId;
        public AdUnit LastShownUnit => Shows[Shows.Count - 1].Unit;

        public void Apply(ConsentSnapshot snapshot)
        {
            AppliedConsents.Add(snapshot);
            Calls.Add("Apply");
        }

        public void SetListener(IAdsAdapterListener listener) => _listener = listener;

        public Task<SdkResult> InitializeAsync(AdsAdapterInitOptions options, CancellationToken ct)
        {
            InitializeCount++;
            LastInitOptions = options;
            Calls.Add("Initialize");
            if (AutoInitialize) return Task.FromResult(SdkResult.Ok);
            // Như adapter thật: đang init thì dùng chung; lần trước lỗi thì bắt đầu lượt init mới.
            if (_init is null || (_init.Task.IsCompleted && !_init.Task.Result.IsSuccess))
                _init = new TaskCompletionSource<SdkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _init.Task;
        }

        public void CompleteInit(SdkResult result) => _init?.TrySetResult(result);

        public void Load(AdUnit unit)
        {
            Loads.Add(unit);
            Calls.Add("Load " + unit.Key);
        }

        public int LoadCount(string unitKey) => Loads.FindAll(u => u.Key == unitKey).Count;

        public bool IsReady(AdUnit unit) => _loaded.Contains(unit.Key);

        public void Show(AdUnit unit, string placementId, Guid showOperationId)
        {
            _loaded.Remove(unit.Key);
            Shows.Add((unit, placementId, showOperationId));
            Calls.Add("Show " + unit.Key);
        }

        public void Discard(AdUnit unit)
        {
            _loaded.Remove(unit.Key);
            Discards.Add(unit);
        }

        public void ShowAdView(AdUnit unit, AdViewRequest request)
        {
            AdViews[unit.Key] = request;
            AdViewRequests.Add(request);
            Calls.Add("ShowAdView " + unit.Key);
        }

        public void HideAdView(AdUnit unit) => Calls.Add("HideAdView " + unit.Key);

        public void DestroyAdView(AdUnit unit)
        {
            AdViews.Remove(unit.Key);
            Calls.Add("DestroyAdView " + unit.Key);
        }

        public void OpenDebugger() => Calls.Add("OpenDebugger");

        public void Dispose() => Disposed = true;

        // ---- Callback từ "vendor" ----

        IAdsAdapterListener Listener => _listener ?? throw new InvalidOperationException("Listener not set");

        public void FireLoaded(AdUnit unit)
        {
            _loaded.Add(unit.Key);
            Listener.OnLoaded(unit);
        }

        // Vendor đã có ad nhưng callback loaded chưa tới AdsManager (đang xếp hàng, hoặc MAX giữ ad sau Discard).
        public void SetReadyWithoutCallback(AdUnit unit) => _loaded.Add(unit.Key);

        public void FireLoadFailed(AdUnit unit, AdLoadFailure kind = AdLoadFailure.NoFill) =>
            Listener.OnLoadFailed(unit, new AdLoadError(kind, "fake " + kind));

        public void FireDisplayed(Guid id) => Listener.OnDisplayed(UnitOf(id), id);
        public void FireDisplayFailed(Guid id) => Listener.OnDisplayFailed(UnitOf(id), id, "fake display failure");
        public void FireRewarded(Guid id, double amount = 1) => Listener.OnRewarded(UnitOf(id), id, new AdReward("coin", amount));
        public void FireClosed(Guid id) => Listener.OnClosed(UnitOf(id), id);
        public void FireClicked(AdUnit unit, Guid id) => Listener.OnClicked(unit, id);

        public void FirePaid(AdUnit unit, Guid id, double value = 0.01, string? impressionId = null) =>
            Listener.OnPaid(unit, id, new AdPaidValue(value, "USD", RevenuePrecision.Precise, "fake_network", impressionId));

        public void FireLayout(AdUnit unit, BannerLayout layout) => Listener.OnAdViewLayoutChanged(unit, layout);
        public void FireMrecSize(AdUnit unit, MrecSize size) => Listener.OnMrecSizeChanged(unit, size);

        AdUnit UnitOf(Guid id)
        {
            foreach (var show in Shows)
            {
                if (show.OperationId == id) return show.Unit;
            }
            throw new InvalidOperationException("Unknown show operation " + id);
        }
    }

    public sealed class FakeEntitlements : IEntitlementProvider
    {
        readonly HashSet<EntitlementId> _active = new HashSet<EntitlementId>();
        readonly SdkEvent<EntitlementId> _changed = new SdkEvent<EntitlementId>();

        public bool IsActive(EntitlementId id) => _active.Contains(id);
        public ISdkEvent<EntitlementId> Changed => _changed;

        public void Set(EntitlementId id, bool active)
        {
            if (active ? _active.Add(id) : _active.Remove(id)) _changed.Raise(id);
        }
    }

    public sealed class FakeLifecycle : IApplicationLifecycle
    {
        readonly SdkEvent<bool> _pauseChanged = new SdkEvent<bool>();

        public bool IsPaused { get; private set; }
        public ISdkEvent<bool> PauseChanged => _pauseChanged;

        public void SetPaused(bool paused)
        {
            if (IsPaused == paused) return;
            IsPaused = paused;
            _pauseChanged.Raise(paused);
        }
    }

    public sealed class FakeNetwork : INetworkStatus
    {
        public bool IsReachable { get; set; } = true;
    }

    public sealed class RecordingRevenuePipeline : IRevenuePipeline
    {
        public readonly List<AdRevenueEvent> AdRevenue = new List<AdRevenueEvent>();
        public readonly List<PurchaseRevenueEvent> Purchases = new List<PurchaseRevenueEvent>();

        public void ReportAdRevenue(AdRevenueEvent e) => AdRevenue.Add(e);
        public void ReportPurchase(PurchaseRevenueEvent e) => Purchases.Add(e);
    }

    public static class TestConsent
    {
        public static ConsentSnapshot Granted { get; } = ConsentSnapshot.Unknown with
        {
            Jurisdiction = Jurisdiction.None,
            AnalyticsStorage = ConsentState.Granted,
            AdStorage = ConsentState.Granted,
            AdUserData = ConsentState.Granted,
            AdPersonalization = ConsentState.Granted,
            Att = AttStatus.NotApplicable,
            CanRequestAds = true,
        };

        public static ConsentSnapshot Denied { get; } = ConsentSnapshot.Unknown with
        {
            Jurisdiction = Jurisdiction.Gdpr,
            AdStorage = ConsentState.Denied,
            CanRequestAds = false,
        };
    }
}
