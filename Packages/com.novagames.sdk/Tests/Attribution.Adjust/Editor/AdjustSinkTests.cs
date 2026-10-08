#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AdjustSdk;
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.Testing;
using NovaGames.Mobile.Tracking;
using NUnit.Framework;

namespace NovaGames.Mobile.Attribution.Tests
{
    public sealed class AdjustSinkTests
    {
        const string AppToken = "abcdefghijkl";

        TestHarness _h = null!;
        FakeAdjustApi _api = null!;

        [SetUp]
        public void SetUp()
        {
            _h = new TestHarness();
            _api = new FakeAdjustApi();
        }

        [TearDown] public void TearDown() => _h.Dispose();

        static AdjustSinkSettings Settings(bool sandbox = true) => new AdjustSinkSettings(AppToken, sandbox)
        {
            EventTokens = new Dictionary<string, string>
            {
                ["level_complete"] = "lvl001",
                ["purchase"] = "pur001",
            },
        };

        AdjustSink CreateSink(AdjustSinkSettings? settings = null, bool android = true) =>
            new AdjustSink(settings ?? Settings(), _api, _h.Main, _h.Store, _h.Log, android);

        static ConsentSnapshot NonGdpr() => ConsentSnapshot.Unknown with
        {
            Jurisdiction = Jurisdiction.None,
            AnalyticsStorage = ConsentState.NotRequired,
            AdStorage = ConsentState.NotRequired,
            AdUserData = ConsentState.NotRequired,
            AdPersonalization = ConsentState.NotRequired,
        };

        static ConsentSnapshot Gdpr(ConsentState analytics, ConsentState adUserData, ConsentState adPersonalization) =>
            ConsentSnapshot.Unknown with
            {
                Jurisdiction = Jurisdiction.Gdpr,
                AnalyticsStorage = analytics,
                AdStorage = adUserData,
                AdUserData = adUserData,
                AdPersonalization = adPersonalization,
            };

        AdjustSink CreateReadySink(ConsentSnapshot? consent = null, AdjustSinkSettings? settings = null, bool android = true)
        {
            var sink = CreateSink(settings, android);
            sink.Apply(consent ?? NonGdpr());
            var init = _h.Run(sink.InitializeAsync(CancellationToken.None));
            Assert.IsTrue(init.IsSuccess, init.ToString());
            return sink;
        }

        static List<string> Pairs(IEnumerable<string>? flat) => flat?.ToList() ?? new List<string>();

        [Test]
        public void Initialize_WithoutSettings_FailsWithConfiguration()
        {
            var sink = new AdjustSink(null, _api, _h.Main, _h.Store, _h.Log, true);

            var result = _h.Run(sink.InitializeAsync(CancellationToken.None));

            Assert.AreEqual(SdkErrorCategory.Configuration, result.Error!.Category);
            Assert.IsEmpty(_api.Calls);
        }

        [Test]
        public void Initialize_EmptyAppToken_FailsWithConfiguration()
        {
            var sink = CreateSink(new AdjustSinkSettings(string.Empty, true));

            var result = _h.Run(sink.InitializeAsync(CancellationToken.None));

            Assert.AreEqual(SdkErrorCategory.Configuration, result.Error!.Category);
        }

        [Test]
        public void Initialize_WaitsForFirstConsentSnapshot()
        {
            var sink = CreateSink();

            var task = sink.InitializeAsync(CancellationToken.None);
            _h.Main.Drain();
            Assert.IsFalse(task.IsCompleted);
            CollectionAssert.DoesNotContain(_api.Calls, "InitSdk");

            sink.Apply(NonGdpr());
            var result = _h.Run(task);

            Assert.IsTrue(result.IsSuccess);
            Assert.IsTrue(sink.IsReady);
            Assert.AreEqual(1, _api.Calls.Count(c => c == "InitSdk"));
        }

        [Test]
        public void Initialize_CallerCancels_InitResumesOnLaterConsent()
        {
            var sink = CreateSink();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var waiting = sink.InitializeAsync(cts.Token);
            Assert.AreEqual(SdkErrorCategory.Cancelled, _h.Run(waiting).Error!.Category);
            CollectionAssert.DoesNotContain(_api.Calls, "InitSdk");

            sink.Apply(NonGdpr());
            Assert.IsTrue(_h.Run(sink.InitializeAsync(CancellationToken.None)).IsSuccess);
            Assert.AreEqual(1, _api.Calls.Count(c => c == "InitSdk"));
        }

        [Test]
        public void Initialize_IsIdempotent()
        {
            var sink = CreateReadySink();

            Assert.IsTrue(_h.Run(sink.InitializeAsync(CancellationToken.None)).IsSuccess);

            Assert.AreEqual(1, _api.Calls.Count(c => c == "InitSdk"));
        }

        [Test]
        public void Initialize_BuildsConfigFromSettings()
        {
            var settings = Settings(sandbox: false) with
            {
                LogLevel = AdjustLogLevel.Warn,
                SendInBackground = true,
                DefaultTracker = "trk123",
                AttConsentWaitingIntervalSeconds = 1000,
            };

            CreateReadySink(settings: settings);

            var config = _api.Config!;
            Assert.AreEqual(AppToken, config.AppToken);
            Assert.AreEqual(AdjustEnvironment.Production, config.Environment);
            Assert.AreEqual(AdjustLogLevel.Warn, config.LogLevel);
            Assert.AreEqual(true, config.IsSendingInBackgroundEnabled);
            Assert.AreEqual(false, config.IsDeferredDeeplinkOpeningEnabled);
            Assert.AreEqual("trk123", config.DefaultTracker);
            Assert.AreEqual(AdjustConfigRules.MaxAttWaitingSeconds, config.AttConsentWaitingInterval);
            Assert.IsNull(config.IsCoppaComplianceEnabled);
            Assert.IsNotNull(config.AttributionChangedDelegate);
            Assert.IsNotNull(config.DeferredDeeplinkDelegate);
        }

        [Test]
        public void Initialize_Sandbox()
        {
            CreateReadySink(settings: Settings(sandbox: true));

            Assert.AreEqual(AdjustEnvironment.Sandbox, _api.Config!.Environment);
        }

        [Test]
        public void Initialize_UnderAge_EnablesCoppaAndKidsCompliance()
        {
            CreateReadySink(NonGdpr() with { IsUnderAge = true });

            Assert.AreEqual(true, _api.Config!.IsCoppaComplianceEnabled);
            Assert.AreEqual(true, _api.Config!.IsPlayStoreKidsComplianceEnabled);
            Assert.AreEqual(false, _api.Sharing.Single().IsEnabled);
        }

        [Test]
        public void Initialize_VendorThrows_ReturnsProviderError()
        {
            var sink = CreateSink();
            sink.Apply(NonGdpr());
            _api.Throw = true;

            var result = _h.Run(sink.InitializeAsync(CancellationToken.None));

            Assert.AreEqual(SdkErrorCategory.Provider, result.Error!.Category);
            Assert.IsFalse(sink.IsReady);
        }

        [Test]
        public void Consent_SentBeforeInitSdk_WithGoogleDma()
        {
            CreateReadySink(Gdpr(ConsentState.Granted, ConsentState.Granted, ConsentState.Denied));

            CollectionAssert.AreEqual(
                new[] { "TrackThirdPartySharing", "TrackMeasurementConsent", "InitSdk", "GetAttribution" }, _api.Calls);
            var sharing = _api.Sharing.Single();
            Assert.AreEqual(true, sharing.IsEnabled);
            CollectionAssert.AreEqual(
                new[] { "google_dma", "eea", "1", "google_dma", "ad_personalization", "0", "google_dma", "ad_user_data", "1" },
                Pairs(sharing.GranularOptions));
            CollectionAssert.AreEqual(new[] { true }, _api.MeasurementConsents);
        }

        [Test]
        public void Consent_NonGdpr_SendsEeaZeroWithoutMeasurementConsent()
        {
            CreateReadySink(NonGdpr());

            CollectionAssert.AreEqual(new[] { "google_dma", "eea", "0" }, Pairs(_api.Sharing.Single().GranularOptions));
            Assert.IsEmpty(_api.MeasurementConsents);
        }

        [Test]
        public void Consent_GdprAdUserDataDenied_DisablesSharing()
        {
            CreateReadySink(Gdpr(ConsentState.Denied, ConsentState.Denied, ConsentState.Denied));

            Assert.AreEqual(false, _api.Sharing.Single().IsEnabled);
            CollectionAssert.AreEqual(new[] { false }, _api.MeasurementConsents);
        }

        [Test]
        public void Consent_UsDoNotSell_DisablesSharing()
        {
            CreateReadySink(NonGdpr() with { Jurisdiction = Jurisdiction.UsState, UsDoNotSell = true });

            Assert.AreEqual(false, _api.Sharing.Single().IsEnabled);
        }

        [Test]
        public void Consent_UnknownRegion_KeepsWaiting_UntilConsentIsKnown()
        {
            var sink = CreateSink(null, true);
            sink.Apply(ConsentSnapshot.Unknown);
            var init = sink.InitializeAsync(CancellationToken.None);
            _h.Main.Drain();
            Assert.IsFalse(init.IsCompleted, "an EEA user may not have answered yet: Adjust must not start");
            CollectionAssert.IsEmpty(_api.Calls);

            sink.Apply(NonGdpr());
            Assert.IsTrue(_h.Run(init).IsSuccess);
            CollectionAssert.Contains(_api.Calls, "InitSdk");
        }

        [Test]
        public void Consent_Unchanged_NotResent()
        {
            var sink = CreateReadySink(NonGdpr());

            sink.Apply(NonGdpr() with { UpdatedUtc = DateTime.UtcNow });

            Assert.AreEqual(1, _api.Sharing.Count);
        }

        [Test]
        public void Consent_Changed_SentAgainAfterInit()
        {
            var sink = CreateReadySink(Gdpr(ConsentState.Granted, ConsentState.Granted, ConsentState.Granted));

            sink.Apply(Gdpr(ConsentState.Granted, ConsentState.Denied, ConsentState.Denied));

            Assert.AreEqual(2, _api.Sharing.Count);
            Assert.AreEqual(false, _api.Sharing[1].IsEnabled);
        }

        [Test]
        public void Consent_PersistedAcrossLaunches()
        {
            CreateReadySink(NonGdpr()).Dispose();
            _api = new FakeAdjustApi();

            CreateReadySink(NonGdpr());

            Assert.IsEmpty(_api.Sharing);
        }

        [Test]
        public void Consent_HandoffFails_NotPersistedAndRetriedOnNextApply()
        {
            var sink = CreateReadySink(NonGdpr());
            var saved = _h.Store.Values[AdjustSink.ConsentStorageKey];
            var changed = Gdpr(ConsentState.Granted, ConsentState.Granted, ConsentState.Granted);

            _api.Throw = true;
            sink.Apply(changed);
            Assert.AreEqual(saved, _h.Store.Values[AdjustSink.ConsentStorageKey]);

            _api.Throw = false;
            sink.Apply(changed);
            Assert.AreEqual(2, _api.Sharing.Count);
            Assert.AreNotEqual(saved, _h.Store.Values[AdjustSink.ConsentStorageKey]);
        }

        [Test]
        public void Send_EventWithToken_TracksWithCallbackParameters()
        {
            var sink = CreateReadySink();

            sink.Send(new TrackingEvent("level_complete", new[]
            {
                TrackingParam.Of("level", 12),
                TrackingParam.Of("mode", "hard"),
                TrackingParam.Of("time", 1.5),
                TrackingParam.Of("bad", double.NaN),
            }));

            var e = _api.Events.Single();
            Assert.AreEqual("lvl001", e.EventToken);
            CollectionAssert.AreEqual(new[] { "level", "12", "mode", "hard", "time", "1.5" }, Pairs(e.CallbackParameters));
        }

        [Test]
        public void Send_EventWithoutToken_Skipped()
        {
            var sink = CreateReadySink();

            sink.Send(new TrackingEvent("level_start"));

            Assert.IsEmpty(_api.Events);
        }

        [Test]
        public void Send_BeforeReady_Dropped()
        {
            var sink = CreateSink();

            sink.Send(new TrackingEvent("level_complete"));
            sink.SendAdRevenue(AdRevenue(AdProviderIds.Max));

            Assert.IsEmpty(_api.Calls);
            Assert.AreEqual(2, _h.Log.Count(SdkLogLevel.Warning));
        }

        [Test]
        public void Send_VendorThrows_DoesNotPropagate()
        {
            var sink = CreateReadySink();
            _api.Throw = true;

            Assert.DoesNotThrow(() => sink.Send(new TrackingEvent("level_complete")));
            Assert.DoesNotThrow(() => sink.SendAdRevenue(AdRevenue(AdProviderIds.Max)));
            Assert.AreEqual(2, _h.Log.Count(SdkLogLevel.Error));
        }

        static AdRevenueEvent AdRevenue(string mediation, double value = 0.0125, string currency = "USD") =>
            new AdRevenueEvent(Guid.NewGuid(), Guid.NewGuid(), AdFormat.Interstitial, "level_end", mediation,
                "AppLovin", "unit-1", value, currency, RevenuePrecision.Precise);

        [TestCase(AdProviderIds.Max, "applovin_max_sdk")]
        [TestCase(AdProviderIds.AdMob, "admob_sdk")]
        [TestCase("other", "publisher_sdk")]
        public void SendAdRevenue_MapsSourceAndFields(string mediation, string source)
        {
            var sink = CreateReadySink();

            sink.SendAdRevenue(AdRevenue(mediation));

            var revenue = _api.AdRevenues.Single();
            Assert.AreEqual(source, revenue.Source);
            Assert.AreEqual(0.0125, revenue.Revenue);
            Assert.AreEqual("USD", revenue.Currency);
            Assert.AreEqual("AppLovin", revenue.AdRevenueNetwork);
            Assert.AreEqual("unit-1", revenue.AdRevenueUnit);
            Assert.AreEqual("level_end", revenue.AdRevenuePlacement);
        }

        [Test]
        public void SendAdRevenue_InvalidValue_Dropped()
        {
            var sink = CreateReadySink();

            sink.SendAdRevenue(AdRevenue(AdProviderIds.Max, double.NaN));
            sink.SendAdRevenue(AdRevenue(AdProviderIds.Max, -1));

            Assert.IsEmpty(_api.AdRevenues);
        }

        [Test]
        public void SendAdRevenue_EmptyCurrency_DefaultsToUsd()
        {
            var sink = CreateReadySink();

            sink.SendAdRevenue(AdRevenue(AdProviderIds.AdMob, currency: string.Empty));

            Assert.AreEqual("USD", _api.AdRevenues.Single().Currency);
        }

        [Test]
        public void SendPurchase_WithToken_TracksRevenueAndDeduplicationId()
        {
            var sink = CreateReadySink();

            sink.SendPurchase(new PurchaseRevenueEvent(Guid.NewGuid(), "tx-1", "gems_100", 4.99, "EUR", 2));

            var e = _api.Events.Single();
            Assert.AreEqual("pur001", e.EventToken);
            Assert.AreEqual(4.99, e.Revenue);
            Assert.AreEqual("EUR", e.Currency);
            Assert.AreEqual("tx-1", e.DeduplicationId);
            Assert.AreEqual("tx-1", e.TransactionId);
            Assert.AreEqual("gems_100", e.ProductId);
            CollectionAssert.AreEqual(new[] { "quantity", "2" }, Pairs(e.CallbackParameters));
        }

        [Test]
        public void SendPurchase_WithoutToken_Skipped()
        {
            var sink = CreateReadySink(settings: Settings() with { PurchaseEventName = "iap" });

            sink.SendPurchase(new PurchaseRevenueEvent(Guid.NewGuid(), "tx-1", "gems_100", 4.99, "USD"));

            Assert.IsEmpty(_api.Events);
        }

        [Test]
        public void SendPurchase_WithoutTransactionId_Dropped()
        {
            var sink = CreateReadySink();

            sink.SendPurchase(new PurchaseRevenueEvent(Guid.NewGuid(), string.Empty, "gems_100", 4.99, "USD"));

            Assert.IsEmpty(_api.Events);
        }

        static AdjustAttribution Attribution(string network, string campaign = "c1") =>
            new AdjustAttribution { TrackerToken = "trk", TrackerName = network + "::" + campaign, Network = network, Campaign = campaign };

        [Test]
        public void Attribution_FromCallback_DeliveredOnMainThread()
        {
            var sink = CreateReadySink();
            var listener = new RecordingListener();
            sink.SetListener(listener);

            _api.Config!.AttributionChangedDelegate(Attribution("Facebook Installs"));
            Assert.IsEmpty(listener.Attributions);
            _h.Main.Drain();

            var data = listener.Attributions.Single();
            Assert.AreEqual("adjust", data.Provider);
            Assert.AreEqual("Facebook Installs", data.Network);
            Assert.AreEqual("c1", data.Campaign);
            Assert.IsNull(data.Adgroup);
        }

        [Test]
        public void Attribution_FromGetAttribution_BufferedUntilListener()
        {
            var sink = CreateReadySink();

            _api.AttributionRequests.Single()(Attribution("Organic"));
            _h.Main.Drain();
            var listener = new RecordingListener();
            sink.SetListener(listener);

            Assert.AreEqual("Organic", listener.Attributions.Single().Network);
        }

        [Test]
        public void Attribution_SameOrEmpty_NotRedelivered()
        {
            var sink = CreateReadySink();
            var listener = new RecordingListener();
            sink.SetListener(listener);

            _api.AttributionRequests.Single()(Attribution("Organic"));
            _api.Config!.AttributionChangedDelegate(Attribution("Organic"));
            _api.Config!.AttributionChangedDelegate(null!);
            _api.Config!.AttributionChangedDelegate(new AdjustAttribution());
            _h.Main.Drain();

            Assert.AreEqual(1, listener.Attributions.Count);
        }

        [Test]
        public void DeepLink_LaunchUrl_BufferedAndForwardedOnAndroid()
        {
            _api.ForwardsDeepLinks = true;
            _api.LaunchUrl = "game://open?x=1";
            var sink = CreateReadySink();

            var listener = new RecordingListener();
            sink.SetListener(listener);

            CollectionAssert.AreEqual(new[] { "game://open?x=1" }, _api.ProcessedDeepLinks);
            Assert.AreEqual(new DeepLink("game://open?x=1", false), listener.DeepLinks.Single());
        }

        [Test]
        public void DeepLink_Activated_NotForwardedOnIos()
        {
            _api.ForwardsDeepLinks = false;
            var sink = CreateReadySink(android: false);
            var listener = new RecordingListener();
            sink.SetListener(listener);

            _api.DeepLinkHandler!("game://shop");

            Assert.IsEmpty(_api.ProcessedDeepLinks);
            Assert.AreEqual(new DeepLink("game://shop", false), listener.DeepLinks.Single());
        }

        [Test]
        public void DeepLink_Deferred_DeliveredButNotForwarded()
        {
            _api.ForwardsDeepLinks = true;
            var sink = CreateReadySink();
            var listener = new RecordingListener();
            sink.SetListener(listener);

            _api.Config!.DeferredDeeplinkDelegate("game://reward");
            _h.Main.Drain();

            Assert.IsEmpty(_api.ProcessedDeepLinks);
            Assert.AreEqual(new DeepLink("game://reward", true), listener.DeepLinks.Single());
        }

        [Test]
        public void DeepLink_PendingQueueIsBounded()
        {
            var sink = CreateReadySink();
            for (int i = 0; i < 20; i++) _api.DeepLinkHandler!("game://" + i);

            var listener = new RecordingListener();
            sink.SetListener(listener);

            Assert.AreEqual(8, listener.DeepLinks.Count);
            Assert.AreEqual("game://19", listener.DeepLinks.Last().Url);
        }

        [Test]
        public void Listener_Throws_DoesNotPropagate()
        {
            var sink = CreateReadySink();
            sink.SetListener(new ThrowingListener());

            Assert.DoesNotThrow(() => _api.DeepLinkHandler!("game://x"));
            Assert.AreEqual(1, _h.Log.Count(SdkLogLevel.Error));
        }

        [Test]
        public void Dispose_IgnoresLateCallbacksAndUnsubscribes()
        {
            var sink = CreateReadySink();
            var listener = new RecordingListener();
            sink.SetListener(listener);
            var callback = _api.Config!.AttributionChangedDelegate;

            sink.Dispose();
            callback(Attribution("Google Ads"));
            _h.Main.Drain();

            Assert.IsEmpty(listener.Attributions);
            Assert.IsNull(_api.DeepLinkHandler);
            Assert.IsFalse(sink.IsReady);
            sink.Send(new TrackingEvent("level_complete"));
            Assert.IsEmpty(_api.Events);
        }

        [Test]
        public void Dispose_WhileWaitingForConsent_CompletesInitAsDisposed()
        {
            var sink = CreateSink();
            var task = sink.InitializeAsync(CancellationToken.None);

            sink.Dispose();

            Assert.AreEqual(SdkErrorCategory.Cancelled, _h.Run(task).Error!.Category);
            Assert.IsEmpty(_api.Calls);
        }

        sealed class ThrowingListener : IAttributionListener
        {
            public void OnAttribution(AttributionData data) => throw new InvalidOperationException();
            public void OnDeepLink(DeepLink link) => throw new InvalidOperationException();
        }
    }

    public sealed class AdjustConfigRulesTests
    {
        static IReadOnlyList<string> Validate(string token, params (string, string)[] events) =>
            AdjustConfigRules.Validate(token, events, isIos: false);

        [Test]
        public void ValidConfig_NoIssues()
        {
            CollectionAssert.IsEmpty(Validate("abcdefghijkl", ("purchase", "abc123")));
        }

        [Test]
        public void MissingAppToken_Reported()
        {
            StringAssert.Contains("No Adjust app token for Android", Validate("  ").Single());
        }

        [Test]
        public void MalformedTokens_Reported()
        {
            var issues = Validate("short", ("purchase", "toolong1"), ("level", ""));

            Assert.AreEqual(3, issues.Count);
        }

        [Test]
        public void DuplicateEvent_Reported()
        {
            var issues = Validate("abcdefghijkl", ("purchase", "abc123"), ("purchase", "def456"));

            StringAssert.Contains("more than once", issues.Single());
        }
    }
}
