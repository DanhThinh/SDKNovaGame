#nullable enable

namespace NovaGames.Mobile.Ads
{
    public enum BannerPosition : byte { Top, Bottom }

    /// <summary>AdaptiveAnchored = cao theo chiều rộng màn hình (khuyến nghị); Standard = 320x50.</summary>
    public enum BannerSize : byte { AdaptiveAnchored, Standard }

    /// <summary>Collapsible banner (chỉ AdMob hỗ trợ).</summary>
    public enum CollapsiblePolicy : byte
    {
        None,
        /// <summary>Provider không hỗ trợ: hiển thị banner thường, <c>BannerLayout.IsCollapsible = false</c>.</summary>
        Preferred,
        /// <summary>Provider không hỗ trợ: trả Unsupported, không hiển thị gì.</summary>
        Required,
    }

    /// <summary>Tùy chọn banner. Hướng collapse suy ra từ Position (Top -> collapsible=top, Bottom -> collapsible=bottom).</summary>
    public sealed record BannerOptions(BannerPosition Position, BannerSize Size = BannerSize.AdaptiveAnchored,
                                       CollapsiblePolicy Collapsible = CollapsiblePolicy.None);

    /// <summary>Kích thước banner hiện tại để game đẩy UI (dp và pixel).</summary>
    public sealed record BannerLayout(float HeightDp, float HeightPx, bool IsCollapsible, bool IsExpanded)
    {
        public static BannerLayout None { get; } = new BannerLayout(0, 0, false, false);
    }

    public sealed record BannerLayoutChanged(BannerPlacement Placement, BannerLayout Layout);

    public enum MrecPosition : byte { TopCenter, Centered, BottomCenter, Custom }

    /// <summary>Tọa độ pixel tính từ góc trên-trái; MAX đặt view trong safe area theo quy ước của vendor.</summary>
    public sealed record MrecPoint(float X, float Y);

    public sealed record MrecOptions(MrecPosition Position, MrecPoint? PixelPosition = null)
    {
        /// <summary>Đặt MREC tại tọa độ pixel tính từ góc trên-trái màn hình.</summary>
        public static MrecOptions AtPixels(float x, float y) =>
            new MrecOptions(MrecPosition.Custom, new MrecPoint(x, y));
    }

    /// <summary>MREC chuẩn luôn là 300x250 dp; kích thước pixel phụ thuộc density của thiết bị.</summary>
    public sealed record MrecSize(float WidthDp, float HeightDp, float WidthPx, float HeightPx)
    {
        public const float StandardWidthDp = 300f;
        public const float StandardHeightDp = 250f;
        public static MrecSize None { get; } = new MrecSize(0, 0, 0, 0);
        /// <summary>Dùng ngay sau Show; WidthPx/HeightPx được cập nhật khi adapter đo được density/layout thật.</summary>
        public static MrecSize Standard { get; } = new MrecSize(StandardWidthDp, StandardHeightDp, 0, 0);
    }

    public sealed record MrecSizeChanged(MrecPlacement Placement, MrecSize Size);

    /// <summary>Trạng thái view banner/MREC.</summary>
    public enum AdViewState : byte { None, Loading, Visible, Hidden, Failed, Destroyed }
}
