#nullable enable

namespace NovaGames.Mobile.Entitlements
{
    public sealed record EntitlementId(string Value)
    {
        public static EntitlementId RemoveAds { get; } = new EntitlementId("remove_ads");

        public override string ToString() => Value;
    }

    /// <summary>Ads chỉ phụ thuộc interface này để biết remove_ads, không phụ thuộc package IAP.</summary>
    public interface IEntitlementProvider
    {
        bool IsActive(EntitlementId id);
        ISdkEvent<EntitlementId> Changed { get; }
    }

    /// <summary>Provider rỗng khi game chưa có IAP: không entitlement nào active.</summary>
    public sealed class NoEntitlements : IEntitlementProvider
    {
        public static NoEntitlements Instance { get; } = new NoEntitlements();

        readonly SdkEvent<EntitlementId> _changed = new SdkEvent<EntitlementId>();

        NoEntitlements() { }

        public bool IsActive(EntitlementId id) => false;
        public ISdkEvent<EntitlementId> Changed => _changed;
    }
}
