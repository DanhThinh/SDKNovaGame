#nullable enable
using System;
using System.Collections.Generic;
using NovaGames.Mobile.Infrastructure;

namespace NovaGames.Mobile.Ads
{
    // Banner/MREC: tạo, ẩn, hủy view, retry khi load lỗi, layout.
    public sealed partial class AdsManager
    {
        public SdkResult Show(BannerPlacement placement, BannerOptions options)
        {
            if (placement is null) throw new ArgumentNullException(nameof(placement));
            if (options is null) throw new ArgumentNullException(nameof(options));
            if (options.Collapsible == CollapsiblePolicy.Required && !Supports(AdCapability.CollapsibleBanner))
                return Unsupported(placement, "collapsible banner");
            return ShowView(placement, options, null);
        }

        public SdkResult Show(BannerPlacement placement) => Show(placement, _options.DefaultBanner);

        public SdkResult Show(MrecPlacement placement, MrecOptions options)
        {
            if (placement is null) throw new ArgumentNullException(nameof(placement));
            if (options is null) throw new ArgumentNullException(nameof(options));
            if (!IsValid(options))
                return new SdkError(Op + ".invalid_mrec_position", SdkErrorCategory.Configuration,
                    "Custom MREC position requires finite, non-negative pixel coordinates", false, _providerId);
            return ShowView(placement, null, options);
        }

        static bool IsValid(MrecOptions options)
        {
            if (options.Position != MrecPosition.Custom) return options.PixelPosition is null;
            var point = options.PixelPosition;
            return point != null && point.X >= 0 && point.Y >= 0
                && point.X <= int.MaxValue && point.Y <= int.MaxValue
                && !float.IsNaN(point.X) && !float.IsInfinity(point.X)
                && !float.IsNaN(point.Y) && !float.IsInfinity(point.Y);
        }

        SdkResult ShowView(AdPlacement placement, BannerOptions? banner, MrecOptions? mrec)
        {
            if (_disposed) return SdkError.Disposed(Op);
            if (_adapter is null || _state.Value == AdsModuleState.Disabled)
                return new SdkError(Op + ".disabled", SdkErrorCategory.Configuration, "No ads provider", false);
            if (!Supports(placement.Format.ToCapability())) return Unsupported(placement, placement.Format.ToString());
            if (!TryGetUnit(placement, out var unit))
                return new SdkError(Op + ".unmapped_placement", SdkErrorCategory.Configuration,
                    placement + " is not mapped to a " + placement.Format + " ad unit", false, _providerId);
            if (IsRemoved(placement.Format))
                return new SdkError(Op + ".removed_by_entitlement", SdkErrorCategory.Blocked, "remove_ads is active", false, _providerId);
            if (!_policy.IsFormatEnabled(placement.Format))
                return new SdkError(Op + ".kill_switch", SdkErrorCategory.Blocked, placement.Format + " disabled by remote config", false, _providerId);

            if (_views.TryGetValue(placement.Id, out var existing))
            {
                if (Equals(existing.BannerOptions, banner) && Equals(existing.MrecOptions, mrec) && existing.State != AdViewState.Failed)
                {
                    if (existing.State == AdViewState.Hidden)
                    {
                        if (existing.Requested)
                        {
                            _log.TryRun("ShowAdView", () => _adapter.ShowAdView(existing.Unit, existing.Request));
                            existing.State = AdViewState.Visible;
                        }
                        else
                        {
                            // Show -> Hide -> Show trong lúc consent/init còn chặn: khôi phục yêu cầu đang chờ
                            // để OnBecameReady tạo view.
                            existing.State = AdViewState.Loading;
                            if (IsReadyState && GateBlockReason() is null) RequestAdView(existing);
                        }
                    }
                    return SdkResult.Ok;
                }
                DestroyView(existing);
            }

            // Một ad unit chỉ có một view; mỗi vị trí banner tối đa một banner.
            var conflicts = new List<AdViewEntry>();
            foreach (var entry in _views.Values)
            {
                if (entry.Unit.Key == unit.Key
                    || banner != null && entry.BannerOptions != null && entry.BannerOptions.Position == banner.Position)
                    conflicts.Add(entry);
            }
            foreach (var conflict in conflicts) DestroyView(conflict);

            var view = new AdViewEntry(placement, unit, banner, mrec);
            _views[placement.Id] = view;
            if (IsReadyState && GateBlockReason() is null) RequestAdView(view);
            // Chưa sẵn sàng: giữ yêu cầu, tạo view khi module Ready.
            return SdkResult.Ok;
        }

        void RequestAdView(AdViewEntry entry)
        {
            var collapse = CollapseDirection.None;
            var banner = entry.BannerOptions;
            if (banner != null && banner.Collapsible != CollapsiblePolicy.None && Supports(AdCapability.CollapsibleBanner))
            {
                var now = _ctx.Clock.UtcNow;
                if (_lastCollapsibleRequestUtc == DateTime.MinValue || now - _lastCollapsibleRequestUtc >= _policy.CollapsibleInterval)
                {
                    collapse = banner.Position == BannerPosition.Top ? CollapseDirection.Top : CollapseDirection.Bottom;
                    _lastCollapsibleRequestUtc = now;
                }
            }

            entry.Request = new AdViewRequest(entry.Placement.Id,
                banner?.Position ?? BannerPosition.Bottom,
                entry.MrecOptions?.Position ?? MrecPosition.Centered,
                banner?.Size ?? BannerSize.Standard,
                collapse,
                entry.MrecOptions?.PixelPosition);
            entry.Requested = true;
            entry.State = AdViewState.Loading;
            _log.TryRun("ShowAdView", () => _adapter!.ShowAdView(entry.Unit, entry.Request));
        }

        public void Hide(BannerPlacement placement) => HideView(placement);
        public void Hide(MrecPlacement placement) => HideView(placement);

        void HideView(AdPlacement placement)
        {
            if (placement is null || !_views.TryGetValue(placement.Id, out var entry)) return;
            if (entry.Requested) _log.TryRun("HideAdView", () => _adapter!.HideAdView(entry.Unit));
            entry.State = AdViewState.Hidden;
        }

        public void Destroy(BannerPlacement placement) => DestroyView(placement);
        public void Destroy(MrecPlacement placement) => DestroyView(placement);

        void DestroyView(AdPlacement placement)
        {
            if (placement is null || !_views.TryGetValue(placement.Id, out var entry)) return;
            DestroyView(entry);
        }

        void DestroyView(AdViewEntry entry)
        {
            _views.Remove(entry.Placement.Id);
            entry.RetryTimer?.Dispose();
            entry.State = AdViewState.Destroyed;
            if (entry.Requested) _log.TryRun("DestroyAdView", () => _adapter!.DestroyAdView(entry.Unit));
            if (entry.Placement is BannerPlacement banner && entry.Layout != BannerLayout.None)
                _layoutChanged.Raise(new BannerLayoutChanged(banner, BannerLayout.None));
            if (entry.Placement is MrecPlacement mrec && entry.MrecSize != MrecSize.None)
                _mrecSizeChanged.Raise(new MrecSizeChanged(mrec, MrecSize.None));
        }

        void DestroyAllViews(Func<AdViewEntry, bool> predicate)
        {
            var targets = new List<AdViewEntry>();
            foreach (var entry in _views.Values)
            {
                if (predicate(entry)) targets.Add(entry);
            }
            foreach (var entry in targets) DestroyView(entry);
        }

        public AdViewState GetState(BannerPlacement placement) => ViewState(placement);
        public AdViewState GetState(MrecPlacement placement) => ViewState(placement);

        AdViewState ViewState(AdPlacement placement) =>
            placement != null && _views.TryGetValue(placement.Id, out var entry) ? entry.State : AdViewState.None;

        public BannerLayout GetLayout(BannerPlacement placement) =>
            placement != null && _views.TryGetValue(placement.Id, out var entry) ? entry.Layout : BannerLayout.None;

        public MrecSize GetSize(MrecPlacement placement) =>
            placement != null && _views.TryGetValue(placement.Id, out var entry) ? entry.MrecSize : MrecSize.None;

        AdViewEntry? FindView(AdUnit unit)
        {
            foreach (var entry in _views.Values)
            {
                if (entry.Unit.Key == unit.Key) return entry;
            }
            return null;
        }

        void HandleAdViewLoaded(AdViewEntry entry)
        {
            entry.RetryTimer?.Dispose();
            entry.RetryTimer = null;
            entry.Attempt = 0;
            if (entry.State == AdViewState.Loading || entry.State == AdViewState.Failed) entry.State = AdViewState.Visible;
        }

        void HandleAdViewLoadFailed(AdViewEntry entry, AdLoadError error)
        {
            // Vendor tự refresh sau lần load thành công; lần load đầu lỗi thì tạo lại view theo backoff.
            if (entry.State == AdViewState.Visible || entry.State == AdViewState.Hidden) return;
            entry.State = AdViewState.Failed;
            ScheduleAdViewRetry(entry, error);
        }

        void ScheduleAdViewRetry(AdViewEntry entry, AdLoadError error)
        {
            entry.Attempt++;
            var delay = RetryDelay(entry.Attempt);
            _log.Debug(entry.Placement + " load failed (" + error.Kind + "), retry #" + entry.Attempt + " in " + delay);
            entry.RetryTimer?.Dispose();
            entry.RetryTimer = _ctx.Scheduler.Schedule(delay, () =>
            {
                entry.RetryTimer = null;
                if (!_views.TryGetValue(entry.Placement.Id, out var still) || still != entry || entry.State != AdViewState.Failed) return;
                if (!CanLoad(entry.Unit.Format))
                {
                    ScheduleAdViewRetry(entry, error);
                    return;
                }
                _log.TryRun("DestroyAdView", () => _adapter!.DestroyAdView(entry.Unit));
                RequestAdView(entry);
            });
        }

        void HandleAdViewLayout(AdUnit unit, BannerLayout layout)
        {
            var entry = FindView(unit);
            if (entry is null || entry.Layout == layout) return;
            entry.Layout = layout;
            if (entry.Placement is BannerPlacement banner) _layoutChanged.Raise(new BannerLayoutChanged(banner, layout));
        }

        void HandleMrecSize(AdUnit unit, MrecSize size)
        {
            var entry = FindView(unit);
            if (entry is null || entry.Placement is not MrecPlacement mrec || entry.MrecSize == size) return;
            entry.MrecSize = size;
            _mrecSizeChanged.Raise(new MrecSizeChanged(mrec, size));
        }

        static SdkError Unsupported(AdPlacement placement, string what) =>
            new SdkError("ads.unsupported", SdkErrorCategory.Unavailable, "Provider does not support " + what + " (" + placement + ")", false);

        sealed class AdViewEntry
        {
            public AdViewEntry(AdPlacement placement, AdUnit unit, BannerOptions? bannerOptions, MrecOptions? mrecOptions)
            {
                Placement = placement;
                Unit = unit;
                BannerOptions = bannerOptions;
                MrecOptions = mrecOptions;
                if (placement is MrecPlacement) MrecSize = NovaGames.Mobile.Ads.MrecSize.Standard;
            }

            public AdPlacement Placement { get; }
            public AdUnit Unit { get; }
            public BannerOptions? BannerOptions { get; }
            public MrecOptions? MrecOptions { get; }
            public AdViewRequest Request = null!;
            public bool Requested;
            public AdViewState State = AdViewState.Loading;
            public BannerLayout Layout = BannerLayout.None;
            public MrecSize MrecSize = MrecSize.None;
            public int Attempt;
            public IDisposable? RetryTimer;
        }
    }
}
