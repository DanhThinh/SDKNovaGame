#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GoogleMobileAds.Api;
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.Tracking;
using UnityEngine;

// Package adapters can be used only through runtime registration, without a scene reference.
[assembly: UnityEngine.Scripting.AlwaysLinkAssembly]

namespace NovaGames.Mobile.Ads.AdMob
{
    /// <summary>
    /// Adapter Google Mobile Ads (Unity plugin 11.5.x). AdMob tự đọc IABTCF do UMP ghi.
    /// GMA có thể bắn callback trên background thread: mọi callback được post về main thread trước khi đụng state.
    /// Collapsible banner: request extra `collapsible=top|bottom` (chỉ adapter này hỗ trợ).
    /// </summary>
    public sealed class AdMobAdsAdapter : IAdsAdapter
    {
        const string Op = "ads.admob";
        const string CollapsibleExtra = "collapsible";

        readonly IMainThreadDispatcher _main;
        readonly ISdkLogger _log;
        readonly Dictionary<string, FullScreenAd> _ads = new Dictionary<string, FullScreenAd>(StringComparer.Ordinal);
        readonly Dictionary<string, int> _loadGenerations = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, AdView> _views = new Dictionary<string, AdView>(StringComparer.Ordinal);

        IAdsAdapterListener? _listener;
        ConsentSnapshot? _consent;
        string[] _testDeviceIds = Array.Empty<string>();
        TaskCompletionSource<SdkResult>? _init;
        bool _initialized;
        bool _disposed;

        public AdMobAdsAdapter(ModuleContext ctx)
        {
            _main = ctx.Main;
            _log = ctx.Logs.Create("ads.admob");
        }

        public string Id => AdProviderIds.AdMob;

        public AdCapability Capabilities =>
            AdCapability.Banner | AdCapability.CollapsibleBanner | AdCapability.MRec
            | AdCapability.Interstitial | AdCapability.Rewarded | AdCapability.AppOpen;

        /// <summary>Ad GMA hết hạn không tự reload (interstitial/rewarded 1 giờ, app open 4 giờ): AdsManager áp TTL.</summary>
        public bool ManagesExpiry(AdUnit unit) => false;

        public void SetListener(IAdsAdapterListener listener) => _listener = listener;

        public void Apply(ConsentSnapshot snapshot)
        {
            if (_disposed || snapshot is null) return;
            _consent = snapshot;
            if (_initialized) _log.TryRun("SetRequestConfiguration", ApplyRequestConfiguration);
        }

        void ApplyRequestConfiguration()
        {
            var configuration = new RequestConfiguration
            {
                // Thay cho TagForUnderAgeOfConsent (obsolete từ GMA 11.x). Under-age of consent (GDPR) -> Teen;
                // game child-directed (COPPA) cần Child.
                AgeRestrictedTreatment = _consent?.IsUnderAge == true
                    ? AgeRestrictedTreatment.Teen
                    : AgeRestrictedTreatment.Unspecified,
                TestDeviceIds = new List<string>(_testDeviceIds),
            };
            MobileAds.SetRequestConfiguration(configuration);
        }

        public Task<SdkResult> InitializeAsync(AdsAdapterInitOptions options, CancellationToken ct)
        {
            if (_disposed) return Task.FromResult<SdkResult>(SdkError.Disposed(Op));
            if (_init != null) return SdkTasks.WaitAsync(_init.Task, Op + ".init", ct);

            _init = new TaskCompletionSource<SdkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _testDeviceIds = options.IsDevelopment
                ? new List<string>(options.For(AdsProvider.AdMob).TestDeviceIds).ToArray()
                : Array.Empty<string>();
            try
            {
                ApplyRequestConfiguration();
                MobileAds.Initialize(_ => _main.Post(() =>
                {
                    if (_disposed) return;
                    _initialized = true;
                    _log.Info("Google Mobile Ads initialized");
                    _init.TrySetResult(SdkResult.Ok);
                }));
            }
            catch (Exception e)
            {
                _init.TrySetResult(SdkError.FromException(Op + ".init", e, Id));
            }
            return SdkTasks.WaitAsync(_init.Task, Op + ".init", ct);
        }

        // ---------------- Full-screen ----------------

        public void Load(AdUnit unit)
        {
            int generation = NextLoadGeneration(unit.Key);
            var request = new AdRequest();
            switch (unit.Format)
            {
                case AdFormat.Interstitial:
                    InterstitialAd.Load(unit.AdUnitId, request, (ad, error) => OnFullScreenLoaded(unit, generation, ad, error, a => new InterstitialEntry(a)));
                    break;
                case AdFormat.Rewarded:
                    RewardedAd.Load(unit.AdUnitId, request, (ad, error) => OnFullScreenLoaded(unit, generation, ad, error, a => new RewardedEntry(a)));
                    break;
                case AdFormat.AppOpen:
                    AppOpenAd.Load(unit.AdUnitId, request, (ad, error) => OnFullScreenLoaded(unit, generation, ad, error, a => new AppOpenEntry(a)));
                    break;
                default:
                    throw new NotSupportedException(unit.Format + " is not a full-screen format");
            }
        }

        void OnFullScreenLoaded<TAd>(AdUnit unit, int generation, TAd? ad, LoadAdError? error,
                                     Func<TAd, FullScreenAd> wrap) where TAd : class
        {
            // Đọc thông tin lỗi ngay trên thread callback, rồi mới post.
            var failure = ad is null || error != null ? MapError(error) : null;
            _main.Post(() =>
            {
                if (_disposed || _listener is null || !IsCurrentLoad(unit.Key, generation))
                {
                    if (ad != null) _log.TryRun("Destroy", () => wrap(ad).Destroy());
                    return;
                }
                if (failure != null)
                {
                    _listener.OnLoadFailed(unit, failure);
                    return;
                }

                if (_ads.TryGetValue(unit.Key, out var previous)) _log.TryRun("Destroy", previous.Destroy);
                var entry = wrap(ad!);
                _ads[unit.Key] = entry;
                RegisterEvents(unit, entry);
                _listener.OnLoaded(unit);
            });
        }

        void RegisterEvents(AdUnit unit, FullScreenAd entry)
        {
            entry.Opened += () => _main.Post(() => _listener?.OnDisplayed(unit, entry.Operation));
            entry.Closed += () => _main.Post(() =>
            {
                _listener?.OnClosed(unit, entry.Operation);
                Release(unit, entry);
            });
            entry.Failed += error =>
            {
                var message = SafeMessage(error);
                _main.Post(() =>
                {
                    _listener?.OnDisplayFailed(unit, entry.Operation, message);
                    Release(unit, entry);
                });
            };
            entry.Clicked += () => _main.Post(() => _listener?.OnClicked(unit, entry.Operation));
            entry.Paid += value =>
            {
                var paid = MapPaid(value, entry.ResponseInfo);
                _main.Post(() => _listener?.OnPaid(unit, entry.Operation, paid));
            };
        }

        // Ad full-screen của GMA dùng một lần: sau close/fail thì destroy.
        void Release(AdUnit unit, FullScreenAd entry)
        {
            if (_ads.TryGetValue(unit.Key, out var current) && current == entry) _ads.Remove(unit.Key);
            _log.TryRun("Destroy", entry.Destroy);
        }

        public bool IsReady(AdUnit unit) => _ads.TryGetValue(unit.Key, out var entry) && !entry.Shown && entry.CanShowAd();

        public void Show(AdUnit unit, string placementId, Guid showOperationId)
        {
            if (!_ads.TryGetValue(unit.Key, out var entry))
            {
                // Không throw: báo display fail qua callback để AdsManager nhả lock.
                _main.Post(() => _listener?.OnDisplayFailed(unit, showOperationId, "No loaded ad for " + unit));
                return;
            }
            entry.Operation = showOperationId;
            entry.Shown = true;
            if (entry is RewardedEntry rewarded)
            {
                rewarded.Show(reward =>
                {
                    var value = new AdReward(reward?.Type ?? string.Empty, reward?.Amount ?? 0);
                    _main.Post(() => _listener?.OnRewarded(unit, showOperationId, value));
                });
            }
            else
            {
                entry.Show();
            }
        }

        public void Discard(AdUnit unit)
        {
            // API load của GMA không hủy được: tăng generation để callback tới sau bị coi là cũ và bỏ qua.
            NextLoadGeneration(unit.Key);
            if (!_ads.TryGetValue(unit.Key, out var entry)) return;
            _ads.Remove(unit.Key);
            entry.Destroy();
        }

        int NextLoadGeneration(string unitKey)
        {
            int next = _loadGenerations.TryGetValue(unitKey, out var current) ? unchecked(current + 1) : 1;
            _loadGenerations[unitKey] = next;
            return next;
        }

        bool IsCurrentLoad(string unitKey, int generation) =>
            _loadGenerations.TryGetValue(unitKey, out var current) && current == generation;

        // ---------------- Banner / MREC ----------------

        public void ShowAdView(AdUnit unit, AdViewRequest request)
        {
            if (_views.TryGetValue(unit.Key, out var existing))
            {
                if (existing.Request == request)
                {
                    existing.View.Show();
                    return;
                }
                DestroyAdView(unit);
            }

            AdSize size;
            AdPosition position = AdPosition.Center;
            if (unit.Format == AdFormat.Banner)
            {
                size = request.Size == BannerSize.AdaptiveAnchored
                    ? AdSize.GetCurrentOrientationAnchoredAdaptiveBannerAdSizeWithWidth(AdSize.FullWidth)
                    : AdSize.Banner;
                position = request.BannerPosition == BannerPosition.Top ? AdPosition.Top : AdPosition.Bottom;
            }
            else if (unit.Format == AdFormat.MRec)
            {
                size = AdSize.MediumRectangle;
                position = request.MrecPosition switch
                {
                    MrecPosition.TopCenter => AdPosition.Top,
                    MrecPosition.BottomCenter => AdPosition.Bottom,
                    _ => AdPosition.Center,
                };
            }
            else
            {
                throw new NotSupportedException(unit.Format + " is not an ad view format");
            }

            BannerView view;
            var point = request.MrecPixelPosition;
            if (point != null)
            {
                // AdViewRequest theo pixel; GMA nhận x/y theo dp (Android) / point (iOS) rồi tự nhân density.
                float scale = SafeDeviceScale();
                view = new BannerView(unit.AdUnitId, size, (int)Math.Round(point.X / scale), (int)Math.Round(point.Y / scale));
            }
            else
            {
                view = new BannerView(unit.AdUnitId, size, position);
            }
            var entry = new AdView(view, request);
            _views[unit.Key] = entry;

            view.OnBannerAdLoaded += () => _main.Post(() =>
            {
                if (!IsCurrent(unit, entry)) return;
                _listener?.OnLoaded(unit);
                if (unit.Format == AdFormat.Banner) _listener?.OnAdViewLayoutChanged(unit, LayoutOf(view));
                else if (unit.Format == AdFormat.MRec) _listener?.OnMrecSizeChanged(unit, MrecSizeOf(view));
            });
            view.OnBannerAdLoadFailed += error =>
            {
                var failure = MapError(error);
                _main.Post(() =>
                {
                    if (IsCurrent(unit, entry)) _listener?.OnLoadFailed(unit, failure);
                });
            };
            view.OnAdPaid += value =>
            {
                var paid = MapPaid(value, SafeResponseInfo(view));
                _main.Post(() =>
                {
                    if (IsCurrent(unit, entry)) _listener?.OnPaid(unit, Guid.Empty, paid);
                });
            };
            view.OnAdClicked += () => _main.Post(() =>
            {
                if (IsCurrent(unit, entry)) _listener?.OnClicked(unit, Guid.Empty);
            });

            var adRequest = new AdRequest();
            if (request.Collapse != CollapseDirection.None)
                adRequest.Extras[CollapsibleExtra] = request.Collapse == CollapseDirection.Top ? "top" : "bottom";
            view.LoadAd(adRequest);
        }

        float SafeDeviceScale()
        {
            try
            {
                float scale = MobileAds.Utils.GetDeviceScale();
                return scale > 0 ? scale : 1f;
            }
            catch (Exception e)
            {
                _log.Warning("Reading device scale failed", e);
                return 1f;
            }
        }

        bool IsCurrent(AdUnit unit, AdView entry) =>
            !_disposed && _views.TryGetValue(unit.Key, out var current) && current == entry;

        BannerLayout LayoutOf(BannerView view)
        {
            try
            {
                float px = view.GetHeightInPixels();
                float scale = MobileAds.Utils.GetDeviceScale();
                bool collapsible = view.IsCollapsible();
                // Collapsible banner hiển thị ở trạng thái mở rộng lúc vừa load; GMA không báo sự kiện thu gọn.
                return new BannerLayout(scale > 0 ? px / scale : px, px, collapsible, collapsible);
            }
            catch (Exception e)
            {
                _log.Warning("Reading banner layout failed", e);
                return BannerLayout.None;
            }
        }

        MrecSize MrecSizeOf(BannerView view)
        {
            try
            {
                float widthPx = view.GetWidthInPixels();
                float heightPx = view.GetHeightInPixels();
                float scale = MobileAds.Utils.GetDeviceScale();
                return new MrecSize(
                    scale > 0 ? widthPx / scale : MrecSize.StandardWidthDp,
                    scale > 0 ? heightPx / scale : MrecSize.StandardHeightDp,
                    widthPx,
                    heightPx);
            }
            catch (Exception e)
            {
                _log.Warning("Reading MREC size failed", e);
                return MrecSize.None;
            }
        }

        public void HideAdView(AdUnit unit)
        {
            if (_views.TryGetValue(unit.Key, out var entry)) entry.View.Hide();
        }

        public void DestroyAdView(AdUnit unit)
        {
            if (!_views.TryGetValue(unit.Key, out var entry)) return;
            _views.Remove(unit.Key);
            entry.View.Destroy();
        }

        public void OpenDebugger() =>
            MobileAds.OpenAdInspector(error =>
            {
                if (error != null) _main.Post(() => _log.Warning("Ad Inspector failed: " + SafeMessage(error)));
            });

        // ---------------- Mapping ----------------

        static AdLoadError MapError(LoadAdError? error)
        {
            if (error is null) return new AdLoadError(AdLoadFailure.Provider, "Load returned no ad");
            int code = error.GetCode();
            // https://developers.google.com/admob/android/reference/com/google/android/gms/ads/AdRequest#constants
            var kind = code switch
            {
                3 => AdLoadFailure.NoFill,
                2 => AdLoadFailure.Network,
                _ => AdLoadFailure.Provider,
            };
            return new AdLoadError(kind, error.GetMessage() ?? "code " + code, code);
        }

        static AdPaidValue MapPaid(AdValue value, ResponseInfo? response)
        {
            var precision = value.Precision switch
            {
                AdValue.PrecisionType.Precise => RevenuePrecision.Precise,
                AdValue.PrecisionType.Estimated => RevenuePrecision.Estimated,
                AdValue.PrecisionType.PublisherProvided => RevenuePrecision.PublisherDefined,
                _ => RevenuePrecision.Unknown,
            };
            string network = "admob";
            string? responseId = null;
            if (response != null)
            {
                var adapter = response.GetLoadedAdapterResponseInfo();
                if (adapter != null && !string.IsNullOrEmpty(adapter.AdSourceName)) network = adapter.AdSourceName;
                responseId = response.GetResponseId();
            }
            // AdValue.Value tính bằng micros.
            return new AdPaidValue(value.Value / 1_000_000d, string.IsNullOrEmpty(value.CurrencyCode) ? "USD" : value.CurrencyCode,
                precision, network, string.IsNullOrEmpty(responseId) ? null : responseId);
        }

        static ResponseInfo? SafeResponseInfo(BannerView view)
        {
            try
            {
                return view.GetResponseInfo();
            }
            catch (Exception)
            {
                // Response info không có thì revenue vẫn gửi với network mặc định.
                return null;
            }
        }

        static string SafeMessage(AdError? error)
        {
            if (error is null) return "unknown error";
            try
            {
                return error.GetCode() + ": " + error.GetMessage();
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _ads.Values) _log.TryRun("Destroy", entry.Destroy);
            _ads.Clear();
            _loadGenerations.Clear();
            foreach (var entry in _views.Values) _log.TryRun("Destroy", entry.View.Destroy);
            _views.Clear();
            _init?.TrySetResult(SdkError.Disposed(Op));
            _listener = null;
        }

        // ---------------- Wrappers ----------------

        sealed class AdView
        {
            public AdView(BannerView view, AdViewRequest request)
            {
                View = view;
                Request = request;
            }

            public BannerView View { get; }
            public AdViewRequest Request { get; }
        }

        // Ba loại ad full-screen của GMA không có base chung: bọc lại để xử lý đồng nhất.
        abstract class FullScreenAd
        {
            public Guid Operation;
            public bool Shown;
            public event Action? Opened;
            public event Action? Closed;
            public event Action<AdError>? Failed;
            public event Action? Clicked;
            public event Action<AdValue>? Paid;

            public abstract ResponseInfo? ResponseInfo { get; }
            public abstract bool CanShowAd();
            public abstract void Show();
            public abstract void Destroy();

            protected void RaiseOpened() => Opened?.Invoke();
            protected void RaiseClosed() => Closed?.Invoke();
            protected void RaiseFailed(AdError error) => Failed?.Invoke(error);
            protected void RaiseClicked() => Clicked?.Invoke();
            protected void RaisePaid(AdValue value) => Paid?.Invoke(value);
        }

        sealed class InterstitialEntry : FullScreenAd
        {
            readonly InterstitialAd _ad;

            public InterstitialEntry(InterstitialAd ad)
            {
                _ad = ad;
                ad.OnAdFullScreenContentOpened += RaiseOpened;
                ad.OnAdFullScreenContentClosed += RaiseClosed;
                ad.OnAdFullScreenContentFailed += RaiseFailed;
                ad.OnAdClicked += RaiseClicked;
                ad.OnAdPaid += RaisePaid;
            }

            public override ResponseInfo? ResponseInfo => _ad.GetResponseInfo();
            public override bool CanShowAd() => _ad.CanShowAd();
            public override void Show() => _ad.Show();
            public override void Destroy() => _ad.Destroy();
        }

        sealed class RewardedEntry : FullScreenAd
        {
            readonly RewardedAd _ad;

            public RewardedEntry(RewardedAd ad)
            {
                _ad = ad;
                ad.OnAdFullScreenContentOpened += RaiseOpened;
                ad.OnAdFullScreenContentClosed += RaiseClosed;
                ad.OnAdFullScreenContentFailed += RaiseFailed;
                ad.OnAdClicked += RaiseClicked;
                ad.OnAdPaid += RaisePaid;
            }

            public override ResponseInfo? ResponseInfo => _ad.GetResponseInfo();
            public override bool CanShowAd() => _ad.CanShowAd();
            public override void Show() => throw new InvalidOperationException("Rewarded ads must be shown with a reward callback");
            public void Show(Action<Reward> onReward) => _ad.Show(onReward);
            public override void Destroy() => _ad.Destroy();
        }

        sealed class AppOpenEntry : FullScreenAd
        {
            readonly AppOpenAd _ad;

            public AppOpenEntry(AppOpenAd ad)
            {
                _ad = ad;
                ad.OnAdFullScreenContentOpened += RaiseOpened;
                ad.OnAdFullScreenContentClosed += RaiseClosed;
                ad.OnAdFullScreenContentFailed += RaiseFailed;
                ad.OnAdClicked += RaiseClicked;
                ad.OnAdPaid += RaisePaid;
            }

            public override ResponseInfo? ResponseInfo => _ad.GetResponseInfo();
            public override bool CanShowAd() => _ad.CanShowAd();
            public override void Show() => _ad.Show();
            public override void Destroy() => _ad.Destroy();
        }
    }

    static class AdMobRegistration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Register() => AdapterRegistry.RegisterAds(AdProviderIds.AdMob, ctx => new AdMobAdsAdapter(ctx));
    }
}
