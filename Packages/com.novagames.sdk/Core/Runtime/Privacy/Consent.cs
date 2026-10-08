#nullable enable
using System;

namespace NovaGames.Mobile.Privacy
{
    public enum ConsentState : byte { Unknown, NotRequired, Granted, Denied }
    public enum AttStatus : byte { NotApplicable, NotDetermined, Restricted, Denied, Authorized }
    public enum Jurisdiction : byte { Unknown, None, Gdpr, UsState, Other }

    public sealed record ConsentSnapshot(
        int SchemaVersion, Jurisdiction Jurisdiction, bool IsUnderAge,
        ConsentState AnalyticsStorage, ConsentState AdStorage,
        ConsentState AdUserData, ConsentState AdPersonalization,
        bool UsDoNotSell, bool HasTcfString, bool HasGppString,
        AttStatus Att, bool CanRequestAds, string PolicyVersion, DateTime UpdatedUtc)
    {
        public const int CurrentSchemaVersion = 1;

        public static ConsentSnapshot Unknown { get; } = new ConsentSnapshot(
            CurrentSchemaVersion, Jurisdiction.Unknown, false,
            ConsentState.Unknown, ConsentState.Unknown, ConsentState.Unknown, ConsentState.Unknown,
            false, false, false, AttStatus.NotDetermined, false, string.Empty, DateTime.MinValue);

        /// <summary>
        /// Đồng ý mọi mục đích, ngoài vùng bắt buộc consent. Chỉ dùng để test hoặc khi game chưa có luồng consent (UMP);
        /// bản phát hành ở EEA/UK/US phải dùng consent thật.
        /// </summary>
        public static ConsentSnapshot AllGranted { get; } = Unknown with
        {
            Jurisdiction = Jurisdiction.None,
            AnalyticsStorage = ConsentState.Granted,
            AdStorage = ConsentState.Granted,
            AdUserData = ConsentState.Granted,
            AdPersonalization = ConsentState.Granted,
            Att = AttStatus.NotApplicable,
            CanRequestAds = true,
        };
    }

    /// <summary>Privacy push snapshot tới adapter trước khi adapter init và mỗi khi consent đổi.</summary>
    public interface IConsentApplier
    {
        void Apply(ConsentSnapshot snapshot);
    }
}
