#nullable enable
using System.Globalization;
using AdjustSdk;
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.Tracking;

namespace NovaGames.Mobile.Attribution
{
    internal static class AdjustMapping
    {
        public const string Provider = "adjust";

        // Source chuẩn của AdjustAdRevenue theo mediation (AdRevenueEvent.Mediation = AdProviderIds).
        public static string AdRevenueSource(string? mediation) => mediation switch
        {
            AdProviderIds.Max => "applovin_max_sdk",
            AdProviderIds.AdMob => "admob_sdk",
            _ => "publisher_sdk",
        };

        public static string ParamValue(TrackingParam p) => p.Kind switch
        {
            TrackingParamKind.String => p.StringValue ?? string.Empty,
            TrackingParamKind.Long => p.LongValue.ToString(CultureInfo.InvariantCulture),
            _ => p.DoubleValue.ToString("R", CultureInfo.InvariantCulture),
        };

        // null khi vendor chưa có attribution (install mới, backend chưa trả).
        public static AttributionData? Attribution(AdjustAttribution? a)
        {
            if (a is null || (string.IsNullOrEmpty(a.TrackerToken) && string.IsNullOrEmpty(a.Network))) return null;
            return new AttributionData(Provider, Blank(a.TrackerToken), Blank(a.TrackerName), Blank(a.Network),
                Blank(a.Campaign), Blank(a.Adgroup), Blank(a.Creative), Blank(a.ClickLabel));
        }

        static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }

    // Trạng thái consent gửi Adjust: third-party sharing (+ Google DMA) và measurement consent.
    internal sealed record AdjustConsentState(
        bool SharingEnabled, string? Eea, string? AdPersonalization, string? AdUserData, bool? MeasurementConsent)
    {
        const string GoogleDma = "google_dma";

        // null = chưa biết gì về consent (giữ mặc định của Adjust, không gửi).
        public static AdjustConsentState? From(ConsentSnapshot s)
        {
            bool known = s.Jurisdiction != Jurisdiction.Unknown || s.UsDoNotSell || s.IsUnderAge;
            if (!known) return null;

            bool gdpr = s.Jurisdiction == Jurisdiction.Gdpr;
            // Tắt chia sẻ cho mọi partner: do-not-sell (US), trẻ em, hoặc GDPR từ chối gửi dữ liệu quảng cáo.
            bool sharing = !s.UsDoNotSell && !s.IsUnderAge && !(gdpr && s.AdUserData == ConsentState.Denied);

            string? eea = s.Jurisdiction == Jurisdiction.Unknown ? null : gdpr ? "1" : "0";
            return new AdjustConsentState(
                sharing, eea,
                gdpr ? Flag(s.AdPersonalization) : null,
                gdpr ? Flag(s.AdUserData) : null,
                gdpr ? Measurement(s.AnalyticsStorage) : null);
        }

        static string? Flag(ConsentState state) => state switch
        {
            ConsentState.Granted => "1",
            ConsentState.NotRequired => "1",
            ConsentState.Denied => "0",
            _ => null,
        };

        static bool? Measurement(ConsentState state) => state switch
        {
            ConsentState.Granted => true,
            ConsentState.NotRequired => true,
            ConsentState.Denied => false,
            _ => null,
        };

        // Lưu để không gửi lại khi consent không đổi giữa các lần mở app (Adjust lưu trạng thái ở backend).
        public string Signature =>
            (SharingEnabled ? "1" : "0") + "|" + Eea + "|" + AdPersonalization + "|" + AdUserData + "|"
            + (MeasurementConsent.HasValue ? (MeasurementConsent.Value ? "1" : "0") : string.Empty);

        public AdjustThirdPartySharing ToThirdPartySharing()
        {
            var sharing = new AdjustThirdPartySharing(SharingEnabled);
            if (Eea != null) sharing.AddGranularOption(GoogleDma, "eea", Eea);
            if (AdPersonalization != null) sharing.AddGranularOption(GoogleDma, "ad_personalization", AdPersonalization);
            if (AdUserData != null) sharing.AddGranularOption(GoogleDma, "ad_user_data", AdUserData);
            return sharing;
        }
    }
}
