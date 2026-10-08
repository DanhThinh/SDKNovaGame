#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Infrastructure;

namespace NovaGames.Mobile.Ads
{
    // Interstitial/rewarded/app open: availability, preload, show operation, callback displayed/reward/close.
    public sealed partial class AdsManager
    {
        // ---------------- Availability ----------------

        public AdAvailability GetAvailability(FullScreenPlacement placement) => Evaluate(placement, out _, out _);

        AdAvailability IAppOpenAds.GetAvailability(AppOpenPlacement placement) => Evaluate(placement, out _, out _);

        // Gate dùng cho cả GetAvailability (kèm load) và RaiseAvailabilityChanges (không load).
        // Ready = qua mọi gate; slot có giá trị từ sau gate kill switch (PreloadAsync vẫn load khi bị capping/busy).
        AdAvailability Evaluate(FullScreenPlacement placement, out AdUnitSlot? slot, out string reason)
        {
            if (placement is null)
            {
                slot = null;
                reason = "null placement";
                return AdAvailability.Disabled;
            }
            _knownPlacements[placement.Id] = placement;

            var availability = CheckGates(placement, out slot, out reason);
            if (availability != AdAvailability.Ready) return availability;
            if (slot!.IsReady) return AdAvailability.Ready;
            slot.EnsureLoaded();
            return slot.State == AdUnitState.Loading ? AdAvailability.Loading : AdAvailability.NotReady;
        }

        // Như Evaluate nhưng không kích hoạt load (tránh đệ quy từ OnSlotChanged).
        AdAvailability EvaluateQuiet(FullScreenPlacement placement)
        {
            var availability = CheckGates(placement, out var slot, out _);
            if (availability != AdAvailability.Ready) return availability;
            if (slot!.State == AdUnitState.Ready) return AdAvailability.Ready;
            return slot.State == AdUnitState.Loading ? AdAvailability.Loading : AdAvailability.NotReady;
        }

        // Thứ tự gate: module, consent/ATT, force update, entitlement, kill switch, capping, suppression, lock.
        // Nơi duy nhất quyết định placement có được show không; thêm gate mới thì thêm ở đây.
        AdAvailability CheckGates(FullScreenPlacement placement, out AdUnitSlot? slot, out string reason)
        {
            slot = null;
            reason = string.Empty;
            var state = _state.Value;
            if (_adapter is null || state == AdsModuleState.Disabled) { reason = "no provider"; return AdAvailability.Disabled; }
            if (!Supports(placement.Format.ToCapability())) { reason = "provider does not support " + placement.Format; return AdAvailability.Unsupported; }
            if (!TryGetUnit(placement, out var unit))
            {
                reason = "placement not mapped to a " + placement.Format + " ad unit";
                return AdAvailability.Disabled;
            }
            if (!IsReadyState) { reason = "module " + state; return state == AdsModuleState.Failed ? AdAvailability.Disabled : AdAvailability.Blocked; }
            var gate = GateBlockReason();
            if (gate != null) { reason = gate; return AdAvailability.Blocked; }
            if (IsRemoved(placement.Format)) { reason = "remove_ads"; return AdAvailability.RemovedByEntitlement; }
            if (!_policy.IsFormatEnabled(placement.Format)) { reason = "kill switch"; return AdAvailability.Disabled; }

            slot = _slots[unit.Key];
            if (placement.Format == AdFormat.Interstitial)
            {
                var capped = _capping.CheckInterstitial(_policy, _playerLevel);
                if (capped != null) { reason = capped; return AdAvailability.Capped; }
            }
            if (placement.Format == AdFormat.AppOpen && _suppressions.Count > 0) { reason = "suppressed"; return AdAvailability.Blocked; }
            if (_current != null) { reason = "busy"; return AdAvailability.Blocked; }
            return AdAvailability.Ready;
        }

        // Placement khai báo trong AdsOptions.Placements dùng unit được map (phải cùng format). Placement chưa khai báo
        // (vd. tên string từ NovaAds) dùng unit đầu tiên cùng format; tên placement vẫn đi theo show/revenue.
        bool TryGetUnit(AdPlacement placement, out AdUnit unit)
        {
            if (_unitsByPlacement.TryGetValue(placement.Id, out unit!)) return unit.Format == placement.Format;
            return _defaultUnits.TryGetValue(placement.Format, out unit!);
        }

        void OnSlotChanged(AdUnitSlot slot) => RaiseAvailabilityChanges();

        void RaiseAvailabilityChanges()
        {
            if (_knownPlacements.Count == 0) return;
            if (_availabilityChanged.SubscriberCount == 0)
            {
                // Không ai nghe: quên giá trị cũ để người đăng ký sau nhận đủ thay đổi tiếp theo.
                _lastAvailability.Clear();
                return;
            }
            foreach (var placement in new List<FullScreenPlacement>(_knownPlacements.Values))
            {
                var availability = EvaluateQuiet(placement);
                if (_lastAvailability.TryGetValue(placement.Id, out var last) && last == availability) continue;
                _lastAvailability[placement.Id] = availability;
                _availabilityChanged.Raise(new AdAvailabilityChanged(placement, availability));
            }
        }

        // ---------------- Full-screen ----------------

        public async Task<SdkResult> PreloadAsync(FullScreenPlacement placement, CancellationToken ct)
        {
            // Evaluate chỉ trả slot khi các gate trước capping đã qua; capping/suppression/busy không chặn preload.
            var availability = Evaluate(placement, out var slot, out var reason);
            if (slot is null) return AvailabilityError(availability, reason);
            if (slot.IsReady) return SdkResult.Ok;

            slot.EnsureLoaded();
            bool ready = await slot.WaitReadyAsync(_options.LoadTimeout, ct);
            if (ready) return SdkResult.Ok;
            if (ct.IsCancellationRequested) return SdkError.Cancelled(Op + ".preload");
            return new SdkError(Op + ".preload.not_ready", SdkErrorCategory.Unavailable,
                placement + " not loaded (" + slot.State + ")", true, _providerId);
        }

        SdkError AvailabilityError(AdAvailability availability, string reason)
        {
            var category = availability switch
            {
                AdAvailability.Blocked => SdkErrorCategory.Blocked,
                AdAvailability.Unsupported => SdkErrorCategory.Unavailable,
                AdAvailability.Disabled => SdkErrorCategory.Configuration,
                _ => SdkErrorCategory.Blocked,
            };
            return new SdkError(Op + "." + availability.ToString().ToLowerInvariant(), category,
                availability + ": " + reason, false, _providerId);
        }

        public async Task<InterstitialResult> ShowAsync(InterstitialPlacement placement, CancellationToken ct)
        {
            var completion = await ShowFullScreenAsync(placement, TimeSpan.Zero, ct);
            return new InterstitialResult(completion.Outcome, completion.OperationId, completion.Error);
        }

        public async Task<RewardedResult> ShowAsync(RewardedPlacement placement, RewardedShowOptions options, CancellationToken ct)
        {
            var completion = await ShowFullScreenAsync(placement, options?.WaitForLoad ?? TimeSpan.Zero, ct);
            return new RewardedResult(completion.Outcome, completion.OperationId, completion.Reward, completion.Error);
        }

        public async Task<AppOpenResult> TryShowAsync(AppOpenPlacement placement, AppOpenTrigger trigger, CancellationToken ct)
        {
            if (trigger == AppOpenTrigger.Resume && _ctx.Clock.UtcNow - _lastFullScreenClosedUtc < PostAdResumeWindow)
                return new AppOpenResult(ShowOutcome.Blocked, Guid.Empty);
            var completion = await ShowFullScreenAsync(placement, TimeSpan.Zero, ct);
            return new AppOpenResult(completion.Outcome, completion.OperationId, completion.Error);
        }

        async Task<ShowCompletion> ShowFullScreenAsync(FullScreenPlacement placement, TimeSpan waitForLoad, CancellationToken ct)
        {
            if (_disposed) return ShowCompletion.Of(ShowOutcome.Disabled);
            if (ct.IsCancellationRequested) return ShowCompletion.Of(ShowOutcome.Cancelled);

            var availability = Evaluate(placement, out var slot, out var reason);
            if ((availability == AdAvailability.NotReady || availability == AdAvailability.Loading) && waitForLoad > TimeSpan.Zero && slot != null)
            {
                await slot.WaitReadyAsync(waitForLoad, ct);
                if (ct.IsCancellationRequested) return ShowCompletion.Of(ShowOutcome.Cancelled);
                availability = Evaluate(placement, out slot, out reason);
            }

            if (availability != AdAvailability.Ready || slot is null)
            {
                var outcome = availability switch
                {
                    AdAvailability.Capped => ShowOutcome.Capped,
                    AdAvailability.Disabled => ShowOutcome.Disabled,
                    AdAvailability.Unsupported => ShowOutcome.Unsupported,
                    AdAvailability.RemovedByEntitlement => ShowOutcome.RemovedByEntitlement,
                    AdAvailability.Blocked => _current != null && reason == "busy" ? ShowOutcome.Busy : ShowOutcome.Blocked,
                    _ => ShowOutcome.NotReady,
                };
                _log.Debug(placement + " not shown: " + outcome + (reason.Length > 0 ? " (" + reason + ")" : ""));
                return ShowCompletion.Of(outcome);
            }

            var op = new ShowOperation(Guid.NewGuid(), placement, slot);
            _current = op;
            slot.BeginShow();
            op.PresentationStarted = true;
            NotifyPresentation(op, FullScreenPresentationPhase.Showing);
            StartDisplayTimer(op);

            // Sau khi gọi vendor Show thì không hủy được nữa (cancel chỉ có hiệu lực trước khi hiển thị).
            try
            {
                _adapter!.Show(slot.Unit, placement.Id, op.Id);
            }
            catch (Exception e)
            {
                Complete(op, ShowOutcome.DisplayFailed, SdkError.FromException(Op + ".show", e, _providerId));
            }
            return await op.Tcs.Task;
        }

        // Pha "displayed": vendor không báo displayed/display failed trong DisplayTimeout -> DisplayFailed.
        // Hủy khi app vào background (OnPauseChanged).
        void StartDisplayTimer(ShowOperation op)
        {
            op.DisplayTimer?.Dispose();
            op.DisplayTimer = _ctx.Scheduler.Schedule(_options.DisplayTimeout, () =>
            {
                if (op.Displayed || op.Done) return;
                Complete(op, ShowOutcome.DisplayFailed, new SdkError(Op + ".display_timeout", SdkErrorCategory.Timeout,
                    op.Placement + " did not display within " + _options.DisplayTimeout, true, _providerId));
            });
        }

        // Ad đã hiển thị mà vendor không báo close (callback bị mất): sau MaxShowDuration khi app ở foreground thì coi như
        // đã đóng, để không khóa full-screen và không để game bị pause mãi. Tạm dừng khi app vào background.
        void StartShowTimer(ShowOperation op)
        {
            op.ShowTimer?.Dispose();
            op.ShowTimer = _ctx.Scheduler.Schedule(_options.MaxShowDuration, () =>
            {
                if (_current != op || op.Closed || op.Done) return;
                _log.Warning(op + " still open after " + _options.MaxShowDuration + " without a close callback; closing by watchdog");
                HandleClosed(op.Slot.Unit, op.Id);
            });
        }

        ShowOperation? Match(AdUnit unit, Guid operationId, string callback)
        {
            var current = _current;
            if (current != null && current.Id == operationId && !current.Done) return current;
            if (operationId != Guid.Empty && !IsRecent(operationId))
                _log.Warning("Ignoring " + callback + " for unknown show operation " + operationId + " (" + unit + ")");
            return null;
        }

        void HandleDisplayed(AdUnit unit, Guid operationId)
        {
            var op = Match(unit, operationId, "displayed");
            if (op is null || op.Displayed) return;
            op.Displayed = true;
            op.DisplayTimer?.Dispose();
            op.DisplayTimer = null;
            StartShowTimer(op);
        }

        void HandleDisplayFailed(AdUnit unit, Guid operationId, string message)
        {
            var op = Match(unit, operationId, "display failed");
            if (op is null) return;
            Complete(op, ShowOutcome.DisplayFailed,
                new SdkError(Op + ".display_failed", SdkErrorCategory.Provider, message, true, _providerId));
        }

        void HandleRewarded(AdUnit unit, Guid operationId, AdReward reward)
        {
            var current = _current;
            if (current != null && current.Id == operationId && !current.Done)
            {
                current.Reward ??= reward;
                // Reward về sau close nhưng trong grace window.
                if (current.Closed) Complete(current, ShowOutcome.Rewarded, null);
                return;
            }
            if (IsRecent(operationId))
                _log.Warning("ads.reward_late: reward for " + operationId + " arrived after the operation completed; not granted");
        }

        void HandleClosed(AdUnit unit, Guid operationId)
        {
            var op = Match(unit, operationId, "closed");
            if (op is null || op.Closed) return;
            op.Closed = true;
            op.Displayed = true;
            op.Watchdog?.Dispose();
            op.Watchdog = null;
            op.ShowTimer?.Dispose();
            op.ShowTimer = null;

            if (op.Placement.Format != AdFormat.Rewarded)
            {
                Complete(op, ShowOutcome.Shown, null);
                return;
            }
            if (op.Reward != null)
            {
                Complete(op, ShowOutcome.Rewarded, null);
                return;
            }
            // Vendor không đảm bảo thứ tự reward/close: chờ reward thêm một khoảng ngắn.
            op.GraceTimer = _ctx.Scheduler.Schedule(_policy.RewardGrace, () =>
            {
                if (!op.Done) Complete(op, ShowOutcome.ClosedWithoutReward, null);
            });
        }

        void Complete(ShowOperation op, ShowOutcome outcome, SdkError? error)
        {
            if (op.Done) return;
            op.Done = true;
            op.DisplayTimer?.Dispose();
            op.GraceTimer?.Dispose();
            op.Watchdog?.Dispose();
            op.ShowTimer?.Dispose();
            if (_current == op) _current = null;

            bool displayed = op.Displayed;
            if (displayed)
            {
                _lastFullScreenClosedUtc = _ctx.Clock.UtcNow;
                if (op.Placement.Format == AdFormat.Interstitial) _capping.RecordInterstitialClosed();
                else if (op.Placement.Format == AdFormat.Rewarded) _capping.RecordRewardedClosed();
            }

            Remember(op);
            op.Slot.EndShow();
            if (op.PresentationStarted) NotifyPresentation(op, FullScreenPresentationPhase.Closed);

            _log.Debug(op + " completed: " + outcome + (error is null ? "" : " " + error));
            op.Tcs.TrySetResult(new ShowCompletion(outcome, op.Id, outcome == ShowOutcome.Rewarded ? op.Reward : null, error));
            RaiseAvailabilityChanges();
        }

        void NotifyPresentation(ShowOperation op, FullScreenPresentationPhase phase)
        {
            var format = op.Placement.Format;
            var handler = _presentationHandler;
            if (handler != null)
            {
                try
                {
                    if (phase == FullScreenPresentationPhase.Showing) handler.OnFullScreenShowing(format);
                    else handler.OnFullScreenClosed(format);
                }
                catch (Exception e)
                {
                    _log.Error("Presentation handler threw", e);
                }
            }
            _presentation.Raise(new FullScreenAdPresentation(phase, format, op.Placement.Id, op.Id));
        }

        void Remember(ShowOperation op)
        {
            _recentOperations.Enqueue(new CompletedOperation(op.Id, op.Placement));
            while (_recentOperations.Count > RecentOperationCapacity) _recentOperations.Dequeue();
        }

        bool IsRecent(Guid operationId)
        {
            foreach (var recent in _recentOperations)
            {
                if (recent.Id == operationId) return true;
            }
            return false;
        }

        sealed class ShowOperation
        {
            public ShowOperation(Guid id, FullScreenPlacement placement, AdUnitSlot slot)
            {
                Id = id;
                Placement = placement;
                Slot = slot;
            }

            public Guid Id { get; }
            public FullScreenPlacement Placement { get; }
            public AdUnitSlot Slot { get; }
            public readonly TaskCompletionSource<ShowCompletion> Tcs =
                new TaskCompletionSource<ShowCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Displayed;
            public bool Closed;
            public bool Done;
            public bool PresentationStarted;
            public AdReward? Reward;
            public IDisposable? DisplayTimer;
            public IDisposable? GraceTimer;
            public IDisposable? Watchdog;
            public IDisposable? ShowTimer;

            public override string ToString() => Placement + "#" + Id.ToString("N").Substring(0, 8);
        }

        sealed class ShowCompletion
        {
            public ShowCompletion(ShowOutcome outcome, Guid operationId, AdReward? reward, SdkError? error)
            {
                Outcome = outcome;
                OperationId = operationId;
                Reward = reward;
                Error = error;
            }

            public ShowOutcome Outcome { get; }
            public Guid OperationId { get; }
            public AdReward? Reward { get; }
            public SdkError? Error { get; }

            public static ShowCompletion Of(ShowOutcome outcome) => new ShowCompletion(outcome, Guid.Empty, null, null);
        }

        readonly struct CompletedOperation
        {
            public CompletedOperation(Guid id, FullScreenPlacement placement)
            {
                Id = id;
                Placement = placement;
            }

            public Guid Id { get; }
            public FullScreenPlacement Placement { get; }
        }
    }
}
