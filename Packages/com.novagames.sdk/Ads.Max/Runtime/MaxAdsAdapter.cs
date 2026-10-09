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

namespace NovaGames.Mobile.Ads.Max
{
    /// <summary>
    /// Adapter AppLovin MAX (plugin 8.6.x). SDK key đặt trong AppLovin Integration Manager.
    /// Lifecycle vendor + normalize callback; state/retry/capping thuộc AdsManager.
    /// Bid floor test là logic riêng của MAX nên nằm hết ở đây: AdsManager chỉ thấy unit main,
    /// MaxFloorCascade tự load tuần tự các unit floor, đặt B2B/auto-retry, chọn tier để show.
    /// MAX không có collapsible banner: Capabilities không gồm CollapsibleBanner.
    /// Một số event MAX chạy trên background thread (revenue của ad full-screen có keepInBackground; mọi event nếu game
    /// đặt MaxSdkBase.InvokeEventsOnUnityMainThread = false): handler chuyển về main thread trước khi đụng state.
    /// </summary>
    public sealed class MaxAdsAdapter : IAdsAdapter, IMaxFullScreenApi
    {
        const string Op = "ads.max";

        readonly ISdkLogger _log;
        readonly ModuleContext _ctx;
        // Theo ad unit id; unit floor là bản sao của unit main (cùng Key) với id riêng.
        readonly Dictionary<string, AdUnit> _units = new Dictionary<string, AdUnit>(StringComparer.Ordinal);
        // Cascade theo id của unit main (AdsManager gọi Load/Show bằng unit này) và theo id của mọi tier (route callback).
        readonly Dictionary<string, MaxFloorCascade> _cascades = new Dictionary<string, MaxFloorCascade>(StringComparer.Ordinal);
        readonly Dictionary<string, MaxFloorCascade> _tierCascades = new Dictionary<string, MaxFloorCascade>(StringComparer.Ordinal);
        IReadOnlyList<AdUnit> _autoRetryOff = Array.Empty<AdUnit>();
        // Show operation gần nhất theo ad unit id; giữ tới lần show sau để paid/reward callback trễ vẫn gắn đúng operation.
        readonly Dictionary<string, Guid> _operations = new Dictionary<string, Guid>(StringComparer.Ordinal);
        readonly Dictionary<string, AdViewRequest> _views = new Dictionary<string, AdViewRequest>(StringComparer.Ordinal);

        IAdsAdapterListener? _listener;
        ConsentSnapshot? _consent;
        TaskCompletionSource<SdkResult>? _init;
        // null = chưa chuẩn bị init (plan cascade, callback, extra parameter); giữ lại cho lần init lại sau lỗi.
        string[]? _initAdUnitIds;
        bool _subscribed;
        bool _disposed;

        public MaxAdsAdapter(ModuleContext ctx)
        {
            _ctx = ctx;
            _log = ctx.Logs.Create("ads.max");
        }

        public string Id => AdProviderIds.Max;

        public AdCapability Capabilities =>
            AdCapability.Banner | AdCapability.MRec | AdCapability.Interstitial | AdCapability.Rewarded | AdCapability.AppOpen;

        /// <summary>MAX tự reload ad hết hạn (OnExpiredAdReloadedEvent) và không hủy được ad đã load.</summary>
        public bool ManagesExpiry(AdUnit unit) => true;

        public void SetListener(IAdsAdapterListener listener) => _listener = listener;

        /// <summary>Phải gọi trước InitializeSdk; MAX cho phép cập nhật lại sau init.</summary>
        public void Apply(ConsentSnapshot snapshot)
        {
            if (_disposed || snapshot is null) return;
            _consent = snapshot;
            _log.TryRun("Apply consent", () =>
            {
                MaxSdk.SetHasUserConsent(HasUserConsent(snapshot));
                MaxSdk.SetDoNotSell(snapshot.UsDoNotSell);
            });
        }

        // GDPR: cần đồng ý ad storage + personalization. Ngoài GDPR: CanRequestAds là đủ.
        internal static bool HasUserConsent(ConsentSnapshot snapshot) =>
            snapshot.Jurisdiction == Jurisdiction.Gdpr
                ? snapshot.AdStorage == ConsentState.Granted && snapshot.AdPersonalization == ConsentState.Granted
                : snapshot.CanRequestAds;

        public Task<SdkResult> InitializeAsync(AdsAdapterInitOptions options, CancellationToken ct)
        {
            if (_disposed) return Task.FromResult<SdkResult>(SdkError.Disposed(Op));
            // Đang init hoặc đã init xong: dùng chung. Lần trước lỗi: AdsManager gọi lại để thử init lần nữa.
            var previous = _init?.Task;
            if (previous != null && !(previous.IsCompleted && !previous.Result.IsSuccess))
                return SdkTasks.WaitAsync(previous, Op + ".init", ct);

            // Under-age: không init MAX (chính sách privacy của SDK).
            if (_consent?.IsUnderAge == true)
                return Task.FromResult<SdkResult>(new SdkError(Op + ".under_age", SdkErrorCategory.Blocked,
                    "User is under age; MAX is not initialized", false, Id));

            var init = new TaskCompletionSource<SdkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _init = init;
            try
            {
                // Plan cascade + extra parameter chỉ chuẩn bị một lần (Remote Config lúc init đầu); lần thử lại chỉ gọi lại InitializeSdk.
                _initAdUnitIds ??= Prepare(options);
                MaxSdk.InitializeSdk(_initAdUnitIds);
            }
            catch (Exception e)
            {
                init.TrySetResult(SdkError.FromException(Op + ".init", e, Id));
            }
            return SdkTasks.WaitAsync(init.Task, Op + ".init", ct);
        }

        // Trả danh sách ad unit cho selective init.
        string[] Prepare(AdsAdapterInitOptions options)
        {
            foreach (var cascade in _cascades.Values) cascade.Dispose();
            _cascades.Clear();
            _tierCascades.Clear();

            // Build có thể dùng cả AdMob: MAX chỉ init và quản lý unit của mình.
            var units = options.UnitsOf(AdsProvider.Max);
            var provider = options.For(AdsProvider.Max);
            foreach (var unit in units) _units[unit.AdUnitId] = unit;
            var settings = provider.Settings as MaxAdsSettings;
            var plan = MaxFloorPlan.Create(units, settings, options.RemoteConfig, _log);
            foreach (var (unit, tiers) in plan.Cascades)
            {
                var cascade = new MaxFloorCascade(tiers, this, _ctx.Clock, _ctx.Scheduler, _log, settings!.FloorTierLoadTimeout,
                    OnCascadeLoaded, OnCascadeLoadFailed);
                _cascades[unit.AdUnitId] = cascade;
                foreach (var tier in tiers)
                {
                    _units[tier.AdUnitId] = tier;
                    _tierCascades[tier.AdUnitId] = cascade;
                }
            }
            _autoRetryOff = plan.DisableAutoRetryUnits;

            Subscribe();
            MaxSdk.SetVerboseLogging(options.IsDevelopment && provider.VerboseLogging);
            if (options.IsDevelopment && provider.TestDeviceIds.Count > 0)
                MaxSdk.SetTestDeviceAdvertisingIdentifiers(new List<string>(provider.TestDeviceIds).ToArray());
            // MAX chỉ nhận disable_b2b_ad_unit_ids khi đặt trước InitializeSdk (không đổi được sau init).
            if (plan.DisableBackToBackIds.Length > 0)
                MaxSdk.SetExtraParameter(MaxFloorPlan.DisableBackToBackKey, plan.DisableBackToBackIds);
            return plan.InitAdUnitIds;
        }

        void OnSdkInitialized(MaxSdkBase.SdkConfiguration configuration) => OnMain(() =>
        {
            if (_disposed) return;
            if (configuration.IsSuccessfullyInitialized)
            {
                _log.Info("MAX " + MaxSdk.Version + " initialized" + (configuration.IsTestModeEnabled ? " (test mode)" : ""));
                // Sau init (gọi sớm hơn sẽ crash), trước lần load đầu: AdsManager chỉ load sau khi init trả Ok.
                foreach (var unit in _autoRetryOff)
                {
                    _log.TryRun("Disable auto retries " + unit.AdUnitId, () =>
                    {
                        if (unit.Format == AdFormat.Rewarded)
                            MaxSdk.SetRewardedAdExtraParameter(unit.AdUnitId, MaxFloorPlan.DisableAutoRetriesKey, "true");
                        else
                            MaxSdk.SetInterstitialExtraParameter(unit.AdUnitId, MaxFloorPlan.DisableAutoRetriesKey, "true");
                    });
                }
                _init?.TrySetResult(SdkResult.Ok);
            }
            else
            {
                _init?.TrySetResult(new SdkError(Op + ".init_failed", SdkErrorCategory.Provider, "MAX SDK failed to initialize", true, Id));
            }
        });

        // ---------------- Full-screen ----------------

        public void Load(AdUnit unit)
        {
            if (_cascades.TryGetValue(unit.AdUnitId, out var cascade)) cascade.Load();
            else LoadFullScreen(unit);
        }

        public bool IsReady(AdUnit unit) =>
            _cascades.TryGetValue(unit.AdUnitId, out var cascade) ? cascade.IsReady : IsFullScreenReady(unit);

        public void Show(AdUnit unit, string placementId, Guid showOperationId)
        {
            // Cascade: show tier giá cao nhất đang có ad; callback của tier đó mang AdUnitId riêng (revenue theo tier).
            var target = _cascades.TryGetValue(unit.AdUnitId, out var cascade) ? cascade.BeginShow() : unit;
            _operations[target.AdUnitId] = showOperationId;
            switch (target.Format)
            {
                case AdFormat.Interstitial: MaxSdk.ShowInterstitial(target.AdUnitId, placementId); break;
                case AdFormat.Rewarded: MaxSdk.ShowRewardedAd(target.AdUnitId, placementId); break;
                case AdFormat.AppOpen: MaxSdk.ShowAppOpenAd(target.AdUnitId, placementId); break;
                default: throw new NotSupportedException(target.Format + " is not a full-screen format");
            }
        }

        /// <summary>
        /// MAX không có API hủy ad full-screen đã load; ad hết hạn được SDK tự reload (OnExpiredAdReloadedEvent).
        /// Cascade chỉ dừng lượt load đang chạy.
        /// </summary>
        public void Discard(AdUnit unit)
        {
            if (_cascades.TryGetValue(unit.AdUnitId, out var cascade)) cascade.Discard();
        }

        void IMaxFullScreenApi.Load(AdUnit unit) => LoadFullScreen(unit);
        bool IMaxFullScreenApi.IsReady(AdUnit unit) => IsFullScreenReady(unit);

        static void LoadFullScreen(AdUnit unit)
        {
            switch (unit.Format)
            {
                case AdFormat.Interstitial: MaxSdk.LoadInterstitial(unit.AdUnitId); break;
                case AdFormat.Rewarded: MaxSdk.LoadRewardedAd(unit.AdUnitId); break;
                case AdFormat.AppOpen: MaxSdk.LoadAppOpenAd(unit.AdUnitId); break;
                default: throw new NotSupportedException(unit.Format + " is not a full-screen format");
            }
        }

        static bool IsFullScreenReady(AdUnit unit) => unit.Format switch
        {
            AdFormat.Interstitial => MaxSdk.IsInterstitialReady(unit.AdUnitId),
            AdFormat.Rewarded => MaxSdk.IsRewardedAdReady(unit.AdUnitId),
            AdFormat.AppOpen => MaxSdk.IsAppOpenAdReady(unit.AdUnitId),
            _ => false,
        };

        void OnCascadeLoaded(AdUnit unit)
        {
            if (!_disposed) _listener?.OnLoaded(unit);
        }

        void OnCascadeLoadFailed(AdUnit unit, AdLoadError error)
        {
            if (!_disposed) _listener?.OnLoadFailed(unit, error);
        }

        // ---------------- Banner / MREC ----------------

        public void ShowAdView(AdUnit unit, AdViewRequest request)
        {
            var id = unit.AdUnitId;
            _units[id] = unit;
            if (_views.TryGetValue(id, out var existing))
            {
                if (existing == request)
                {
                    if (unit.Format == AdFormat.Banner) MaxSdk.ShowBanner(id);
                    else MaxSdk.ShowMRec(id);
                    return;
                }
                DestroyAdView(unit);
            }

            if (unit.Format == AdFormat.Banner)
            {
                var position = request.BannerPosition == BannerPosition.Top
                    ? MaxSdkBase.AdViewPosition.TopCenter
                    : MaxSdkBase.AdViewPosition.BottomCenter;
                MaxSdk.CreateBanner(id, new MaxSdkBase.AdViewConfiguration(position)
                {
                    IsAdaptive = request.Size == BannerSize.AdaptiveAnchored,
                });
                MaxSdk.SetBannerPlacement(id, request.PlacementId);
                MaxSdk.ShowBanner(id);
            }
            else if (unit.Format == AdFormat.MRec)
            {
                MaxSdkBase.AdViewConfiguration configuration;
                if (request.MrecPixelPosition != null)
                {
                    float density = SafeScreenDensity();
                    configuration = new MaxSdkBase.AdViewConfiguration(
                        request.MrecPixelPosition.X / density, request.MrecPixelPosition.Y / density);
                }
                else
                {
                    var position = request.MrecPosition switch
                    {
                        MrecPosition.TopCenter => MaxSdkBase.AdViewPosition.TopCenter,
                        MrecPosition.BottomCenter => MaxSdkBase.AdViewPosition.BottomCenter,
                        _ => MaxSdkBase.AdViewPosition.Centered,
                    };
                    configuration = new MaxSdkBase.AdViewConfiguration(position);
                }
                MaxSdk.CreateMRec(id, configuration);
                MaxSdk.SetMRecPlacement(id, request.PlacementId);
                MaxSdk.ShowMRec(id);
            }
            else
            {
                throw new NotSupportedException(unit.Format + " is not an ad view format");
            }
            _views[id] = request;
        }

        public void HideAdView(AdUnit unit)
        {
            if (!_views.ContainsKey(unit.AdUnitId)) return;
            if (unit.Format == AdFormat.Banner) MaxSdk.HideBanner(unit.AdUnitId);
            else MaxSdk.HideMRec(unit.AdUnitId);
        }

        public void DestroyAdView(AdUnit unit)
        {
            if (!_views.Remove(unit.AdUnitId)) return;
            if (unit.Format == AdFormat.Banner) MaxSdk.DestroyBanner(unit.AdUnitId);
            else MaxSdk.DestroyMRec(unit.AdUnitId);
        }

        public void OpenDebugger() => MaxSdk.ShowMediationDebugger();

        // ---------------- Callbacks ----------------

        void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            MaxSdkCallbacks.OnSdkInitializedEvent += OnSdkInitialized;

            MaxSdkCallbacks.Interstitial.OnAdLoadedEvent += OnLoaded;
            MaxSdkCallbacks.Interstitial.OnAdLoadFailedEvent += OnLoadFailed;
            MaxSdkCallbacks.Interstitial.OnAdDisplayedEvent += OnDisplayed;
            MaxSdkCallbacks.Interstitial.OnAdDisplayFailedEvent += OnDisplayFailed;
            MaxSdkCallbacks.Interstitial.OnAdHiddenEvent += OnHidden;
            MaxSdkCallbacks.Interstitial.OnAdClickedEvent += OnClicked;
            MaxSdkCallbacks.Interstitial.OnAdRevenuePaidEvent += OnRevenuePaid;

            MaxSdkCallbacks.Rewarded.OnAdLoadedEvent += OnLoaded;
            MaxSdkCallbacks.Rewarded.OnAdLoadFailedEvent += OnLoadFailed;
            MaxSdkCallbacks.Rewarded.OnAdDisplayedEvent += OnDisplayed;
            MaxSdkCallbacks.Rewarded.OnAdDisplayFailedEvent += OnDisplayFailed;
            MaxSdkCallbacks.Rewarded.OnAdHiddenEvent += OnHidden;
            MaxSdkCallbacks.Rewarded.OnAdClickedEvent += OnClicked;
            MaxSdkCallbacks.Rewarded.OnAdRevenuePaidEvent += OnRevenuePaid;
            MaxSdkCallbacks.Rewarded.OnAdReceivedRewardEvent += OnReceivedReward;

            MaxSdkCallbacks.AppOpen.OnAdLoadedEvent += OnLoaded;
            MaxSdkCallbacks.AppOpen.OnAdLoadFailedEvent += OnLoadFailed;
            MaxSdkCallbacks.AppOpen.OnAdDisplayedEvent += OnDisplayed;
            MaxSdkCallbacks.AppOpen.OnAdDisplayFailedEvent += OnDisplayFailed;
            MaxSdkCallbacks.AppOpen.OnAdHiddenEvent += OnHidden;
            MaxSdkCallbacks.AppOpen.OnAdClickedEvent += OnClicked;
            MaxSdkCallbacks.AppOpen.OnAdRevenuePaidEvent += OnRevenuePaid;

            MaxSdkCallbacks.Banner.OnAdLoadedEvent += OnAdViewLoaded;
            MaxSdkCallbacks.Banner.OnAdLoadFailedEvent += OnLoadFailed;
            MaxSdkCallbacks.Banner.OnAdClickedEvent += OnClicked;
            MaxSdkCallbacks.Banner.OnAdRevenuePaidEvent += OnRevenuePaid;
            MaxSdkCallbacks.Banner.OnAdExpandedEvent += OnAdViewExpanded;
            MaxSdkCallbacks.Banner.OnAdCollapsedEvent += OnAdViewCollapsed;

            MaxSdkCallbacks.MRec.OnAdLoadedEvent += OnAdViewLoaded;
            MaxSdkCallbacks.MRec.OnAdLoadFailedEvent += OnLoadFailed;
            MaxSdkCallbacks.MRec.OnAdClickedEvent += OnClicked;
            MaxSdkCallbacks.MRec.OnAdRevenuePaidEvent += OnRevenuePaid;
        }

        void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            MaxSdkCallbacks.OnSdkInitializedEvent -= OnSdkInitialized;

            MaxSdkCallbacks.Interstitial.OnAdLoadedEvent -= OnLoaded;
            MaxSdkCallbacks.Interstitial.OnAdLoadFailedEvent -= OnLoadFailed;
            MaxSdkCallbacks.Interstitial.OnAdDisplayedEvent -= OnDisplayed;
            MaxSdkCallbacks.Interstitial.OnAdDisplayFailedEvent -= OnDisplayFailed;
            MaxSdkCallbacks.Interstitial.OnAdHiddenEvent -= OnHidden;
            MaxSdkCallbacks.Interstitial.OnAdClickedEvent -= OnClicked;
            MaxSdkCallbacks.Interstitial.OnAdRevenuePaidEvent -= OnRevenuePaid;

            MaxSdkCallbacks.Rewarded.OnAdLoadedEvent -= OnLoaded;
            MaxSdkCallbacks.Rewarded.OnAdLoadFailedEvent -= OnLoadFailed;
            MaxSdkCallbacks.Rewarded.OnAdDisplayedEvent -= OnDisplayed;
            MaxSdkCallbacks.Rewarded.OnAdDisplayFailedEvent -= OnDisplayFailed;
            MaxSdkCallbacks.Rewarded.OnAdHiddenEvent -= OnHidden;
            MaxSdkCallbacks.Rewarded.OnAdClickedEvent -= OnClicked;
            MaxSdkCallbacks.Rewarded.OnAdRevenuePaidEvent -= OnRevenuePaid;
            MaxSdkCallbacks.Rewarded.OnAdReceivedRewardEvent -= OnReceivedReward;

            MaxSdkCallbacks.AppOpen.OnAdLoadedEvent -= OnLoaded;
            MaxSdkCallbacks.AppOpen.OnAdLoadFailedEvent -= OnLoadFailed;
            MaxSdkCallbacks.AppOpen.OnAdDisplayedEvent -= OnDisplayed;
            MaxSdkCallbacks.AppOpen.OnAdDisplayFailedEvent -= OnDisplayFailed;
            MaxSdkCallbacks.AppOpen.OnAdHiddenEvent -= OnHidden;
            MaxSdkCallbacks.AppOpen.OnAdClickedEvent -= OnClicked;
            MaxSdkCallbacks.AppOpen.OnAdRevenuePaidEvent -= OnRevenuePaid;

            MaxSdkCallbacks.Banner.OnAdLoadedEvent -= OnAdViewLoaded;
            MaxSdkCallbacks.Banner.OnAdLoadFailedEvent -= OnLoadFailed;
            MaxSdkCallbacks.Banner.OnAdClickedEvent -= OnClicked;
            MaxSdkCallbacks.Banner.OnAdRevenuePaidEvent -= OnRevenuePaid;
            MaxSdkCallbacks.Banner.OnAdExpandedEvent -= OnAdViewExpanded;
            MaxSdkCallbacks.Banner.OnAdCollapsedEvent -= OnAdViewCollapsed;

            MaxSdkCallbacks.MRec.OnAdLoadedEvent -= OnAdViewLoaded;
            MaxSdkCallbacks.MRec.OnAdLoadFailedEvent -= OnLoadFailed;
            MaxSdkCallbacks.MRec.OnAdClickedEvent -= OnClicked;
            MaxSdkCallbacks.MRec.OnAdRevenuePaidEvent -= OnRevenuePaid;
        }

        bool TryResolve(string adUnitId, out AdUnit unit, out IAdsAdapterListener listener)
        {
            listener = _listener!;
            if (_disposed || _listener is null || !_units.TryGetValue(adUnitId, out unit!))
            {
                unit = null!;
                return false;
            }
            return true;
        }

        Guid OperationOf(string adUnitId) => _operations.TryGetValue(adUnitId, out var id) ? id : Guid.Empty;

        // Event trên main thread chạy ngay (giữ thứ tự); event trên background thread được post về main.
        void OnMain(Action action)
        {
            if (_ctx.Main.IsMainThread) action();
            else _ctx.Main.Post(action);
        }

        void OnLoaded(string adUnitId, MaxSdkBase.AdInfo info) => OnMain(() =>
        {
            if (_disposed) return;
            // Tier của cascade: cascade quyết định có báo AdsManager hay không (một kết quả mỗi lượt).
            if (_tierCascades.TryGetValue(adUnitId, out var cascade)) cascade.OnTierLoaded(adUnitId);
            else if (TryResolve(adUnitId, out var unit, out var listener)) listener.OnLoaded(unit);
        });

        void OnLoadFailed(string adUnitId, MaxSdkBase.ErrorInfo error) => OnMain(() =>
        {
            if (_disposed) return;
            if (_tierCascades.TryGetValue(adUnitId, out var cascade)) cascade.OnTierLoadFailed(adUnitId, MapError(error));
            else if (TryResolve(adUnitId, out var unit, out var listener)) listener.OnLoadFailed(unit, MapError(error));
        });

        void OnDisplayed(string adUnitId, MaxSdkBase.AdInfo info) => OnMain(() =>
        {
            if (TryResolve(adUnitId, out var unit, out var listener)) listener.OnDisplayed(unit, OperationOf(adUnitId));
        });

        void OnDisplayFailed(string adUnitId, MaxSdkBase.ErrorInfo error, MaxSdkBase.AdInfo info) => OnMain(() =>
        {
            if (_disposed) return;
            if (_tierCascades.TryGetValue(adUnitId, out var cascade)) cascade.EndShow();
            if (TryResolve(adUnitId, out var unit, out var listener))
                listener.OnDisplayFailed(unit, OperationOf(adUnitId), error.Code + ": " + error.Message);
        });

        void OnHidden(string adUnitId, MaxSdkBase.AdInfo info) => OnMain(() =>
        {
            if (_disposed) return;
            if (_tierCascades.TryGetValue(adUnitId, out var cascade)) cascade.EndShow();
            if (TryResolve(adUnitId, out var unit, out var listener)) listener.OnClosed(unit, OperationOf(adUnitId));
        });

        void OnClicked(string adUnitId, MaxSdkBase.AdInfo info) => OnMain(() =>
        {
            if (TryResolve(adUnitId, out var unit, out var listener))
                listener.OnClicked(unit, unit.Format.IsFullScreen() ? OperationOf(adUnitId) : Guid.Empty);
        });

        void OnReceivedReward(string adUnitId, MaxSdkBase.Reward reward, MaxSdkBase.AdInfo info) => OnMain(() =>
        {
            if (TryResolve(adUnitId, out var unit, out var listener))
                listener.OnRewarded(unit, OperationOf(adUnitId), new AdReward(reward.Label ?? string.Empty, reward.Amount));
        });

        // Ad full-screen: MAX bắn event này trên background thread (keepInBackground) để có revenue cả khi ad đang chạy.
        void OnRevenuePaid(string adUnitId, MaxSdkBase.AdInfo info) => OnMain(() =>
        {
            if (!TryResolve(adUnitId, out var unit, out var listener)) return;
            var operation = unit.Format.IsFullScreen() ? OperationOf(adUnitId) : Guid.Empty;
            listener.OnPaid(unit, operation, new AdPaidValue(info.Revenue, "USD", MapPrecision(info.RevenuePrecision),
                string.IsNullOrEmpty(info.NetworkName) ? "applovin" : info.NetworkName));
        });

        void OnAdViewLoaded(string adUnitId, MaxSdkBase.AdInfo info) => OnMain(() =>
        {
            if (!TryResolve(adUnitId, out var unit, out var listener)) return;
            listener.OnLoaded(unit);
            if (unit.Format == AdFormat.Banner) listener.OnAdViewLayoutChanged(unit, BannerLayoutOf(adUnitId, expanded: false));
            else if (unit.Format == AdFormat.MRec) listener.OnMrecSizeChanged(unit, MrecSizeOf());
        });

        MrecSize MrecSizeOf()
        {
            float density = SafeScreenDensity();
            return new MrecSize(MrecSize.StandardWidthDp, MrecSize.StandardHeightDp,
                MrecSize.StandardWidthDp * density, MrecSize.StandardHeightDp * density);
        }

        float SafeScreenDensity()
        {
            try
            {
                float density = MaxSdkUtils.GetScreenDensity();
                return density > 0 ? density : 1f;
            }
            catch (Exception e)
            {
                _log.Warning("Reading MAX screen density failed", e);
                return 1f;
            }
        }

        void OnAdViewExpanded(string adUnitId, MaxSdkBase.AdInfo info) => OnMain(() =>
        {
            if (TryResolve(adUnitId, out var unit, out var listener) && unit.Format == AdFormat.Banner)
                listener.OnAdViewLayoutChanged(unit, BannerLayoutOf(adUnitId, expanded: true));
        });

        void OnAdViewCollapsed(string adUnitId, MaxSdkBase.AdInfo info) => OnMain(() =>
        {
            if (TryResolve(adUnitId, out var unit, out var listener) && unit.Format == AdFormat.Banner)
                listener.OnAdViewLayoutChanged(unit, BannerLayoutOf(adUnitId, expanded: false));
        });

        BannerLayout BannerLayoutOf(string adUnitId, bool expanded)
        {
            try
            {
                bool adaptive = !_views.TryGetValue(adUnitId, out var request) || request.Size == BannerSize.AdaptiveAnchored;
                float heightDp = adaptive ? MaxSdkUtils.GetAdaptiveBannerHeight() : MaxSdkUtils.IsTablet() ? 90f : 50f;
                float density = MaxSdkUtils.GetScreenDensity();
                return new BannerLayout(heightDp, heightDp * (density > 0 ? density : 1f), false, expanded);
            }
            catch (Exception e)
            {
                _log.Warning("Reading MAX banner layout failed", e);
                return BannerLayout.None;
            }
        }

        static AdLoadError MapError(MaxSdkBase.ErrorInfo error)
        {
            var kind = error.Code switch
            {
                MaxSdkBase.ErrorCode.NoFill => AdLoadFailure.NoFill,
                MaxSdkBase.ErrorCode.NetworkError => AdLoadFailure.Network,
                MaxSdkBase.ErrorCode.NetworkTimeout => AdLoadFailure.Network,
                MaxSdkBase.ErrorCode.NoNetwork => AdLoadFailure.Network,
                _ => AdLoadFailure.Provider,
            };
            return new AdLoadError(kind, error.Message ?? error.Code.ToString(), (int)error.Code);
        }

        internal static RevenuePrecision MapPrecision(string? precision) => precision switch
        {
            "exact" => RevenuePrecision.Precise,
            "estimated" => RevenuePrecision.Estimated,
            "publisher_defined" => RevenuePrecision.PublisherDefined,
            _ => RevenuePrecision.Unknown,
        };

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Unsubscribe();
            foreach (var cascade in _cascades.Values) cascade.Dispose();
            foreach (var pair in new List<KeyValuePair<string, AdViewRequest>>(_views))
            {
                if (_units.TryGetValue(pair.Key, out var unit)) _log.TryRun("Destroy ad view", () => DestroyAdView(unit));
            }
            _init?.TrySetResult(SdkError.Disposed(Op));
            _listener = null;
        }
    }
}
