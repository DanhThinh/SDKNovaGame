#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Entitlements;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.RemoteConfig;

namespace NovaGames.Mobile.Ads
{
    // Khởi tạo adapter, gate (consent/ATT/force update), policy Remote Config, entitlement, pause/resume.
    public sealed partial class AdsManager
    {
        // ---------------- Initialization & gate ----------------

        // Idempotent. Gate đóng (consent/ATT/force update) -> trả Blocked; Ads tự init khi gate mở.
        // Token chỉ hủy việc chờ của caller.
        public Task<SdkResult> InitializeAsync(CancellationToken ct)
        {
            if (_disposed) return Task.FromResult<SdkResult>(SdkError.Disposed(Op));
            if (!_started) Start();
            if (_state.Value == AdsModuleState.Disabled)
                return Task.FromResult<SdkResult>(new SdkError(Op + ".disabled", SdkErrorCategory.Configuration,
                    "No ads provider selected or adapter not installed", false, _providerId));
            if (_state.Value == AdsModuleState.Failed && _adapterInit is null)
                return Task.FromResult<SdkResult>(new SdkError(Op + ".invalid_options", SdkErrorCategory.Configuration,
                    "Invalid ads options", false, _providerId));
            if (_adapterInit is null)
                return Task.FromResult<SdkResult>(new SdkError(Op + ".blocked", SdkErrorCategory.Blocked,
                    "Ads waiting for consent/ATT/force update gate", true, _providerId));
            // Lượt init adapter luôn kết thúc trong InitTimeout (thành công, lỗi hoặc Timeout rồi retry ở nền).
            return SdkTasks.WaitAsync(_adapterInit, Op + ".init", ct);
        }

        void Start()
        {
            _started = true;
            if (_adapter is null)
            {
                _state.Set(AdsModuleState.Disabled);
                _log.Info(_options.UsedProviders.Count == 0 ? "Ads disabled: no format uses a provider" : "Ads disabled: adapter not installed");
                return;
            }

            var errors = _options.Validate();
            if (errors.Count > 0)
            {
                foreach (var error in errors) _log.Error("Ads options: " + error);
                _state.Set(AdsModuleState.Failed);
                return;
            }

            _adapter.SetListener(new DispatchingListener(this));
            foreach (var unit in _options.AdUnits)
            {
                if (!unit.Format.IsFullScreen()) continue;
                var ttl = _adapter.ManagesExpiry(unit) ? TimeSpan.Zero
                    : unit.Format == AdFormat.AppOpen ? _options.AppOpenTtl : _options.FullScreenTtl;
                var slot = new AdUnitSlot(unit, _adapter, _ctx.Main, _ctx.Clock, _ctx.Scheduler, _log,
                    () => CanLoad(unit.Format), _random, OnSlotChanged, ttl, _options.LoadTimeout, _options.MaxRetryDelay);
                _slots[unit.Key] = slot;
            }

            _subscriptions.Add(_deps.Consent.Subscribe(OnConsentChanged, emitCurrent: false));
            if (_deps.RemoteConfig != null) _subscriptions.Add(_deps.RemoteConfig.Current.Subscribe(OnRemoteConfigChanged, emitCurrent: false));
            _subscriptions.Add(_deps.Entitlements.Changed.Subscribe(OnEntitlementChanged));
            if (_deps.Lifecycle != null) _subscriptions.Add(_deps.Lifecycle.PauseChanged.Subscribe(OnPauseChanged));
            if (_deps.ForceUpdateBlocking != null) _subscriptions.Add(_deps.ForceUpdateBlocking.Subscribe(_ => OnGateMaybeChanged(), emitCurrent: false));
            if (_deps.Network != null) ScheduleNetworkCheck(_deps.Network.IsReachable);

            if (WaitsForAtt) _attHoldTimer = _ctx.Scheduler.Schedule(_options.AttHoldTimeout, OnAttHoldExpired);

            Apply(_deps.Consent.Value);
            _state.Set(AdsModuleState.Blocked);
            OnGateMaybeChanged();
        }

        void OnAttHoldExpired()
        {
            _attHoldTimer = null;
            _attHoldExpired = true;
            if (_deps.Consent.Value.Att == AttStatus.NotDetermined) _log.Info("ATT not determined after hold; starting ads without IDFA");
            OnGateMaybeChanged();
        }

        bool WaitsForAtt => _deps.WaitForAtt ?? _deps.IsIos;

        // null = gate mở.
        string? GateBlockReason()
        {
            if (_deps.ForceUpdateBlocking?.Value == true) return "force_update";
            var consent = _deps.Consent.Value;
            if (!consent.CanRequestAds) return "consent";
            if (WaitsForAtt && consent.Att == AttStatus.NotDetermined && !_attHoldExpired) return "att";
            return null;
        }

        void OnConsentChanged(ConsentSnapshot snapshot)
        {
            if (_disposed) return;
            Apply(snapshot);
            OnGateMaybeChanged();
        }

        void Apply(ConsentSnapshot snapshot)
        {
            if (_adapter is null) return;
            _log.TryRun("Apply consent", () => _adapter.Apply(snapshot));
        }

        void OnGateMaybeChanged()
        {
            if (_disposed || !_started || _adapter is null) return;
            var state = _state.Value;
            // Disabled, options sai, hoặc adapter init lỗi: giữ nguyên, không tự init lại.
            if (state == AdsModuleState.Disabled || state == AdsModuleState.Failed) return;

            var reason = GateBlockReason();
            if (reason != null)
            {
                if (state == AdsModuleState.Ready || state == AdsModuleState.Initializing)
                {
                    _log.Info("Ads blocked by " + reason);
                    CloseDown();
                }
                _state.Set(AdsModuleState.Blocked);
                RaiseAvailabilityChanges();
                return;
            }

            if (!_initStarted)
            {
                // Cờ đặt trước: adapter có thể hoàn tất đồng bộ và gọi lại OnGateMaybeChanged.
                _initStarted = true;
                _state.Set(AdsModuleState.Initializing);
                _adapterInit = InitializeAdapterAsync();
                return;
            }
            if (_adapterReady)
            {
                _state.Set(AdsModuleState.Ready);
                OnBecameReady();
            }
            else
            {
                _state.Set(AdsModuleState.Initializing);
            }
        }

        async Task<SdkResult> InitializeAdapterAsync()
        {
            var adapter = _adapter!;
            var init = new AdsAdapterInitOptions(_ctx.Settings.IsDevelopment, ToArray(_options.AdUnits), _options.Providers)
            {
                RemoteConfig = (IConfigValues?)_deps.RemoteConfig?.Current.Value ?? RemoteConfigSnapshot.Empty,
            };
            // Vendor không callback thì không kẹt Initializing: hết InitTimeout là lỗi Timeout (retryable).
            var op = new VendorOperation<bool>(Op + ".init", _ctx.Main, _ctx.Scheduler, _options.InitTimeout, CancellationToken.None);
            _ = ForwardAdapterInitAsync(adapter, init, op);
            var result = (await op.Task).AsResult();

            if (_disposed) return SdkError.Disposed(Op);
            if (!result.IsSuccess)
            {
                if (result.Error!.IsRetryable)
                {
                    ScheduleInitRetry(result.Error);
                    return result;
                }
                _log.Error("Ads adapter init failed: " + result.Error);
                _state.Set(AdsModuleState.Failed);
                return result;
            }

            _initAttempt = 0;
            _adapterReady = true;
            _log.Info("Ads ready (" + _providerId + ")");
            OnGateMaybeChanged();
            return SdkResult.Ok;
        }

        // Kết quả của adapter (kể cả exception) chuyển vào op; op lo timeout, exactly-once và trả về main thread.
        async Task ForwardAdapterInitAsync(IAdsAdapter adapter, AdsAdapterInitOptions init, VendorOperation<bool> op)
        {
            SdkResult result;
            try
            {
                result = await adapter.InitializeAsync(init, CancellationToken.None);
            }
            catch (Exception e)
            {
                result = SdkError.FromException(Op + ".init", e, _providerId);
            }
            op.Complete(result.IsSuccess ? SdkResult<bool>.Ok(true) : result.Error!);
        }

        // Lỗi tạm thời (mạng, vendor chưa callback): giữ Initializing và init lại theo backoff. Gate đóng trong lúc chờ
        // thì OnGateMaybeChanged chuyển Blocked và init lại khi gate mở.
        void ScheduleInitRetry(SdkError error)
        {
            _initAttempt++;
            var delay = RetryDelay(_initAttempt);
            _log.Warning("Ads adapter init failed (" + error + "), retry #" + _initAttempt + " in " + delay);
            _initRetry?.Dispose();
            _initRetry = _ctx.Scheduler.Schedule(delay, () =>
            {
                _initRetry = null;
                if (_disposed || _adapterReady) return;
                _initStarted = false;
                OnGateMaybeChanged();
            });
        }

        // 2, 4, 8, ... giây, trần MaxRetryDelay, jitter ±20%.
        TimeSpan RetryDelay(int attempt)
        {
            double seconds = Math.Min(_options.MaxRetryDelay.TotalSeconds, Math.Pow(2, Math.Min(attempt, 16)));
            return TimeSpan.FromSeconds(seconds * (0.8 + 0.4 * _random()));
        }

        void OnBecameReady()
        {
            ResumeLoading();
            foreach (var entry in _views.Values)
            {
                if (entry.State == AdViewState.Loading && !entry.Requested) RequestAdView(entry);
            }
            RaiseAvailabilityChanges();
        }

        // Gate đóng: bỏ ad đã preload, dừng retry, destroy banner/MREC. Ad đang show được để kết thúc tự nhiên.
        void CloseDown()
        {
            foreach (var slot in _slots.Values) slot.Discard();
            DestroyAllViews(_ => true);
        }

        bool IsReadyState => _state.Value == AdsModuleState.Ready && _adapterReady && !_disposed;

        bool IsRemoved(AdFormat format) =>
            format != AdFormat.Rewarded && SafeIsActive(EntitlementId.RemoveAds);

        bool SafeIsActive(EntitlementId id)
        {
            try
            {
                return _deps.Entitlements.IsActive(id);
            }
            catch (Exception e)
            {
                _log.Error("Entitlement provider threw", e);
                return false;
            }
        }

        bool CanLoad(AdFormat format) =>
            IsReadyState && Supports(format.ToCapability()) && GateBlockReason() is null
            && _deps.Lifecycle?.IsPaused != true
            && _deps.Network?.IsReachable != false
            && _policy.IsFormatEnabled(format) && !IsRemoved(format);

        // Không có event đổi mạng: kiểm tra định kỳ, có mạng trở lại thì load ngay các ad bị bỏ lỡ lúc offline.
        void ScheduleNetworkCheck(bool wasReachable)
        {
            _networkTimer?.Dispose();
            _networkTimer = _ctx.Scheduler.Schedule(NetworkCheckInterval, () =>
            {
                if (_disposed || _deps.Network is null) return;
                bool reachable = _deps.Network.IsReachable;
                if (reachable && !wasReachable)
                {
                    _log.Debug("Network is back: resuming ad loading");
                    ResumeLoading();
                }
                ScheduleNetworkCheck(reachable);
            });
        }

        void ResumeLoading()
        {
            if (!IsReadyState) return;
            foreach (var slot in _slots.Values)
            {
                if (CanLoad(slot.Unit.Format)) slot.EnsureLoaded();
            }
        }

        // ---------------- Policy / entitlement / lifecycle ----------------

        void OnRemoteConfigChanged(RemoteConfigSnapshot snapshot)
        {
            if (_disposed) return;
            _policy = _deps.ConfigKeys.Read(snapshot);
            ApplyKillSwitches();
            ResumeLoading();
            RaiseAvailabilityChanges();
        }

        void OnEntitlementChanged(EntitlementId id)
        {
            if (_disposed || id != EntitlementId.RemoveAds) return;
            if (SafeIsActive(EntitlementId.RemoveAds))
            {
                _log.Info("remove_ads active: stopping interstitial/app-open/banner/MREC");
                foreach (var slot in _slots.Values)
                {
                    if (slot.Unit.Format != AdFormat.Rewarded) slot.Discard();
                }
                DestroyAllViews(_ => true);
            }
            else
            {
                ResumeLoading();
            }
            RaiseAvailabilityChanges();
        }

        void ApplyKillSwitches()
        {
            foreach (var slot in _slots.Values)
            {
                if (!_policy.IsFormatEnabled(slot.Unit.Format)) slot.Discard();
            }
            DestroyAllViews(entry => !_policy.IsFormatEnabled(entry.Unit.Format));
        }

        void OnPauseChanged(bool paused)
        {
            if (_disposed) return;
            var now = _ctx.Clock.UtcNow;
            if (paused)
            {
                _backgroundSinceUtc = now;
                var showing = _current;
                if (showing != null)
                {
                    _resumeCausedByAd = true;
                    // Thời gian ở background không tính vào display timeout. Android: ad activity pause Unity ngay sau
                    // Show; callback displayed của vendor chỉ được xử lý sau resume, tức là sau cả timer (chạy trên
                    // thread pool, post về main trong lúc pause) -> timer sẽ báo DisplayFailed cho ad đã hiển thị.
                    showing.DisplayTimer?.Dispose();
                    showing.DisplayTimer = null;
                    showing.ShowTimer?.Dispose();
                    showing.ShowTimer = null;
                }
                return;
            }

            var current = _current;
            if (current != null && !current.Closed)
            {
                if (current.Displayed) StartShowTimer(current);
                if (!_deps.IsIos)
                {
                    // Android: Unity chỉ resume khi ad activity đã đóng; callback displayed/reward/closed đang xếp hàng
                    // sẽ tới ngay sau đây. Không tới -> tự đóng.
                    current.Watchdog?.Dispose();
                    current.Watchdog = _ctx.Scheduler.Schedule(ResumeWatchdog, () =>
                    {
                        if (_current != current || current.Closed) return;
                        _log.Warning(current + " close callback missing after resume; closing by watchdog");
                        HandleClosed(current.Slot.Unit, current.Id);
                    });
                }
                else if (!current.Displayed)
                {
                    // iOS: app vào background trước khi ad kịp hiển thị; tính lại display timeout từ lúc quay lại.
                    StartDisplayTimer(current);
                }
            }

            ResumeLoading();

            bool causedByAd = _resumeCausedByAd || current != null || now - _lastFullScreenClosedUtc < PostAdResumeWindow;
            _resumeCausedByAd = false;
            var background = _backgroundSinceUtc == DateTime.MinValue ? TimeSpan.Zero : now - _backgroundSinceUtc;
            if (_autoAppOpen is null || causedByAd || background < _policy.AppOpenMinBackground) return;
            _ = TryShowAsync(_autoAppOpen, AppOpenTrigger.Resume, CancellationToken.None);
        }
    }
}
