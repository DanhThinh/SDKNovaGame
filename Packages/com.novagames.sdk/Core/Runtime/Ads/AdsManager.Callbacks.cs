#nullable enable
using System;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.Tracking;

namespace NovaGames.Mobile.Ads
{
    // Revenue và callback từ adapter (luôn được chuyển về main thread).
    public sealed partial class AdsManager
    {
        // ---------------- Revenue ----------------

        void HandlePaid(AdUnit unit, Guid operationId, AdPaidValue value)
        {
            string placementId;
            if (unit.Format.IsFullScreen())
            {
                var placement = FindOperationPlacement(operationId);
                placementId = placement?.Id ?? string.Empty;
                if (operationId == Guid.Empty) operationId = Guid.NewGuid();
                // Paid callback thứ hai cho cùng show operation full-screen.
                else if (!_paidOperations.Add(operationId))
                {
                    _log.Warning("Duplicate paid callback for " + operationId + " dropped");
                    return;
                }
                if (_paidOperations.Count > 256) _paidOperations.Clear();
            }
            else
            {
                placementId = FindView(unit)?.Placement.Id ?? string.Empty;
                // Banner/MREC: mỗi paid callback (mỗi refresh) là một impression mới.
                operationId = Guid.NewGuid();
            }

            if (!string.IsNullOrEmpty(value.ImpressionId))
            {
                if (_recentImpressionIds.Contains(value.ImpressionId!))
                {
                    _log.Warning("Duplicate impression " + value.ImpressionId + " dropped");
                    return;
                }
                _recentImpressionIds.Enqueue(value.ImpressionId!);
                while (_recentImpressionIds.Count > 64) _recentImpressionIds.Dequeue();
            }

            // Mediation theo unit: build có thể dùng cả MAX lẫn AdMob (Adjust source, ad_platform).
            var mediation = AdsOptions.ProviderId(unit.Provider);

            var revenue = new AdRevenueEvent(Guid.NewGuid(), operationId, unit.Format, placementId, mediation,
                value.Network, unit.AdUnitId, value.Value, value.Currency, value.Precision, value.ImpressionId);
            if (_deps.Revenue != null)
            {
                try
                {
                    _deps.Revenue.ReportAdRevenue(revenue);
                }
                catch (Exception e)
                {
                    _log.Error("Revenue pipeline threw", e);
                }
            }
            _impressions.Raise(new AdImpression(operationId, unit.Format, placementId, mediation, value.Network,
                unit.AdUnitId, value.Value, value.Currency));
        }

        FullScreenPlacement? FindOperationPlacement(Guid operationId)
        {
            if (_current != null && _current.Id == operationId) return _current.Placement;
            foreach (var recent in _recentOperations)
            {
                if (recent.Id == operationId) return recent.Placement;
            }
            return null;
        }

        void HandleClicked(AdUnit unit, Guid operationId)
        {
            // Click mở browser/store -> app vào background; lần resume đó không bật app-open.
            _resumeCausedByAd = true;
        }

        // ---------------- Adapter callbacks ----------------

        void HandleLoaded(AdUnit unit)
        {
            if (_disposed) return;
            if (_slots.TryGetValue(unit.Key, out var slot))
            {
                slot.OnLoaded();
                return;
            }
            var view = FindView(unit);
            if (view != null) HandleAdViewLoaded(view);
        }

        void HandleLoadFailed(AdUnit unit, AdLoadError error)
        {
            if (_disposed) return;
            if (_slots.TryGetValue(unit.Key, out var slot))
            {
                slot.OnLoadFailed(error);
                return;
            }
            var view = FindView(unit);
            if (view != null) HandleAdViewLoadFailed(view, error);
        }

        sealed class DispatchingListener : IAdsAdapterListener
        {
            readonly AdsManager _owner;

            public DispatchingListener(AdsManager owner) { _owner = owner; }

            IMainThreadDispatcher Main => _owner._ctx.Main;

            public void OnLoaded(AdUnit unit) => Main.Post(() => _owner.HandleLoaded(unit));
            public void OnLoadFailed(AdUnit unit, AdLoadError error) => Main.Post(() => _owner.HandleLoadFailed(unit, error));
            public void OnDisplayed(AdUnit unit, Guid id) => Main.Post(() => _owner.HandleDisplayed(unit, id));
            public void OnDisplayFailed(AdUnit unit, Guid id, string message) => Main.Post(() => _owner.HandleDisplayFailed(unit, id, message));
            public void OnRewarded(AdUnit unit, Guid id, AdReward reward) => Main.Post(() => _owner.HandleRewarded(unit, id, reward));
            public void OnClosed(AdUnit unit, Guid id) => Main.Post(() => _owner.HandleClosed(unit, id));
            public void OnClicked(AdUnit unit, Guid id) => Main.Post(() => _owner.HandleClicked(unit, id));
            public void OnPaid(AdUnit unit, Guid id, AdPaidValue value) => Main.Post(() => { if (!_owner._disposed) _owner.HandlePaid(unit, id, value); });
            public void OnAdViewLayoutChanged(AdUnit unit, BannerLayout layout) => Main.Post(() => { if (!_owner._disposed) _owner.HandleAdViewLayout(unit, layout); });
            public void OnMrecSizeChanged(AdUnit unit, MrecSize size) => Main.Post(() => { if (!_owner._disposed) _owner.HandleMrecSize(unit, size); });
        }
    }
}
