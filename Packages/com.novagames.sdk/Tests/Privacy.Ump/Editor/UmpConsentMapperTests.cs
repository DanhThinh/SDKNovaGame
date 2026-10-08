#nullable enable
using System;
using NUnit.Framework;

namespace NovaGames.Mobile.Privacy.Ump.Tests
{
    public sealed class UmpConsentMapperTests
    {
        static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        static IabConsentData Gdpr(string purposes) =>
            IabConsentData.Empty with { GdprApplies = 1, PurposeConsents = purposes, TcString = "CP-tc" };

        [Test]
        public void UnknownStatus_IsUnknown_ButKeepsUnderAgeAndCanRequestAds()
        {
            var s = UmpConsentMapper.Map(UmpConsentStatus.Unknown, false, IabConsentData.Empty, isUnderAge: true, Now);

            Assert.AreEqual(Jurisdiction.Unknown, s.Jurisdiction);
            Assert.AreEqual(ConsentState.Unknown, s.AdStorage);
            Assert.IsTrue(s.IsUnderAge);
            Assert.IsFalse(s.CanRequestAds);
            Assert.AreEqual(Now, s.UpdatedUtc);
        }

        [Test]
        public void Gdpr_AllPurposesAccepted_GrantsEverything()
        {
            var s = UmpConsentMapper.Map(UmpConsentStatus.Obtained, true, Gdpr("1111111111"), false, Now);

            Assert.AreEqual(Jurisdiction.Gdpr, s.Jurisdiction);
            Assert.AreEqual(ConsentState.Granted, s.AdStorage);
            Assert.AreEqual(ConsentState.Granted, s.AnalyticsStorage);
            Assert.AreEqual(ConsentState.Granted, s.AdPersonalization);
            Assert.AreEqual(ConsentState.Granted, s.AdUserData);
            Assert.IsTrue(s.HasTcfString);
            Assert.IsTrue(s.CanRequestAds);
        }

        [Test]
        public void Gdpr_RejectAll_DeniesEverything()
        {
            var s = UmpConsentMapper.Map(UmpConsentStatus.Obtained, true, Gdpr("0000000000"), false, Now);

            Assert.AreEqual(ConsentState.Denied, s.AdStorage);
            Assert.AreEqual(ConsentState.Denied, s.AnalyticsStorage);
            Assert.AreEqual(ConsentState.Denied, s.AdPersonalization);
            Assert.AreEqual(ConsentState.Denied, s.AdUserData);
            Assert.IsTrue(s.CanRequestAds, "UMP still allows non-personalised ads after an answer");
        }

        [Test]
        public void Gdpr_StorageOnly_DeniesPersonalisationAndAdUserData()
        {
            // Mục đích 1 đồng ý, 3/4/7 từ chối.
            var s = UmpConsentMapper.Map(UmpConsentStatus.Obtained, true, Gdpr("1100000000"), false, Now);

            Assert.AreEqual(ConsentState.Granted, s.AdStorage);
            Assert.AreEqual(ConsentState.Denied, s.AdPersonalization);
            Assert.AreEqual(ConsentState.Denied, s.AdUserData);
        }

        [Test]
        public void Required_WithoutIabData_StaysUnknown_NotTreatedAsNotRequired()
        {
            var s = UmpConsentMapper.Map(UmpConsentStatus.Required, false, IabConsentData.Empty, false, Now);

            Assert.AreEqual(Jurisdiction.Unknown, s.Jurisdiction);
            Assert.AreEqual(ConsentState.Unknown, s.AdPersonalization);
            Assert.AreEqual(ConsentState.Unknown, s.AdUserData);
        }

        [Test]
        public void Gdpr_NotAnsweredYet_StaysUnknown()
        {
            var s = UmpConsentMapper.Map(UmpConsentStatus.Required, false, Gdpr(""), false, Now);

            Assert.AreEqual(Jurisdiction.Gdpr, s.Jurisdiction);
            Assert.AreEqual(ConsentState.Unknown, s.AdStorage);
            Assert.IsFalse(s.CanRequestAds);
        }

        [Test]
        public void UsStateSection_IsUsState_WithConsentNotRequired()
        {
            var iab = IabConsentData.Empty with { GdprApplies = 0, GppString = "DBABL~BVQqAAAAAgA", GppSectionIds = "7_8" };
            var s = UmpConsentMapper.Map(UmpConsentStatus.Obtained, true, iab, false, Now);

            Assert.AreEqual(Jurisdiction.UsState, s.Jurisdiction);
            Assert.AreEqual(ConsentState.NotRequired, s.AdStorage);
            Assert.IsTrue(s.HasGppString);
        }

        [Test]
        public void ConsentNotRequired_OutsideRegulatedRegions_IsNone()
        {
            var s = UmpConsentMapper.Map(UmpConsentStatus.NotRequired, true, IabConsentData.Empty, false, Now);

            Assert.AreEqual(Jurisdiction.None, s.Jurisdiction);
            Assert.AreEqual(ConsentState.NotRequired, s.AdPersonalization);
            Assert.AreEqual(AttStatus.NotApplicable, s.Att);
        }

        [TestCase("", false)]
        [TestCase("2", false)]
        [TestCase("2_5", false)]
        [TestCase("6", true)]
        [TestCase("2_7", true)]
        [TestCase("x_12", true)]
        public void HasUsSection(string ids, bool expected) => Assert.AreEqual(expected, UmpConsentMapper.HasUsSection(ids));

        [TestCase("1", 1, true)]
        [TestCase("10", 2, false)]
        [TestCase("1", 3, false)]
        [TestCase("111", 0, false)]
        public void HasPurpose(string purposes, int purpose, bool expected) =>
            Assert.AreEqual(expected, UmpConsentMapper.HasPurpose(purposes, purpose));
    }
}
