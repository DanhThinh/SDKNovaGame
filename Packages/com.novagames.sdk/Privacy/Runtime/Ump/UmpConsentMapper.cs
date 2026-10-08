#nullable enable
using System;

namespace NovaGames.Mobile.Privacy.Ump
{
    internal enum UmpConsentStatus : byte { Unknown, NotRequired, Required, Obtained }

    /// <summary>Chuỗi IAB mà UMP ghi vào bộ nhớ của app sau khi người dùng trả lời form (chuẩn IAB TCF v2 và GPP).</summary>
    internal sealed record IabConsentData(int? GdprApplies, string PurposeConsents, string TcString, string GppString, string GppSectionIds)
    {
        public static IabConsentData Empty { get; } = new IabConsentData(null, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    /// <summary>Đổi trạng thái UMP + chuỗi IAB thành <see cref="ConsentSnapshot"/>. Hàm thuần, không gọi Unity/vendor.</summary>
    internal static class UmpConsentMapper
    {
        // Mục đích TCF v2: 1 = lưu/đọc thông tin trên thiết bị, 3 = tạo hồ sơ quảng cáo cá nhân hóa,
        // 4 = dùng hồ sơ đó để chọn quảng cáo, 7 = đo hiệu quả quảng cáo.
        const int StoreOnDevice = 1;
        const int CreatePersonalisedProfile = 3;
        const int UsePersonalisedProfile = 4;
        const int MeasureAdPerformance = 7;

        // GPP section ID: 2 = TCF EU, 5 = TCF Canada, 6 = US Privacy (CCPA), từ 7 trở đi = luật US (usnat, usca, usva, ...).
        const int FirstUsSection = 6;

        /// <summary>
        /// Chưa giải mã chuỗi GPP nên <c>UsDoNotSell</c> luôn false (GMA tự đọc GPP; MAX/Adjust chưa nhận opt-out US).
        /// UMP không đọc ATT: snapshot để Att = NotApplicable, trên iOS NovaSdk thay bằng trạng thái ATT thật của máy.
        /// </summary>
        public static ConsentSnapshot Map(UmpConsentStatus status, bool canRequestAds, IabConsentData iab, bool isUnderAge,
                                          DateTime nowUtc)
        {
            var snapshot = ConsentSnapshot.Unknown with
            {
                IsUnderAge = isUnderAge,
                CanRequestAds = canRequestAds,
                HasTcfString = iab.TcString.Length > 0,
                HasGppString = iab.GppString.Length > 0,
                Att = AttStatus.NotApplicable,
                UpdatedUtc = nowUtc,
            };
            if (status == UmpConsentStatus.Unknown) return snapshot;

            if (iab.GdprApplies == 1)
            {
                // Người dùng chưa trả lời form (hoặc form lỗi): giữ Unknown, CanRequestAds của UMP sẽ là false.
                if (iab.PurposeConsents.Length == 0) return snapshot with { Jurisdiction = Jurisdiction.Gdpr };

                bool storage = HasPurpose(iab.PurposeConsents, StoreOnDevice);
                return snapshot with
                {
                    Jurisdiction = Jurisdiction.Gdpr,
                    AdStorage = State(storage),
                    AnalyticsStorage = State(storage),
                    AdPersonalization = State(HasPurpose(iab.PurposeConsents, CreatePersonalisedProfile)
                                              && HasPurpose(iab.PurposeConsents, UsePersonalisedProfile)),
                    AdUserData = State(storage && HasPurpose(iab.PurposeConsents, MeasureAdPerformance)),
                };
            }

            // UMP báo cần consent nhưng chưa ghi chuỗi IAB (form lỗi, chưa publish message GDPR): chưa biết vùng, không coi là
            // "không cần consent" để tránh gửi Granted cho người dùng EEA chưa trả lời.
            if (status == UmpConsentStatus.Required && iab.GdprApplies is null && !HasUsSection(iab.GppSectionIds))
                return snapshot;

            var jurisdiction = HasUsSection(iab.GppSectionIds) ? Jurisdiction.UsState
                : status == UmpConsentStatus.NotRequired ? Jurisdiction.None
                : Jurisdiction.Other;
            return snapshot with
            {
                Jurisdiction = jurisdiction,
                AdStorage = ConsentState.NotRequired,
                AnalyticsStorage = ConsentState.NotRequired,
                AdPersonalization = ConsentState.NotRequired,
                AdUserData = ConsentState.NotRequired,
            };
        }

        /// <summary>IABTCF_PurposeConsents: chuỗi '0'/'1', ký tự thứ N là mục đích N.</summary>
        internal static bool HasPurpose(string purposeConsents, int purpose) =>
            purpose >= 1 && purpose <= purposeConsents.Length && purposeConsents[purpose - 1] == '1';

        /// <summary>IABGPP_GppSID: các section ID cách nhau bởi '_', vd. "7_8".</summary>
        internal static bool HasUsSection(string gppSectionIds)
        {
            foreach (var part in gppSectionIds.Split('_'))
            {
                if (int.TryParse(part, out var id) && id >= FirstUsSection) return true;
            }
            return false;
        }

        static ConsentState State(bool granted) => granted ? ConsentState.Granted : ConsentState.Denied;
    }
}
