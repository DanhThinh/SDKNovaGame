#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AdjustSdk;
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.Tracking;
using UnityEngine;

namespace NovaGames.Mobile.Attribution
{
    /// <summary>
    /// Tracking sink Adjust (attribution, deep link, event có token, ad/purchase revenue). At-most-once: lỗi handoff chỉ log.
    /// Init Adjust chỉ sau consent snapshot đầu tiên: consent được gửi trước InitSdk để đi cùng install.
    /// Sink chưa Ready thì bỏ event (game gửi sau khi InitializeAsync xong).
    /// </summary>
    public sealed class AdjustSink : ITrackingSink, IAttributionSink
    {
        const string Op = "attribution.adjust";
        const string DefaultCurrency = "USD";
        const int MaxPendingDeepLinks = 8;
        internal const string ConsentStorageKey = StorageKeys.Prefix + "adjust.consent";

        readonly AdjustSinkSettings? _settings;
        readonly IAdjustApi _api;
        readonly IMainThreadDispatcher _main;
        readonly IKeyValueStore _store;
        readonly ISdkLogger _log;
        readonly bool _isAndroid;
        readonly TaskCompletionSource<bool> _consentReceived =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Queue<DeepLink> _pendingDeepLinks = new Queue<DeepLink>();

        ConsentSnapshot? _consent;
        bool _coppaAtInit;
        string? _sentConsent;
        Task<SdkResult>? _init;
        IDisposable? _deepLinkSubscription;
        IAttributionListener? _listener;
        AttributionData? _attribution;
        bool _ready;
        bool _disposed;

        public AdjustSink(ModuleContext ctx)
            : this(ctx.Settings.GetSinkSettings<AdjustSinkSettings>(TrackingSinkIds.Adjust), new AdjustApi(),
                   ctx.Main, ctx.Store, ctx.Logs.Create("adjust"), Application.platform == RuntimePlatform.Android)
        {
        }

        internal AdjustSink(
            AdjustSinkSettings? settings, IAdjustApi api, IMainThreadDispatcher main,
            IKeyValueStore store, ISdkLogger log, bool isAndroid)
        {
            _settings = settings;
            _api = api;
            _main = main;
            _store = store;
            _log = log;
            _isAndroid = isAndroid;
            _sentConsent = store.TryGetString(ConsentStorageKey, out var sent) ? sent : null;
        }

        public string Id => TrackingSinkIds.Adjust;
        public bool IsReady => _ready && !_disposed;
        public SinkCapabilities Capabilities => SinkCapabilities.Events | SinkCapabilities.AdRevenue | SinkCapabilities.Purchase;

        /// <summary>Chờ Apply(ConsentSnapshot) đầu tiên đã biết vùng (không Unknown) rồi mới InitSdk; token chỉ hủy việc chờ của caller.</summary>
        public Task<SdkResult> InitializeAsync(CancellationToken ct)
        {
            if (_disposed) return Task.FromResult<SdkResult>(SdkError.Disposed(Op));
            if (_ready) return Task.FromResult(SdkResult.Ok);
            if (_settings is null) return Task.FromResult<SdkResult>(ConfigError("Adjust settings missing (RuntimeSdkSettings.WithSinkSettings)"));
            if (string.IsNullOrEmpty(_settings.AppToken)) return Task.FromResult<SdkResult>(ConfigError("Adjust app token is empty for this platform"));

            var task = _init;
            if (task is null || task.IsCompleted)
            {
                task = RunInitAsync(_settings);
                _init = task;
            }
            return SdkTasks.WaitAsync(task, Op + ".init", ct);
        }

        async Task<SdkResult> RunInitAsync(AdjustSinkSettings settings)
        {
            if (_consent is null) _log.Debug("Adjust waiting for the first consent snapshot");
            await _consentReceived.Task;
            if (_disposed) return SdkError.Disposed(Op);

            var consent = _consent!;
            try
            {
                var config = BuildConfig(settings, consent);
                // Adjust v5 giữ third-party sharing/measurement consent gọi trước InitSdk và gửi cùng install.
                ApplyConsent(consent);
                _api.InitSdk(config);
            }
            catch (Exception e)
            {
                _log.Error("Adjust InitSdk failed", e);
                return SdkError.FromException(Op + ".init", e, AdjustMapping.Provider);
            }

            _coppaAtInit = consent.IsUnderAge;
            _ready = true;
            _log.Debug($"Adjust started ({(settings.IsSandbox ? "sandbox" : "production")})");

            StartDeepLinks();
            _log.TryRun("GetAttribution", () => _api.GetAttribution(a => _main.Post(() => OnVendorAttribution(a))));
            return SdkResult.Ok;
        }

        AdjustConfig BuildConfig(AdjustSinkSettings settings, ConsentSnapshot consent)
        {
            var environment = settings.IsSandbox ? AdjustEnvironment.Sandbox : AdjustEnvironment.Production;
            var config = new AdjustConfig(settings.AppToken, environment, settings.LogLevel == AdjustLogLevel.Suppress)
            {
                LogLevel = settings.LogLevel,
                IsSendingInBackgroundEnabled = settings.SendInBackground,
                IsCostDataInAttributionEnabled = settings.CostDataInAttribution,
                // Deep link deferred giao cho game qua IAttributionListener, Adjust không tự mở (tránh nhận 2 lần).
                IsDeferredDeeplinkOpeningEnabled = false,
                AttributionChangedDelegate = a => _main.Post(() => OnVendorAttribution(a)),
                DeferredDeeplinkDelegate = url => _main.Post(() => OnDeepLink(url, isDeferred: true)),
            };
            if (!string.IsNullOrEmpty(settings.DefaultTracker)) config.DefaultTracker = settings.DefaultTracker;
            if (settings.AttConsentWaitingIntervalSeconds > 0)
                config.AttConsentWaitingInterval = Math.Min(settings.AttConsentWaitingIntervalSeconds, AdjustConfigRules.MaxAttWaitingSeconds);
            if (consent.IsUnderAge)
            {
                config.IsCoppaComplianceEnabled = true;
                if (_isAndroid) config.IsPlayStoreKidsComplianceEnabled = true;
            }
            return config;
        }

        /// <summary>Có thể được gọi trước InitializeAsync: snapshot mới nhất được áp ngay trước InitSdk.</summary>
        public void Apply(ConsentSnapshot snapshot)
        {
            if (_disposed || snapshot is null) return;
            _consent = snapshot;
            if (_ready)
            {
                if (snapshot.IsUnderAge && !_coppaAtInit)
                    _log.Warning("User became under-age after Adjust started; COPPA mode applies from next launch");
                ApplyConsent(snapshot);
            }
            // Chỉ khởi động khi đã biết người dùng thuộc vùng nào: consent "Unknown" (UMP lỗi/timeout lần đầu) mà init thì
            // Adjust dùng mặc định là chia sẻ dữ liệu cho partner, kể cả với người dùng EEA chưa đồng ý.
            if (AdjustConsentState.From(snapshot) is null)
            {
                if (!_ready) _log.Debug("Adjust waiting for a consent with a known region");
                return;
            }
            _consentReceived.TrySetResult(true);
        }

        void ApplyConsent(ConsentSnapshot snapshot)
        {
            var state = AdjustConsentState.From(snapshot);
            if (state is null) return;
            var signature = state.Signature;
            if (signature == _sentConsent) return;

            bool ok = _log.TryRun("TrackThirdPartySharing", () => _api.TrackThirdPartySharing(state.ToThirdPartySharing()));
            if (state.MeasurementConsent.HasValue)
            {
                bool measurement = state.MeasurementConsent.Value;
                ok &= _log.TryRun("TrackMeasurementConsent", () => _api.TrackMeasurementConsent(measurement));
            }
            if (!ok) return;

            _sentConsent = signature;
            try
            {
                _store.SetString(ConsentStorageKey, signature);
            }
            catch (Exception e)
            {
                _log.Warning("Saving Adjust consent state failed", e);
            }
        }

        public void Send(TrackingEvent e)
        {
            if (e is null || !CanSend(e.Name)) return;
            if (!_settings!.EventTokens.TryGetValue(e.Name, out var token))
            {
                _log.Debug($"'{e.Name}' has no Adjust event token; not sent to Adjust");
                return;
            }

            var adjustEvent = new AdjustEvent(token);
            var parameters = e.Params ?? TrackingEvent.NoParams;
            for (int i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i];
                if (string.IsNullOrEmpty(p.Name)) continue;
                if (p.Kind == TrackingParamKind.Double && (double.IsNaN(p.DoubleValue) || double.IsInfinity(p.DoubleValue)))
                {
                    _log.Warning($"'{e.Name}': dropping non-finite param '{p.Name}'");
                    continue;
                }
                adjustEvent.AddCallbackParameter(p.Name, AdjustMapping.ParamValue(p));
            }
            if (_log.TryRun(e.Name, () => _api.TrackEvent(adjustEvent))) _log.Debug($"Adjust event '{e.Name}' ({token})");
        }

        public void SendAdRevenue(AdRevenueEvent e)
        {
            if (e is null || !CanSend("ad_revenue")) return;
            if (!IsValidAmount(e.Value))
            {
                _log.Warning($"Dropping ad revenue with invalid value {e.Value} (show {e.ShowOperationId})");
                return;
            }

            var revenue = new AdjustAdRevenue(AdjustMapping.AdRevenueSource(e.Mediation));
            revenue.SetRevenue(e.Value, Currency(e.Currency));
            if (!string.IsNullOrEmpty(e.Network)) revenue.AdRevenueNetwork = e.Network;
            if (!string.IsNullOrEmpty(e.AdUnitId)) revenue.AdRevenueUnit = e.AdUnitId;
            if (!string.IsNullOrEmpty(e.PlacementId)) revenue.AdRevenuePlacement = e.PlacementId;
            _log.TryRun("TrackAdRevenue", () => _api.TrackAdRevenue(revenue));
        }

        public void SendPurchase(PurchaseRevenueEvent e)
        {
            if (e is null || !CanSend("purchase")) return;
            var settings = _settings!;
            if (!settings.EventTokens.TryGetValue(settings.PurchaseEventName, out var token))
            {
                _log.Debug($"Purchase event '{settings.PurchaseEventName}' has no Adjust event token; purchase not sent to Adjust");
                return;
            }
            if (string.IsNullOrEmpty(e.TransactionId))
            {
                _log.Warning("Dropping purchase without transaction id");
                return;
            }
            if (!IsValidAmount(e.Value))
            {
                _log.Warning($"Dropping purchase {e.TransactionId} with invalid value {e.Value}");
                return;
            }

            var purchase = new AdjustEvent(token)
            {
                // Adjust bỏ event trùng DeduplicationId (purchase restore/replay).
                DeduplicationId = e.TransactionId,
                TransactionId = e.TransactionId,
                ProductId = string.IsNullOrEmpty(e.ProductId) ? null : e.ProductId,
            };
            purchase.SetRevenue(e.Value, Currency(e.Currency));
            if (e.Quantity != 1) purchase.AddCallbackParameter("quantity", e.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _log.TryRun("TrackEvent purchase", () => _api.TrackEvent(purchase));
        }

        public void SetListener(IAttributionListener? listener)
        {
            if (_disposed) return;
            _listener = listener;
            if (listener is null) return;

            var attribution = _attribution;
            if (attribution != null) Notify(l => l.OnAttribution(attribution));
            while (_pendingDeepLinks.Count > 0 && _listener == listener)
            {
                var link = _pendingDeepLinks.Dequeue();
                Notify(l => l.OnDeepLink(link));
            }
        }

        void StartDeepLinks()
        {
            try
            {
                _deepLinkSubscription = _api.SubscribeDeepLinks(url => OnDeepLink(url, isDeferred: false));
                var launchUrl = _api.LaunchUrl;
                if (!string.IsNullOrEmpty(launchUrl)) OnDeepLink(launchUrl!, isDeferred: false);
            }
            catch (Exception e)
            {
                _log.Error("Subscribing to deep links failed", e);
            }
        }

        void OnDeepLink(string? url, bool isDeferred)
        {
            if (_disposed || string.IsNullOrEmpty(url)) return;
            if (!isDeferred && _api.ForwardsDeepLinks) _log.TryRun("ProcessDeeplink", () => _api.ProcessDeeplink(url!));

            var link = new DeepLink(url!, isDeferred);
            if (_listener != null)
            {
                Notify(l => l.OnDeepLink(link));
                return;
            }
            if (_pendingDeepLinks.Count == MaxPendingDeepLinks) _pendingDeepLinks.Dequeue();
            _pendingDeepLinks.Enqueue(link);
        }

        void OnVendorAttribution(AdjustAttribution? attribution)
        {
            if (_disposed) return;
            AttributionData? data;
            try
            {
                data = AdjustMapping.Attribution(attribution);
            }
            catch (Exception e)
            {
                _log.Error("Reading Adjust attribution failed", e);
                return;
            }
            if (data is null || data.Equals(_attribution)) return;

            _attribution = data;
            _log.Debug($"Adjust attribution: network '{data.Network}', campaign '{data.Campaign}'");
            Notify(l => l.OnAttribution(data));
        }

        void Notify(Action<IAttributionListener> call)
        {
            var listener = _listener;
            if (listener is null) return;
            try
            {
                call(listener);
            }
            catch (Exception e)
            {
                _log.Error("Attribution listener threw", e);
            }
        }

        bool CanSend(string name)
        {
            if (_disposed) return false;
            if (_ready) return true;
            _log.Warning($"Dropping '{name}': Adjust not ready");
            return false;
        }

        static bool IsValidAmount(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;

        static string Currency(string? currency) => string.IsNullOrEmpty(currency) ? DefaultCurrency : currency!;

        static SdkError ConfigError(string message) =>
            new SdkError(Op + ".config", SdkErrorCategory.Configuration, message, false, AdjustMapping.Provider);

        /// <summary>Adjust không gỡ được delegate đã đăng ký: callback sau Dispose bị bỏ qua. SDK native vẫn chạy tới khi app tắt.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ready = false;
            _listener = null;
            _pendingDeepLinks.Clear();
            _deepLinkSubscription?.Dispose();
            _deepLinkSubscription = null;
            _consentReceived.TrySetResult(false);
        }
    }
}
