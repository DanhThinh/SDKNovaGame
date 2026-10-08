#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.Tracking;
using UnityEngine;

namespace NovaGames.Mobile.Firebase
{
    /// <summary>
    /// Tracking sink Firebase Analytics. At-most-once: lỗi handoff chỉ log, không retry.
    /// Sink chưa Ready thì bỏ event (game gửi sau khi InitializeAsync xong); consent/user property được giữ lại (latest-wins).
    /// </summary>
    public sealed class FirebaseAnalyticsSink : ITrackingSink, IUserPropertySink
    {
        const string Op = "analytics.firebase";
        const string AdImpressionEvent = "ad_impression";

        readonly FirebaseAppInitializer _app;
        readonly IFirebaseAnalyticsApi _api;
        readonly ISdkLogger _log;
        readonly AnalyticsOptions _settings;
        readonly bool _logAppleTransactions;
        readonly Dictionary<string, string?> _pendingUserProperties = new Dictionary<string, string?>(StringComparer.Ordinal);

        ConsentSnapshot? _pendingConsent;
        bool _hasPendingUserId;
        string? _pendingUserId;
        Task<SdkResult>? _init;
        Task? _lateInit;
        bool _ready;
        bool _disposed;

        public FirebaseAnalyticsSink(ModuleContext ctx)
            : this(FirebaseAppInitializer.Create(ctx), new FirebaseAnalyticsApi(), ctx.Logs.Create("firebase.analytics"),
                   ctx.Settings.Analytics, Application.platform == RuntimePlatform.IPhonePlayer)
        {
        }

        internal FirebaseAnalyticsSink(
            FirebaseAppInitializer app, IFirebaseAnalyticsApi api, ISdkLogger log,
            AnalyticsOptions settings, bool logAppleTransactions)
        {
            _app = app;
            _api = api;
            _log = log;
            _settings = settings;
            _logAppleTransactions = logAppleTransactions;

            // Android: Firebase tự log in_app_purchase từ Google Play Billing.
            // iOS: StoreKit 2 (Unity IAP 5) cần LogAppleTransactionAsync.
            Capabilities = SinkCapabilities.Events | SinkCapabilities.AdRevenue | SinkCapabilities.UserProperties
                           | SinkCapabilities.ConsentMode
                           | (logAppleTransactions ? SinkCapabilities.Purchase : SinkCapabilities.None);
        }

        public string Id => TrackingSinkIds.Firebase;
        public bool IsReady => _ready && !_disposed;
        public SinkCapabilities Capabilities { get; }

        public Task<SdkResult> InitializeAsync(CancellationToken ct)
        {
            if (_disposed) return Task.FromResult<SdkResult>(SdkError.Disposed(Op));
            if (_ready) return Task.FromResult(SdkResult.Ok);

            var task = _init;
            if (task is null || task.IsCompleted)
            {
                task = RunInitAsync();
                _init = task;
            }
            return SdkTasks.WaitAsync(task, Op + ".init", ct);
        }

        async Task<SdkResult> RunInitAsync()
        {
            var app = await _app.EnsureAsync(_settings.InitTimeout, CancellationToken.None);
            if (_disposed) return SdkError.Disposed(Op);
            if (!app.IsSuccess)
            {
                // InitTimeout chỉ là hạn chờ của caller: check dependency dùng chung vẫn chạy (máy chậm, Play services
                // đang cập nhật). Chờ tiếp ở nền để sink tự Ready thay vì drop event cả session.
                if (app.Error!.Category == SdkErrorCategory.Timeout && (_lateInit is null || _lateInit.IsCompleted))
                    _lateInit = CompleteAfterTimeoutAsync();
                return app;
            }
            return BecomeReady();
        }

        async Task CompleteAfterTimeoutAsync()
        {
            var app = await _app.EnsureAsync(Timeout.InfiniteTimeSpan, CancellationToken.None);
            if (!app.IsSuccess || _disposed || _ready) return;
            BecomeReady();
            _log.Warning("Firebase Analytics ready after init timeout; events sent before this were dropped");
        }

        SdkResult BecomeReady()
        {
            if (_ready) return SdkResult.Ok;
            try
            {
                if (_settings.CollectionEnabled.HasValue) _api.SetCollectionEnabled(_settings.CollectionEnabled.Value);
                if (_settings.SessionTimeout.HasValue) _api.SetSessionTimeout(_settings.SessionTimeout.Value);
            }
            catch (Exception e)
            {
                _log.Error("Applying analytics settings failed", e);
            }

            _ready = true;
            FlushPending();
            _log.Debug("Firebase Analytics ready");
            return SdkResult.Ok;
        }

        void FlushPending()
        {
            if (_pendingConsent != null)
            {
                ApplyConsent(_pendingConsent);
                _pendingConsent = null;
            }
            if (_hasPendingUserId)
            {
                _log.TryRun("SetUserId", () => _api.SetUserId(_pendingUserId));
                _hasPendingUserId = false;
                _pendingUserId = null;
            }
            foreach (var pair in _pendingUserProperties)
            {
                var name = pair.Key;
                var value = pair.Value;
                _log.TryRun("SetUserProperty " + name, () => _api.SetUserProperty(name, value));
            }
            _pendingUserProperties.Clear();
        }

        /// <summary>Có thể được gọi trước InitializeAsync: giữ snapshot mới nhất và áp ngay khi Firebase sẵn sàng.</summary>
        public void Apply(ConsentSnapshot snapshot)
        {
            if (_disposed || snapshot is null) return;
            if (!_ready)
            {
                _pendingConsent = snapshot;
                return;
            }
            ApplyConsent(snapshot);
        }

        void ApplyConsent(ConsentSnapshot snapshot)
        {
            var consent = FirebaseConsentMapper.Map(snapshot);
            if (consent.Count == 0) return;
            _log.TryRun("SetConsent", () => _api.SetConsent(consent));
        }

        public void Send(TrackingEvent e)
        {
            if (e is null || !CanSend(e.Name)) return;
            if (!FirebaseAnalyticsRules.IsValidEventName(e.Name, out var reason))
            {
                _log.Warning($"Dropping event '{e.Name}': {reason}");
                return;
            }
            var parameters = SanitizeParams(e.Name, e.Params);
            _log.TryRun(e.Name, () => _api.LogEvent(e.Name, parameters));
        }

        public void SendAdRevenue(AdRevenueEvent e)
        {
            if (e is null || !CanSend(AdImpressionEvent)) return;
            if (double.IsNaN(e.Value) || double.IsInfinity(e.Value) || e.Value < 0)
            {
                _log.Warning($"Dropping ad revenue with invalid value {e.Value} (show {e.ShowOperationId})");
                return;
            }

            // Tham số chuẩn của sự kiện ad_impression trong GA4.
            var parameters = SanitizeParams(AdImpressionEvent, new[]
            {
                TrackingParam.Of("ad_platform", e.Mediation),
                TrackingParam.Of("ad_source", e.Network),
                TrackingParam.Of("ad_format", FirebaseAnalyticsRules.FormatName(e.Format)),
                TrackingParam.Of("ad_unit_name", e.AdUnitId),
                TrackingParam.Of("currency", e.Currency),
                TrackingParam.Of("value", e.Value),
            });
            _log.TryRun(AdImpressionEvent, () => _api.LogEvent(AdImpressionEvent, parameters));
        }

        public void SendPurchase(PurchaseRevenueEvent e)
        {
            if (e is null || !CanSend("purchase")) return;
            if (!_logAppleTransactions)
            {
                _log.Debug($"Purchase {e.TransactionId} is collected automatically by Firebase on this platform");
                return;
            }
            if (string.IsNullOrEmpty(e.TransactionId))
            {
                _log.Warning("Dropping purchase without transaction id");
                return;
            }

            Task task;
            try
            {
                task = _api.LogAppleTransactionAsync(e.TransactionId);
            }
            catch (Exception ex)
            {
                _log.Error("LogAppleTransactionAsync handoff failed", ex);
                return;
            }
            // Handoff đã xong; chỉ quan sát lỗi để log (logger thread-safe).
            task.ContinueWith(t => _log.Error("LogAppleTransactionAsync failed", t.Exception?.GetBaseException()),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        public void SetUserProperty(string name, string? value)
        {
            if (_disposed) return;
            if (!FirebaseAnalyticsRules.IsValidUserPropertyName(name, out var reason))
            {
                _log.Warning($"Ignoring user property '{name}': {reason}");
                return;
            }
            if (value != null && value.Length > FirebaseAnalyticsRules.MaxUserPropertyValueLength)
            {
                _log.Warning($"User property '{name}' truncated to {FirebaseAnalyticsRules.MaxUserPropertyValueLength} characters");
                value = FirebaseAnalyticsRules.Truncate(value, FirebaseAnalyticsRules.MaxUserPropertyValueLength);
            }

            if (!_ready)
            {
                _pendingUserProperties[name] = value;
                return;
            }
            _log.TryRun("SetUserProperty " + name, () => _api.SetUserProperty(name, value));
        }

        public void SetUserId(string? id)
        {
            if (_disposed) return;
            if (id != null && id.Length > FirebaseAnalyticsRules.MaxUserIdLength)
            {
                _log.Warning($"Ignoring user id longer than {FirebaseAnalyticsRules.MaxUserIdLength} characters");
                return;
            }

            if (!_ready)
            {
                _hasPendingUserId = true;
                _pendingUserId = id;
                return;
            }
            _log.TryRun("SetUserId", () => _api.SetUserId(id));
        }

        bool CanSend(string name)
        {
            if (_disposed) return false;
            if (_ready) return true;
            _log.Warning($"Dropping '{name}': Firebase Analytics not ready");
            return false;
        }

        IReadOnlyList<TrackingParam> SanitizeParams(string eventName, IReadOnlyList<TrackingParam>? source)
        {
            if (source is null || source.Count == 0) return TrackingEvent.NoParams;

            var result = new List<TrackingParam>(Math.Min(source.Count, FirebaseAnalyticsRules.MaxParamsPerEvent));
            for (int i = 0; i < source.Count; i++)
            {
                var p = source[i];
                if (result.Count == FirebaseAnalyticsRules.MaxParamsPerEvent)
                {
                    _log.Warning($"'{eventName}': dropping {source.Count - i} params over the {FirebaseAnalyticsRules.MaxParamsPerEvent} limit");
                    break;
                }
                if (!FirebaseAnalyticsRules.IsValidParamName(p.Name, out var reason))
                {
                    _log.Warning($"'{eventName}': dropping param '{p.Name}': {reason}");
                    continue;
                }
                switch (p.Kind)
                {
                    case TrackingParamKind.String:
                        var text = p.StringValue ?? string.Empty;
                        if (text.Length > FirebaseAnalyticsRules.MaxParamStringLength)
                        {
                            _log.Warning($"'{eventName}': param '{p.Name}' truncated to {FirebaseAnalyticsRules.MaxParamStringLength} characters");
                            p = TrackingParam.Of(p.Name, FirebaseAnalyticsRules.Truncate(text, FirebaseAnalyticsRules.MaxParamStringLength));
                        }
                        break;
                    case TrackingParamKind.Double:
                        if (double.IsNaN(p.DoubleValue) || double.IsInfinity(p.DoubleValue))
                        {
                            _log.Warning($"'{eventName}': dropping non-finite param '{p.Name}'");
                            continue;
                        }
                        break;
                }
                result.Add(p);
            }
            return result;
        }

        public void Dispose()
        {
            _disposed = true;
            _ready = false;
            _pendingConsent = null;
            _pendingUserProperties.Clear();
            _hasPendingUserId = false;
            _pendingUserId = null;
        }
    }

    static class FirebaseAnalyticsRegistration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Register() =>
            AdapterRegistry.RegisterTrackingSink(TrackingSinkIds.Firebase, ctx => new FirebaseAnalyticsSink(ctx));
    }
}
