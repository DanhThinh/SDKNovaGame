#nullable enable

namespace NovaGames.Mobile.Tracking
{
    /// <summary>Attribution thô từ vendor (Adjust). Sink không tự phân loại organic/paid.</summary>
    public sealed record AttributionData(
        string Provider, string? TrackerToken, string? TrackerName, string? Network,
        string? Campaign, string? Adgroup, string? Creative, string? ClickLabel);

    /// <summary>IsDeferred = link về từ install (deferred deep link), false = app được mở bằng link.</summary>
    public sealed record DeepLink(string Url, bool IsDeferred);

    /// <summary>Callback luôn trên main thread.</summary>
    public interface IAttributionListener
    {
        void OnAttribution(AttributionData data);
        void OnDeepLink(DeepLink link);
    }

    /// <summary>
    /// Sink attribution báo attribution/deep link cho listener. Dữ liệu tới trước khi có listener được giữ lại
    /// (attribution mới nhất + deep link cold start) và phát lại khi SetListener.
    /// </summary>
    public interface IAttributionSink
    {
        void SetListener(IAttributionListener? listener);
    }
}
