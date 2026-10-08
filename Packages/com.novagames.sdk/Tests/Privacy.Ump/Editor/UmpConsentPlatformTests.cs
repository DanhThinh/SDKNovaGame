#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.Testing;
using NUnit.Framework;

namespace NovaGames.Mobile.Privacy.Ump.Tests
{
    public sealed class UmpConsentPlatformTests
    {
        static readonly ConsentGatherOptions Options = new ConsentGatherOptions(IsUnderAge: false, IsDevelopment: true);

        TestHarness _h = null!;
        FakeUmpApi _api = null!;
        UmpConsentPlatform _platform = null!;

        [SetUp]
        public void SetUp()
        {
            _h = new TestHarness();
            _api = new FakeUmpApi();
            _platform = new UmpConsentPlatform(_api, _h.Main, _h.Scheduler, _h.Clock, _h.Log);
        }

        [TearDown]
        public void TearDown()
        {
            _platform.Dispose();
            _h.Dispose();
        }

        [Test]
        public void Gather_UpdatesThenShowsForm_AndReturnsMappedConsent()
        {
            var task = _platform.GatherAsync(Options, CancellationToken.None);
            Assert.AreEqual(1, _api.UpdateCalls.Count);
            Assert.AreEqual(0, _api.FormCalls.Count, "form waits for the update");

            _api.CompleteUpdate(null);
            _h.Main.Drain();
            Assert.AreEqual(1, _api.FormCalls.Count);

            _api.Status = UmpConsentStatus.Obtained;
            _api.CanRequestAds = true;
            _api.Iab = IabConsentData.Empty with { GdprApplies = 1, PurposeConsents = "1111111111" };
            _api.CompleteForm(null);

            var consent = Value(_h.Run(task));
            Assert.AreEqual(Jurisdiction.Gdpr, consent.Jurisdiction);
            Assert.AreEqual(ConsentState.Granted, consent.AdPersonalization);
            Assert.IsTrue(consent.CanRequestAds);
        }

        [Test]
        public void UpdateTimesOut_ReturnsConsentStoredByUmp_WithoutShowingForm()
        {
            _api.Status = UmpConsentStatus.Obtained;
            _api.CanRequestAds = true;
            var task = _platform.GatherAsync(Options, CancellationToken.None);

            _h.Scheduler.Advance(TimeSpan.FromSeconds(11));

            Assert.IsTrue(Value(_h.Run(task)).CanRequestAds);
            Assert.AreEqual(0, _api.FormCalls.Count);
            Assert.AreEqual(1, _h.Log.Count(SdkLogLevel.Warning));
        }

        [Test]
        public void UpdateError_StillReturnsStoredConsent()
        {
            var task = _platform.GatherAsync(Options, CancellationToken.None);
            _api.CompleteUpdate("2: network");

            Assert.IsTrue(_h.Run(task).IsSuccess);
            Assert.AreEqual(0, _api.FormCalls.Count);
        }

        [Test]
        public void FormError_IsLogged_AndConsentIsStillReturned()
        {
            var task = _platform.GatherAsync(Options, CancellationToken.None);
            _api.CompleteUpdate(null);
            _h.Main.Drain();
            _api.CompleteForm("1: internal");

            Assert.IsTrue(_h.Run(task).IsSuccess);
            Assert.AreEqual(1, _h.Log.Count(SdkLogLevel.Warning));
        }

        [Test]
        public void GatherTwiceWhileRunning_SharesOneUmpRequest()
        {
            var first = _platform.GatherAsync(Options, CancellationToken.None);
            var second = _platform.GatherAsync(Options, CancellationToken.None);
            _api.CompleteUpdate(null);
            _h.Main.Drain();
            _api.CompleteForm(null);

            Assert.IsTrue(_h.Run(first).IsSuccess);
            Assert.IsTrue(_h.Run(second).IsSuccess);
            Assert.AreEqual(1, _api.UpdateCalls.Count);
        }

        [Test]
        public void PrivacyOptionsWhileGathering_IsBusy()
        {
            _platform.GatherAsync(Options, CancellationToken.None);

            var result = _h.Run(_platform.ShowPrivacyOptionsAsync(CancellationToken.None));

            Assert.AreEqual(SdkErrorCategory.Busy, result.Error!.Category);
        }

        [Test]
        public void PrivacyOptions_ReturnsNewConsent()
        {
            var task = _platform.ShowPrivacyOptionsAsync(CancellationToken.None);
            _api.CanRequestAds = false;
            _api.Status = UmpConsentStatus.Obtained;
            _api.CompletePrivacyOptions(null);

            Assert.IsFalse(Value(_h.Run(task)).CanRequestAds);
        }

        [Test]
        public void UnderAge_IsPassedToUmpAndKeptInSnapshot()
        {
            var options = Options with { IsUnderAge = true };
            Assert.IsTrue(_platform.ReadStored(options).IsUnderAge);

            _platform.GatherAsync(options, CancellationToken.None);
            Assert.IsTrue(_api.UpdateCalls[0].IsUnderAge);
        }

        [Test]
        public void VendorThrows_IsReportedAsError_NotThrown()
        {
            _api.ThrowOnUpdate = true;
            var result = _h.Run(_platform.GatherAsync(Options, CancellationToken.None));

            Assert.IsTrue(result.IsSuccess, "falls back to stored consent");
            Assert.AreEqual(0, _api.FormCalls.Count);
        }

        [Test]
        public void Dispose_CancelsPendingGather()
        {
            var task = _platform.GatherAsync(Options, CancellationToken.None);
            _platform.Dispose();

            Assert.AreEqual(SdkErrorCategory.Cancelled, _h.Run(task).Error!.Category);
            Assert.IsFalse(_platform.IsPrivacyOptionsRequired);
        }

        static ConsentSnapshot Value(SdkResult<ConsentSnapshot> result)
        {
            Assert.IsTrue(result.TryGetValue(out var value), result.ToString());
            return value!;
        }

        sealed class FakeUmpApi : IUmpApi
        {
            public readonly List<ConsentGatherOptions> UpdateCalls = new List<ConsentGatherOptions>();
            public readonly List<Action<string?>> FormCalls = new List<Action<string?>>();
            Action<string?>? _update;
            Action<string?>? _privacyOptions;

            public UmpConsentStatus Status = UmpConsentStatus.Unknown;
            public IabConsentData Iab = IabConsentData.Empty;
            public bool ThrowOnUpdate;

            public UmpConsentStatus ConsentStatus => Status;
            public bool CanRequestAds { get; set; }
            public bool IsPrivacyOptionsRequired { get; set; }

            public void RequestConsentInfoUpdate(ConsentGatherOptions options, Action<string?> onDone)
            {
                if (ThrowOnUpdate) throw new InvalidOperationException("UMP not available");
                UpdateCalls.Add(options);
                _update = onDone;
            }

            public void LoadAndShowConsentFormIfRequired(Action<string?> onDone) => FormCalls.Add(onDone);
            public void ShowPrivacyOptionsForm(Action<string?> onDone) => _privacyOptions = onDone;
            public IabConsentData ReadIabData() => Iab;

            public void CompleteUpdate(string? error) => _update!(error);
            public void CompleteForm(string? error) => FormCalls[FormCalls.Count - 1](error);
            public void CompletePrivacyOptions(string? error) => _privacyOptions!(error);
        }
    }
}
