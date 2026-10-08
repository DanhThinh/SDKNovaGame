#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.RemoteConfig;
using NovaGames.Mobile.Tracking;

namespace NovaGames.Mobile.Ads
{
    public static class AdProviderIds
    {
        public const string Max = "max";
        public const string AdMob = "admob";
    }

    /// <summary>
    /// Ad unit đã resolve theo platform/environment khi tạo AdsOptions. Provider = mediation phục vụ unit này
    /// (một build có thể dùng cả MAX và AdMob, mỗi format một provider).
    /// </summary>
    public sealed record AdUnit(string Key, AdFormat Format, string AdUnitId, AdsProvider Provider)
    {
        public override string ToString() => Format + ":" + Key;
    }

    public enum AdLoadFailure : byte { NoFill, Network, Provider, Timeout }

    public sealed record AdLoadError(AdLoadFailure Kind, string Message, int VendorCode = 0);

    public sealed record AdPaidValue(
        double Value, string Currency, RevenuePrecision Precision, string Network, string? ImpressionId = null);

    public enum CollapseDirection : byte { None, Top, Bottom }

    /// <summary>Request hiển thị banner/MREC đã chuẩn hóa. Adapter không biết placement policy.</summary>
    public sealed record AdViewRequest(string PlacementId, BannerPosition BannerPosition, MrecPosition MrecPosition,
                                       BannerSize Size, CollapseDirection Collapse, MrecPoint? MrecPixelPosition = null);

    /// <summary>Cấu hình riêng của một provider (vd. bid floor của MAX). Core chỉ chuyển nguyên vẹn từ AdsOptions tới adapter.</summary>
    public interface IAdsProviderSettings { }

    /// <summary>Toàn bộ unit + cấu hình mọi provider của build; mỗi adapter lấy phần của mình qua UnitsOf/For.</summary>
    public sealed record AdsAdapterInitOptions(bool IsDevelopment, AdUnit[] AdUnits, IReadOnlyList<AdsProviderOptions> Providers)
    {
        /// <summary>Remote Config đang active lúc init adapter, để adapter đọc key riêng của nó (vd. cờ A/B phải chốt trước init).</summary>
        public IConfigValues RemoteConfig { get; init; } = RemoteConfigSnapshot.Empty;

        public AdUnit[] UnitsOf(AdsProvider provider) => Array.FindAll(AdUnits, u => u.Provider == provider);

        public AdsProviderOptions For(AdsProvider provider)
        {
            foreach (var options in Providers)
            {
                if (options.Provider == provider) return options;
            }
            return new AdsProviderOptions(provider);
        }
    }

    /// <summary>
    /// Callback thô từ adapter. Gọi từ thread bất kỳ; AdsManager dispatch về main thread.
    /// showOperationId = Guid.Empty khi không thuộc show operation (banner/MREC).
    /// </summary>
    public interface IAdsAdapterListener
    {
        void OnLoaded(AdUnit unit);
        void OnLoadFailed(AdUnit unit, AdLoadError error);
        void OnDisplayed(AdUnit unit, Guid showOperationId);
        void OnDisplayFailed(AdUnit unit, Guid showOperationId, string message);
        void OnRewarded(AdUnit unit, Guid showOperationId, AdReward reward);
        void OnClosed(AdUnit unit, Guid showOperationId);
        void OnClicked(AdUnit unit, Guid showOperationId);
        void OnPaid(AdUnit unit, Guid showOperationId, AdPaidValue value);
        void OnAdViewLayoutChanged(AdUnit unit, BannerLayout layout);
        void OnMrecSizeChanged(AdUnit unit, MrecSize size);
    }

    /// <summary>
    /// SPI cho MAX/AdMob. Adapter chỉ làm lifecycle vendor: init, load/show/destroy và normalize callback.
    /// State, retry, TTL, capping, gate thuộc AdsManager. Adapter không throw ra ngoài.
    /// </summary>
    public interface IAdsAdapter : IConsentApplier, IDisposable
    {
        string Id { get; }
        AdCapability Capabilities { get; }

        /// <summary>
        /// true = vendor tự xử lý ad full-screen hết hạn (tự reload, không hủy được ad đã load, vd. MAX):
        /// AdsManager không áp TTL cho unit này để khỏi load lại trên ad vendor vẫn đang giữ.
        /// </summary>
        bool ManagesExpiry(AdUnit unit);

        void SetListener(IAdsAdapterListener listener);

        /// <summary>
        /// Idempotent khi đang init/đã init xong. Lần trước lỗi thì được gọi lại để thử lại (AdsManager retry khi
        /// lỗi IsRetryable hoặc vendor không callback trong InitTimeout). Consent đã được Apply trước khi gọi.
        /// </summary>
        Task<SdkResult> InitializeAsync(AdsAdapterInitOptions options, CancellationToken ct);

        /// <summary>Full-screen.</summary>
        void Load(AdUnit unit);
        bool IsReady(AdUnit unit);
        void Show(AdUnit unit, string placementId, Guid showOperationId);
        /// <summary>Bỏ ad đã load (hết TTL, remove_ads, consent bị thu hồi).</summary>
        void Discard(AdUnit unit);

        /// <summary>Banner/MREC: tạo + load + hiển thị; vendor tự refresh.</summary>
        void ShowAdView(AdUnit unit, AdViewRequest request);
        void HideAdView(AdUnit unit);
        void DestroyAdView(AdUnit unit);

        void OpenDebugger();
    }
}
