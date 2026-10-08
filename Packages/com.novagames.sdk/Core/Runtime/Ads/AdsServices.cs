#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NovaGames.Mobile.Ads
{
    /// <summary>
    /// API Ads đầy đủ (Task, kết quả chi tiết, sự kiện). Game thông thường dùng <c>NovaAds</c>; lấy interface này qua
    /// <c>NovaAds.Service</c> khi cần chi tiết. Mọi API gọi trên main thread. Không throw: lỗi trả qua Outcome/SdkResult.
    /// </summary>
    public interface IAdsService
    {
        ISdkProperty<AdsModuleState> State { get; }

        /// <summary>Provider đang dùng có hỗ trợ capability (vd. CollapsibleBanner) không.</summary>
        bool Supports(AdCapability capability);

        IFullScreenAds FullScreen { get; }
        IBannerAds Banners { get; }
        IMrecAds Mrec { get; }
        IAppOpenAds AppOpen { get; }

        /// <summary>Mỗi impression có doanh thu (đã chuyển tới tracking qua revenue pipeline).</summary>
        ISdkEvent<AdImpression> Impressions { get; }

        /// <summary>Full-screen ad bắt đầu hiện / đã đóng.</summary>
        ISdkEvent<FullScreenAdPresentation> Presentation { get; }

        /// <summary>Game tự pause/resume (audio, time scale) khi full-screen ad hiện. null = bỏ.</summary>
        void SetPresentationHandler(IAdPresentationHandler? handler);

        /// <summary>Level hiện tại cho capping <c>ad_inter_start_level</c>. Chưa gọi = chưa đạt ngưỡng.</summary>
        void SetPlayerLevel(int level);

        /// <summary>MAX Mediation Debugger / AdMob Ad Inspector. Chỉ Development build, Production là no-op.</summary>
        void OpenDebugger();
    }

    /// <summary>Interstitial và rewarded.</summary>
    public interface IFullScreenAds
    {
        /// <summary>Show được ngay không; chưa có ad thì tự bắt đầu load.</summary>
        AdAvailability GetAvailability(FullScreenPlacement placement);

        ISdkEvent<AdAvailabilityChanged> AvailabilityChanged { get; }

        /// <summary>Chờ ad load xong (tối đa LoadTimeout). Ad thường đã tự preload, chỉ cần khi muốn chờ chắc chắn.</summary>
        Task<SdkResult> PreloadAsync(FullScreenPlacement placement, CancellationToken ct);

        /// <summary>Show interstitial; Task kết thúc khi ad đóng hoặc không show được.</summary>
        Task<InterstitialResult> ShowAsync(InterstitialPlacement placement, CancellationToken ct);

        /// <summary>Show rewarded; chỉ cấp thưởng khi <c>result.IsRewarded</c>.</summary>
        Task<RewardedResult> ShowAsync(RewardedPlacement placement, RewardedShowOptions options, CancellationToken ct);
    }

    public interface IBannerAds
    {
        /// <summary>Hiện banner (gọi lại với cùng options là no-op; options khác thì tạo lại view).</summary>
        SdkResult Show(BannerPlacement placement, BannerOptions options);

        /// <summary>Như trên với <c>AdsOptions.DefaultBanner</c> (vị trí chọn trong Ads Config).</summary>
        SdkResult Show(BannerPlacement placement);

        void Hide(BannerPlacement placement);
        void Destroy(BannerPlacement placement);
        AdViewState GetState(BannerPlacement placement);
        BannerLayout GetLayout(BannerPlacement placement);
        ISdkEvent<BannerLayoutChanged> LayoutChanged { get; }
    }

    public interface IMrecAds
    {
        SdkResult Show(MrecPlacement placement, MrecOptions options);
        void Hide(MrecPlacement placement);
        void Destroy(MrecPlacement placement);
        AdViewState GetState(MrecPlacement placement);
        MrecSize GetSize(MrecPlacement placement);
        ISdkEvent<MrecSizeChanged> SizeChanged { get; }
    }

    public interface IAppOpenAds
    {
        AdAvailability GetAvailability(AppOpenPlacement placement);

        /// <summary>Chặn app open tới khi Dispose handle (mua hàng, popup hệ thống, màn force update, ...).</summary>
        IDisposable Suppress(string reason);

        Task<AppOpenResult> TryShowAsync(AppOpenPlacement placement, AppOpenTrigger trigger, CancellationToken ct);

        /// <summary>Tự show khi quay lại app; null = tắt, game tự gọi TryShowAsync.</summary>
        void SetAutoShowOnResume(AppOpenPlacement? placement);
    }

    /// <summary>Game pause/resume audio và time scale; SDK không tự đổi Time.timeScale/AudioListener.</summary>
    public interface IAdPresentationHandler
    {
        void OnFullScreenShowing(AdFormat format);
        void OnFullScreenClosed(AdFormat format);
    }
}
