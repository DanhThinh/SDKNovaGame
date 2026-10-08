#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Firebase;
using Firebase.Analytics;
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.Testing;
using NovaGames.Mobile.Tracking;
using NUnit.Framework;

namespace NovaGames.Mobile.Firebase.Tests
{
    public sealed class FirebaseAnalyticsSinkTests
    {
        TestHarness _h = null!;
        FakeFirebaseAppApi _appApi = null!;
        FakeAnalyticsApi _api = null!;

        [SetUp]
        public void SetUp()
        {
            _h = new TestHarness();
            _appApi = new FakeFirebaseAppApi();
            _api = new FakeAnalyticsApi();
        }

        [TearDown] public void TearDown() => _h.Dispose();

        FirebaseAnalyticsSink CreateSink(bool ios = false, AnalyticsOptions? settings = null) =>
            new FirebaseAnalyticsSink(
                new FirebaseAppInitializer(_appApi, _h.Main, _h.Scheduler, _h.Log),
                _api, _h.Log, settings ?? AnalyticsOptions.Default, ios);

        FirebaseAnalyticsSink CreateReadySink(bool ios = false)
        {
            var sink = CreateSink(ios);
            var init = _h.Run(sink.InitializeAsync(CancellationToken.None));
            Assert.IsTrue(init.IsSuccess, init.ToString());
            return sink;
        }

        static ConsentSnapshot Consent(ConsentState analytics, ConsentState ads) =>
            ConsentSnapshot.Unknown with { AnalyticsStorage = analytics, AdStorage = ads };

        [Test]
        public void Initialize_DependenciesUnavailable_Fails()
        {
            _appApi.Result = Task.FromResult(DependencyStatus.UnavailableOther);
            var sink = CreateSink();

            var result = _h.Run(sink.InitializeAsync(CancellationToken.None));

            Assert.AreEqual(SdkErrorCategory.Unavailable, result.Error!.Category);
            Assert.IsFalse(sink.IsReady);
        }

        [Test]
        public void Initialize_DependencyCheckHangs_TimesOut()
        {
            _appApi.Result = new TaskCompletionSource<DependencyStatus>().Task;
            var sink = CreateSink();

            var task = sink.InitializeAsync(CancellationToken.None);
            _h.Main.Drain();
            _h.Scheduler.Advance(AnalyticsOptions.Default.InitTimeout);
            var result = _h.Run(task);

            Assert.AreEqual(SdkErrorCategory.Timeout, result.Error!.Category);
        }

        [Test]
        public void Initialize_TimesOut_ThenBecomesReadyWhenDependencyCheckFinishes()
        {
            var check = new TaskCompletionSource<DependencyStatus>();
            _appApi.Result = check.Task;
            var sink = CreateSink();
            sink.SetUserProperty("segment", "paid");

            var task = sink.InitializeAsync(CancellationToken.None);
            _h.Main.Drain();
            _h.Scheduler.Advance(AnalyticsOptions.Default.InitTimeout);
            Assert.AreEqual(SdkErrorCategory.Timeout, _h.Run(task).Error!.Category);
            Assert.IsFalse(sink.IsReady);

            check.SetResult(DependencyStatus.Available);
            _h.Main.Drain();

            Assert.IsTrue(sink.IsReady, "shared dependency check finished after the caller's timeout");
            Assert.AreEqual(1, _api.UserProperties.Count, "pending user property flushed");
            sink.Send(new TrackingEvent("level_start"));
            Assert.AreEqual(1, _api.Events.Count);
        }

        [Test]
        public void Initialize_IsIdempotent_AndAppliesSettings()
        {
            var sink = CreateSink(settings: AnalyticsOptions.Default with { SessionTimeout = TimeSpan.FromMinutes(10) });

            var first = sink.InitializeAsync(CancellationToken.None);
            var second = sink.InitializeAsync(CancellationToken.None);
            _h.Run(first);
            _h.Run(second);
            _h.Run(sink.InitializeAsync(CancellationToken.None));

            Assert.AreEqual(1, _appApi.Calls);
            Assert.AreEqual(TimeSpan.FromMinutes(10), _api.SessionTimeout);
            Assert.IsNull(_api.CollectionEnabled, "collection flag must stay untouched unless configured");
        }

        [Test]
        public void ConsentAppliedBeforeInit_IsFlushedAfterInit_LatestWins()
        {
            var sink = CreateSink();
            sink.Apply(Consent(ConsentState.Denied, ConsentState.Denied));
            sink.Apply(Consent(ConsentState.Granted, ConsentState.Denied));
            Assert.AreEqual(0, _api.Consents.Count, "no Firebase call before dependencies are ready");

            _h.Run(sink.InitializeAsync(CancellationToken.None));

            Assert.AreEqual(1, _api.Consents.Count);
            var consent = _api.Consents[0];
            Assert.AreEqual(ConsentStatus.Granted, consent[ConsentType.AnalyticsStorage]);
            Assert.AreEqual(ConsentStatus.Denied, consent[ConsentType.AdStorage]);
            Assert.IsFalse(consent.ContainsKey(ConsentType.AdUserData), "Unknown keeps manifest default");
        }

        [Test]
        public void NotRequiredConsent_MapsToGranted()
        {
            var sink = CreateReadySink();
            sink.Apply(Consent(ConsentState.NotRequired, ConsentState.NotRequired));

            Assert.AreEqual(ConsentStatus.Granted, _api.Consents.Single()[ConsentType.AdStorage]);
        }

        [Test]
        public void UnderAge_AdPersonalizationAndUserData_AlwaysDenied()
        {
            var sink = CreateReadySink();
            sink.Apply(ConsentSnapshot.AllGranted with { IsUnderAge = true });

            var consent = _api.Consents.Single();
            Assert.AreEqual(ConsentStatus.Denied, consent[ConsentType.AdPersonalization]);
            Assert.AreEqual(ConsentStatus.Denied, consent[ConsentType.AdUserData]);
            Assert.AreEqual(ConsentStatus.Granted, consent[ConsentType.AnalyticsStorage]);
        }

        [Test]
        public void Send_BeforeReady_IsDropped()
        {
            var sink = CreateSink();
            sink.Send(new TrackingEvent("level_start"));

            _h.Run(sink.InitializeAsync(CancellationToken.None));

            Assert.AreEqual(0, _api.Events.Count);
        }

        [Test]
        public void Send_SanitizesParams()
        {
            var sink = CreateReadySink();
            var parameters = Enumerable.Range(0, 30).Select(i => TrackingParam.Of("p" + i, i)).ToList();
            parameters.Insert(0, TrackingParam.Of("long_text", new string('x', 150)));
            parameters.Insert(1, TrackingParam.Of("bad-name", 1));
            parameters.Insert(2, TrackingParam.Of("nan", double.NaN));

            sink.Send(new TrackingEvent("level_end", parameters));

            var (name, sent) = _api.Events.Single();
            Assert.AreEqual("level_end", name);
            Assert.AreEqual(FirebaseAnalyticsRules.MaxParamsPerEvent, sent.Length);
            Assert.AreEqual(100, sent[0].StringValue!.Length);
            Assert.IsFalse(sent.Any(p => p.Name == "bad-name" || p.Name == "nan"));
        }

        [TestCase("1level")]
        [TestCase("firebase_custom")]
        [TestCase("session_start")]
        [TestCase("this_event_name_is_definitely_longer_than_forty")]
        public void Send_InvalidEventName_IsDropped(string eventName)
        {
            var sink = CreateReadySink();
            sink.Send(new TrackingEvent(eventName));
            Assert.AreEqual(0, _api.Events.Count);
        }

        [Test]
        public void Send_VendorThrows_DoesNotEscape()
        {
            var sink = CreateReadySink();
            _api.ThrowOnLog = true;

            Assert.DoesNotThrow(() => sink.Send(new TrackingEvent("level_start")));
        }

        [Test]
        public void SendAdRevenue_LogsAdImpressionWithStandardParams()
        {
            var sink = CreateReadySink();
            sink.SendAdRevenue(new AdRevenueEvent(Guid.NewGuid(), Guid.NewGuid(), AdFormat.Rewarded, "revive",
                "AppLovin", "AdMob", "unit-1", 0.0123, "USD", RevenuePrecision.Precise));

            var (name, sent) = _api.Events.Single();
            Assert.AreEqual("ad_impression", name);
            Assert.AreEqual("AppLovin", sent.Single(p => p.Name == "ad_platform").StringValue);
            Assert.AreEqual("AdMob", sent.Single(p => p.Name == "ad_source").StringValue);
            Assert.AreEqual("rewarded", sent.Single(p => p.Name == "ad_format").StringValue);
            Assert.AreEqual("unit-1", sent.Single(p => p.Name == "ad_unit_name").StringValue);
            Assert.AreEqual("USD", sent.Single(p => p.Name == "currency").StringValue);
            Assert.AreEqual(0.0123, sent.Single(p => p.Name == "value").DoubleValue);
        }

        [Test]
        public void SendPurchase_OnlyLogsAppleTransactionOnIos()
        {
            var purchase = new PurchaseRevenueEvent(Guid.NewGuid(), "tx-1", "remove_ads", 2.99, "USD");

            var android = CreateReadySink(ios: false);
            android.SendPurchase(purchase);
            Assert.AreEqual(0, _api.AppleTransactions.Count);
            Assert.IsFalse(android.Capabilities.HasFlag(SinkCapabilities.Purchase));

            var ios = CreateReadySink(ios: true);
            ios.SendPurchase(purchase);
            CollectionAssert.AreEqual(new[] { "tx-1" }, _api.AppleTransactions);
            Assert.IsTrue(ios.Capabilities.HasFlag(SinkCapabilities.Purchase));
        }

        [Test]
        public void UserProperties_BeforeReady_AreFlushedLatestWins()
        {
            var sink = CreateSink();
            sink.SetUserProperty("level", "1");
            sink.SetUserProperty("level", "2");
            sink.SetUserProperty("firebase_x", "nope");
            sink.SetUserId("user-1");

            _h.Run(sink.InitializeAsync(CancellationToken.None));

            CollectionAssert.AreEqual(new[] { ("level", (string?)"2") }, _api.UserProperties);
            CollectionAssert.AreEqual(new[] { "user-1" }, _api.UserIds);
        }

        [Test]
        public void Dispose_StopsHandoff()
        {
            var sink = CreateReadySink();
            sink.Dispose();

            sink.Send(new TrackingEvent("level_start"));
            sink.Apply(Consent(ConsentState.Granted, ConsentState.Granted));

            Assert.AreEqual(0, _api.Events.Count);
            Assert.AreEqual(0, _api.Consents.Count);
            Assert.IsFalse(sink.IsReady);
        }
    }
}
