#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Privacy;

namespace NovaGames.Mobile.Ads
{
    /// <summary>
    /// Nhiều mediation trong một build: chuyển lệnh của từng ad unit tới adapter của AdUnit.Provider.
    /// AdsManager chỉ thấy một adapter nên gate, capping, khóa full-screen, retry, revenue dùng chung cho mọi provider.
    /// Capabilities chỉ gồm format được route tới provider đã cài; format của provider chưa cài thành Unsupported.
    /// </summary>
    public sealed class RoutingAdsAdapter : IAdsAdapter
    {
        readonly Dictionary<AdsProvider, IAdsAdapter> _adapters;

        public RoutingAdsAdapter(IReadOnlyDictionary<AdsProvider, IAdsAdapter> adapters, IReadOnlyList<AdUnit> units)
        {
            if (adapters is null) throw new ArgumentNullException(nameof(adapters));
            if (units is null) throw new ArgumentNullException(nameof(units));
            _adapters = new Dictionary<AdsProvider, IAdsAdapter>();
            var ids = new List<string>();
            foreach (var pair in adapters)
            {
                _adapters[pair.Key] = pair.Value;
                ids.Add(pair.Value.Id);
            }
            Id = string.Join("+", ids);

            foreach (var unit in units)
            {
                if (!_adapters.TryGetValue(unit.Provider, out var adapter)) continue;
                var capability = unit.Format.ToCapability();
                if ((adapter.Capabilities & capability) != 0) Capabilities |= capability;
                // Collapsible đi theo provider phục vụ banner.
                if (unit.Format == AdFormat.Banner) Capabilities |= adapter.Capabilities & AdCapability.CollapsibleBanner;
            }
        }

        public string Id { get; }
        public AdCapability Capabilities { get; }

        public bool ManagesExpiry(AdUnit unit) => _adapters.TryGetValue(unit.Provider, out var adapter) && adapter.ManagesExpiry(unit);

        public void SetListener(IAdsAdapterListener listener)
        {
            foreach (var adapter in _adapters.Values) adapter.SetListener(listener);
        }

        public void Apply(ConsentSnapshot snapshot)
        {
            foreach (var adapter in _adapters.Values) adapter.Apply(snapshot);
        }

        /// <summary>
        /// Init mọi provider song song; Ok khi tất cả Ok. Lỗi không retry được ưu tiên báo trước lỗi tạm thời.
        /// Adapter idempotent: lần thử lại chỉ init lại provider chưa xong.
        /// </summary>
        public async Task<SdkResult> InitializeAsync(AdsAdapterInitOptions options, CancellationToken ct)
        {
            var tasks = new List<Task<SdkResult>>();
            foreach (var adapter in _adapters.Values) tasks.Add(adapter.InitializeAsync(options, ct));

            SdkError? retryable = null;
            SdkError? fatal = null;
            foreach (var task in tasks)
            {
                var result = await task;
                if (result.IsSuccess) continue;
                if (result.Error!.IsRetryable) retryable ??= result.Error;
                else fatal ??= result.Error;
            }
            if (fatal != null) return fatal;
            if (retryable != null) return retryable;
            return SdkResult.Ok;
        }

        public void Load(AdUnit unit) => Route(unit).Load(unit);
        public bool IsReady(AdUnit unit) => _adapters.TryGetValue(unit.Provider, out var adapter) && adapter.IsReady(unit);
        public void Show(AdUnit unit, string placementId, Guid showOperationId) => Route(unit).Show(unit, placementId, showOperationId);
        public void Discard(AdUnit unit) => Route(unit).Discard(unit);

        public void ShowAdView(AdUnit unit, AdViewRequest request) => Route(unit).ShowAdView(unit, request);
        public void HideAdView(AdUnit unit) => Route(unit).HideAdView(unit);
        public void DestroyAdView(AdUnit unit) => Route(unit).DestroyAdView(unit);

        /// <summary>Mở debugger của mọi provider (MAX Mediation Debugger + AdMob Ad Inspector).</summary>
        public void OpenDebugger()
        {
            foreach (var adapter in _adapters.Values) adapter.OpenDebugger();
        }

        public void Dispose()
        {
            foreach (var adapter in _adapters.Values) adapter.Dispose();
        }

        // AdsManager bắt exception của adapter: unit của provider chưa cài thành load fail / show fail.
        IAdsAdapter Route(AdUnit unit) =>
            _adapters.TryGetValue(unit.Provider, out var adapter)
                ? adapter
                : throw new InvalidOperationException("No ads adapter installed for " + unit.Provider + " (" + unit + ")");
    }
}
