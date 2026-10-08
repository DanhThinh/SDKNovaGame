#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.Privacy;

namespace NovaGames.Mobile.Tracking
{
    public enum TrackingParamKind : byte { String, Long, Double }

    /// <summary>Param của event, typed (không boxing). Tạo bằng <c>TrackingParam.Of(name, value)</c>.</summary>
    public readonly struct TrackingParam
    {
        TrackingParam(string name, TrackingParamKind kind, string? stringValue, long longValue, double doubleValue)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Kind = kind;
            StringValue = stringValue;
            LongValue = longValue;
            DoubleValue = doubleValue;
        }

        public string Name { get; }
        public TrackingParamKind Kind { get; }
        public string? StringValue { get; }
        public long LongValue { get; }
        public double DoubleValue { get; }

        public static TrackingParam Of(string name, string value) => new TrackingParam(name, TrackingParamKind.String, value ?? string.Empty, 0, 0);
        public static TrackingParam Of(string name, long value) => new TrackingParam(name, TrackingParamKind.Long, null, value, 0);
        public static TrackingParam Of(string name, int value) => new TrackingParam(name, TrackingParamKind.Long, null, value, 0);
        public static TrackingParam Of(string name, double value) => new TrackingParam(name, TrackingParamKind.Double, null, 0, value);
        /// <summary>bool gửi dạng số 1/0 (Firebase không có kiểu bool).</summary>
        public static TrackingParam Of(string name, bool value) => new TrackingParam(name, TrackingParamKind.Long, null, value ? 1 : 0, 0);

        public override string ToString() => Kind switch
        {
            TrackingParamKind.String => Name + "=\"" + StringValue + "\"",
            TrackingParamKind.Long => Name + "=" + LongValue,
            _ => Name + "=" + DoubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Một event analytics. Game thường gửi qua <c>NovaAnalytics.LogEvent</c>.</summary>
    public sealed record TrackingEvent(string Name, IReadOnlyList<TrackingParam> Params)
    {
        public static readonly IReadOnlyList<TrackingParam> NoParams = Array.Empty<TrackingParam>();

        public TrackingEvent(string name) : this(name, NoParams) { }
    }

    public enum RevenuePrecision : byte { Unknown, Estimated, PublisherDefined, Precise }

    /// <summary>Doanh thu một impression quảng cáo. Mediation = "max" / "admob"; Network = ad network thực sự fill.</summary>
    public sealed record AdRevenueEvent(
        Guid EventId, Guid ShowOperationId, AdFormat Format, string PlacementId,
        string Mediation, string Network, string AdUnitId,
        double Value, string Currency, RevenuePrecision Precision, string? ImpressionId = null);

    /// <summary>Doanh thu một giao dịch in-app purchase đã xác nhận.</summary>
    public sealed record PurchaseRevenueEvent(
        Guid EventId, string TransactionId, string ProductId,
        double Value, string Currency, int Quantity = 1);

    /// <summary>Những gì một sink nhận; NovaAnalytics chỉ gửi tới sink có capability tương ứng.</summary>
    [Flags]
    public enum SinkCapabilities
    {
        None = 0,
        Events = 1 << 0,
        AdRevenue = 1 << 1,
        Purchase = 1 << 2,
        UserProperties = 1 << 3,
        /// <summary>Sink tự xử lý consent (Consent Mode v2): event vẫn được gửi khi consent bị từ chối, vendor tự quyết định.</summary>
        ConsentMode = 1 << 4,
    }

    /// <summary>
    /// SPI cho một đích tracking (Firebase Analytics, Adjust). At-most-once: "đã gửi" = lời gọi API vendor trả về;
    /// sink không retry. Sink chưa Ready thì bỏ event (NovaAnalytics giữ lại và gửi sau).
    /// </summary>
    public interface ITrackingSink : IConsentApplier, IDisposable
    {
        string Id { get; }
        bool IsReady { get; }
        SinkCapabilities Capabilities { get; }

        /// <summary>Idempotent. Token chỉ hủy việc chờ của caller.</summary>
        Task<SdkResult> InitializeAsync(CancellationToken ct);

        void Send(TrackingEvent e);
        void SendAdRevenue(AdRevenueEvent e);
        void SendPurchase(PurchaseRevenueEvent e);
    }

    /// <summary>Ads/IAP không gọi vendor tracking trực tiếp; doanh thu chỉ đi qua pipeline này.</summary>
    public interface IRevenuePipeline
    {
        void ReportAdRevenue(AdRevenueEvent e);
        void ReportPurchase(PurchaseRevenueEvent e);
    }

    /// <summary>Sink hỗ trợ user property/user id (latest-wins, được giữ lại tới khi sink sẵn sàng).</summary>
    public interface IUserPropertySink
    {
        void SetUserProperty(string name, string? value);
        void SetUserId(string? id);
    }

    /// <summary>
    /// Cấu hình riêng của một sink (vd. app token/event token Adjust). Core chỉ chuyển nguyên vẹn tới factory của sink
    /// qua <c>RuntimeSdkSettings.SinkSettings</c>.
    /// </summary>
    public interface ITrackingSinkSettings { }

    public static class TrackingSinkIds
    {
        public const string Firebase = "firebase";
        public const string Adjust = "adjust";
    }

    public sealed record AnalyticsOptions(TimeSpan InitTimeout, TimeSpan? SessionTimeout = null, bool? CollectionEnabled = null)
    {
        public static AnalyticsOptions Default { get; } = new AnalyticsOptions(TimeSpan.FromSeconds(5));
    }
}
