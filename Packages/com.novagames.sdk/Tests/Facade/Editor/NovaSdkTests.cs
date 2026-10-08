#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Diagnostics;
using NovaGames.Mobile.Iap;
using NovaGames.Mobile.Notifications;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.RemoteConfig;
using NovaGames.Mobile.Testing;
using NovaGames.Mobile.Tracking;
using NUnit.Framework;

namespace NovaGames.Mobile.Tests
{
    // NovaSdk dựng toàn bộ module từ registry (fake) như trong game thật.
    public sealed class NovaSdkTests
    {
        static readonly AdUnit InterUnit = new AdUnit("inter", AdFormat.Interstitial, "inter-id", AdsProvider.Max);
        static readonly AdUnit RewardedUnit = new AdUnit("rewarded", AdFormat.Rewarded, "rewarded-id", AdsProvider.Max);
        static readonly IapProductDefinition RemoveAdsProduct = new IapProductDefinition("remove_ads", "store.remove_ads", IapProductType.NonConsumable)
        {
            Entitlements = new[] { NovaGames.Mobile.Entitlements.EntitlementId.RemoveAds },
        };
        static readonly IapProductDefinition GemsProduct = new IapProductDefinition("gems", "store.gems", IapProductType.Consumable);

        TestHarness _h = null!;
        FakeSink _firebase = null!;
        FakeSink _adjust = null!;
        FakeAdsAdapter _ads = null!;
        FakeConsentPlatform _ump = null!;
        FakeStoreAdapter _iapStore = null!;
        FakeNotificationPlatform _notifications = null!;
        FakeCrashReporter _crash = null!;
        FakeAttPlatform _att = null!;

        [SetUp]
        public void SetUp()
        {
            NovaSdk.Shutdown();
            _h = new TestHarness();
            _firebase = new FakeSink(TrackingSinkIds.Firebase,
                SinkCapabilities.Events | SinkCapabilities.AdRevenue | SinkCapabilities.UserProperties | SinkCapabilities.ConsentMode);
            _adjust = new FakeSink(TrackingSinkIds.Adjust, SinkCapabilities.Events | SinkCapabilities.AdRevenue | SinkCapabilities.Purchase);
            _ads = new FakeAdsAdapter();
            _ump = new FakeConsentPlatform();
            _iapStore = new FakeStoreAdapter();
            _notifications = new FakeNotificationPlatform();
            _crash = new FakeCrashReporter();
            _att = new FakeAttPlatform();
        }

        [TearDown]
        public void TearDown()
        {
            NovaSdk.Shutdown();
            _h.Dispose();
        }

        static NovaSdkSetup Setup(bool withAds = true, bool withAdjust = false,
                                  NovaConsentSource consent = NovaConsentSource.Game, bool withIap = false, bool testStore = false,
                                  bool withNotifications = false, bool withCrash = false,
                                  bool ios = false, bool attOnStartup = false, bool attAllowed = true)
        {
            var setup = new NovaSdkSetup
            {
                IsDevelopment = true, ConsentSource = consent, IsIos = ios, RequestAttOnStartup = attOnStartup, AttAllowed = attAllowed,
            };
            if (withAds) setup.Ads = new AdsOptions(new[] { InterUnit, RewardedUnit }, Array.Empty<AdPlacementBinding>());
            if (withAdjust) setup.AdjustSettings = new FakeSinkSettings();
            if (withIap) setup.Iap = new IapOptions(new[] { RemoveAdsProduct, GemsProduct }) { UseTestStore = testStore };
            if (withNotifications) setup.Notifications = NotificationOptions.Default with { AskPermissionOnStartup = false };
            if (withCrash) setup.CrashReporting = new CrashReportingOptions { UncaughtExceptionsAsFatal = true };
            return setup;
        }

        AdapterRegistrySnapshot Registry(bool withUmp = true, bool withAtt = false) => new AdapterRegistrySnapshot(
            new Dictionary<string, Func<ModuleContext, IRemoteConfigSource>> { [RemoteConfigSourceIds.Firebase] = _ => new FakeSource() },
            new Dictionary<string, Func<ModuleContext, ITrackingSink>>
            {
                [TrackingSinkIds.Firebase] = _ => _firebase,
                [TrackingSinkIds.Adjust] = _ => _adjust,
            },
            new Dictionary<string, Func<ModuleContext, IAdsAdapter>> { [AdProviderIds.Max] = _ => _ads },
            withUmp
                ? new Dictionary<string, Func<ModuleContext, IConsentPlatform>> { [ConsentPlatformIds.GoogleUmp] = _ => _ump }
                : null,
            new Dictionary<string, Func<ModuleContext, IStoreAdapter>> { [StoreAdapterIds.UnityIap] = _ => _iapStore },
            new Dictionary<string, Func<ModuleContext, INotificationPlatform>> { [NotificationPlatformIds.Unity] = _ => _notifications },
            new Dictionary<string, Func<ModuleContext, ICrashReporter>> { [CrashReporterIds.FirebaseCrashlytics] = _ => _crash },
            withAtt ? new Dictionary<string, Func<ModuleContext, IAttPlatform>> { [AttPlatformIds.Apple] = _ => _att } : null);

        Task Init(NovaSdkSetup setup, bool withUmp = true, bool withAtt = false) =>
            NovaSdk.InitializeAsync(setup, _h.Context, Registry(withUmp, withAtt), null, null);

        void CompleteFirebaseInit()
        {
            _firebase.Ready = true;
            _firebase.Init.TrySetResult(SdkResult.Ok);
            _h.Main.Drain();
        }

        [Test]
        public void EventsLoggedBeforeInit_AreDeliveredOnceTheSinkIsReady()
        {
            NovaAnalytics.LogEvent("boot");
            var init = Init(Setup(withAds: false));
            NovaAnalytics.LogEvent("level_start", ("level", 1));
            _h.Main.Drain();
            Assert.IsEmpty(_firebase.Events, "sink not ready yet");
            Assert.AreEqual(NovaSdkState.Initializing, NovaSdk.State);

            CompleteFirebaseInit();

            CollectionAssert.AreEqual(new[] { "boot", "level_start" }, _firebase.Events.Select(e => e.Name));
            Assert.IsTrue(_h.Main.RunUntilCompleted(NovaSdk.WhenReady));
            Assert.IsTrue(init.IsCompleted);
            Assert.AreEqual(NovaSdkState.Ready, NovaSdk.State);
        }

        [Test]
        public void PendingEventsPerSink_AreBounded_OldestDropped()
        {
            Init(Setup(withAds: false));
            for (int i = 0; i < NovaAnalytics.MaxPendingPerSink + 20; i++) NovaAnalytics.LogEvent("e" + i);

            CompleteFirebaseInit();

            Assert.AreEqual(NovaAnalytics.MaxPendingPerSink, _firebase.Events.Count);
            Assert.AreEqual("e20", _firebase.Events[0].Name);
        }

        [Test]
        public void EventParams_AreConvertedToTypedParams()
        {
            Init(Setup(withAds: false));
            CompleteFirebaseInit();

            NovaAnalytics.LogEvent("level_complete", ("level", 3), ("time", 42.5f), ("hard", true), ("mode", "daily"), ("missing", null));

            var param = _firebase.Events.Single().Params;
            Assert.AreEqual(3, param[0].LongValue);
            Assert.AreEqual(42.5, param[1].DoubleValue, 1e-6);
            Assert.AreEqual(1, param[2].LongValue);
            Assert.AreEqual("daily", param[3].StringValue);
            Assert.AreEqual(string.Empty, param[4].StringValue);
        }

        [Test]
        public void NoConsentFromGame_SinksAreNotGivenConsent_AdsStayBlocked()
        {
            Init(Setup(withAdjust: true));
            CompleteFirebaseInit();

            Assert.IsEmpty(_adjust.Consents, "Adjust starts only after the first real consent");
            Assert.AreEqual(AdsModuleState.Blocked, NovaAds.Service!.State.Value);
            Assert.AreEqual(0, _ads.InitializeCount);
        }

        [Test]
        public void SetConsent_BeforeInit_IsAppliedToSinksAndAds()
        {
            NovaSdk.SetConsent(ConsentSnapshot.AllGranted);
            Init(Setup(withAdjust: true));
            CompleteFirebaseInit();

            Assert.AreSame(ConsentSnapshot.AllGranted, _adjust.Consents.Single());
            Assert.AreEqual(AdsModuleState.Ready, NovaAds.Service!.State.Value);
            Assert.AreEqual(1, _ads.InitializeCount);
        }

        [Test]
        public void SetConsent_AfterInit_ReachesSinksAndUnblocksAds()
        {
            Init(Setup(withAdjust: true));
            CompleteFirebaseInit();

            NovaSdk.SetConsent(ConsentSnapshot.AllGranted);
            _h.Main.Drain();

            Assert.AreEqual(1, _adjust.Consents.Count);
            Assert.AreEqual(AdsModuleState.Ready, NovaAds.Service!.State.Value);
        }

        [Test]
        public void AssumeGrantedForTesting_GrantsEverything()
        {
            Init(Setup(consent: NovaConsentSource.AssumeGrantedForTesting));
            CompleteFirebaseInit();

            Assert.AreSame(ConsentSnapshot.AllGranted, NovaSdk.Consent);
            Assert.AreEqual(AdsModuleState.Ready, NovaAds.Service!.State.Value);
        }

        [Test]
        public void GoogleUmp_GatheredConsent_IsAppliedToSinksAndAds()
        {
            Init(Setup(withAdjust: true, consent: NovaConsentSource.GoogleUmp));
            CompleteFirebaseInit();
            Assert.AreEqual(1, _ump.GatherCount);
            Assert.AreEqual(AdsModuleState.Blocked, NovaAds.Service!.State.Value, "nothing stored from a previous run");

            var changed = new List<ConsentSnapshot>();
            NovaPrivacy.ConsentChanged += changed.Add;
            _ump.Gather.TrySetResult(SdkResult<ConsentSnapshot>.Ok(ConsentSnapshot.AllGranted));
            _h.Main.Drain();

            Assert.AreSame(ConsentSnapshot.AllGranted, NovaPrivacy.Consent);
            Assert.AreSame(ConsentSnapshot.AllGranted, changed.Single());
            Assert.AreSame(ConsentSnapshot.AllGranted, _adjust.Consents.Single());
            Assert.AreEqual(AdsModuleState.Ready, NovaAds.Service!.State.Value);
        }

        [Test]
        public void GoogleUmp_GatherThrows_RetriesAndEventuallyAppliesConsent()
        {
            _ump.GatherException = new InvalidOperationException("vendor handoff failed");
            Init(Setup(withAds: false, withAdjust: true, consent: NovaConsentSource.GoogleUmp));
            CompleteFirebaseInit();

            Assert.AreEqual(1, _ump.GatherCount);
            Assert.AreSame(ConsentSnapshot.Unknown, NovaSdk.Consent);

            _h.Scheduler.Advance(TimeSpan.FromSeconds(30));
            _h.Main.Drain();
            Assert.AreEqual(2, _ump.GatherCount);

            _ump.Gather.TrySetResult(SdkResult<ConsentSnapshot>.Ok(ConsentSnapshot.AllGranted));
            _h.Main.Drain();

            Assert.AreSame(ConsentSnapshot.AllGranted, NovaSdk.Consent);
            Assert.AreSame(ConsentSnapshot.AllGranted, _adjust.Consents.Single());
        }

        [Test]
        public void GoogleUmp_StoredConsentThatAllowsAds_StartsAdsBeforeGatherCompletes()
        {
            _ump.Stored = ConsentSnapshot.AllGranted;
            Init(Setup(consent: NovaConsentSource.GoogleUmp));
            CompleteFirebaseInit();

            Assert.IsFalse(_ump.Gather.Task.IsCompleted);
            Assert.AreEqual(AdsModuleState.Ready, NovaAds.Service!.State.Value);
        }

        [Test]
        public void GoogleUmp_ModuleNotInstalled_AdsStayBlocked()
        {
            Init(Setup(consent: NovaConsentSource.GoogleUmp), withUmp: false);
            CompleteFirebaseInit();

            Assert.AreEqual(AdsModuleState.Blocked, NovaAds.Service!.State.Value);
            Assert.IsFalse(NovaPrivacy.IsPrivacyOptionsRequired);
        }

        [Test]
        public void ShowPrivacyOptions_AppliesNewConsent_ThenCallsOnClosed()
        {
            Init(Setup(consent: NovaConsentSource.GoogleUmp));
            CompleteFirebaseInit();
            _ump.Gather.TrySetResult(SdkResult<ConsentSnapshot>.Ok(ConsentSnapshot.AllGranted));
            _h.Main.Drain();
            _ump.PrivacyOptionsRequired = true;
            Assert.IsTrue(NovaPrivacy.IsPrivacyOptionsRequired);

            bool closed = false;
            NovaPrivacy.ShowPrivacyOptions(() => closed = true);
            var revoked = ConsentSnapshot.AllGranted with { Jurisdiction = Jurisdiction.Gdpr, CanRequestAds = false };
            _ump.PrivacyOptions.TrySetResult(SdkResult<ConsentSnapshot>.Ok(revoked));
            _h.Main.Drain();

            Assert.IsTrue(closed);
            Assert.AreSame(revoked, NovaSdk.Consent);
            Assert.AreEqual(AdsModuleState.Blocked, NovaAds.Service!.State.Value);
        }

        [Test]
        public void Shutdown_DisposesConsentPlatform_AndLateResultIsIgnored()
        {
            Init(Setup(consent: NovaConsentSource.GoogleUmp));
            NovaSdk.Shutdown();
            Assert.IsTrue(_ump.Disposed);

            _ump.Gather.TrySetResult(SdkResult<ConsentSnapshot>.Ok(ConsentSnapshot.AllGranted));
            _h.Main.Drain();

            Assert.AreSame(ConsentSnapshot.Unknown, NovaSdk.Consent);
        }

        [Test]
        public void RewardedThroughNovaAds_GrantsReward_AndRevenueReachesEverySink()
        {
            NovaSdk.SetConsent(ConsentSnapshot.AllGranted);
            Init(Setup(withAdjust: true));
            CompleteFirebaseInit();
            _adjust.Ready = true;
            _adjust.Init.TrySetResult(SdkResult.Ok);
            _ads.FireLoaded(RewardedUnit);
            _h.Main.Drain();

            bool rewarded = false;
            NovaAds.ShowRewarded("revive", () => rewarded = true);
            var id = _ads.LastOperationId;
            _ads.FireDisplayed(id);
            _ads.FirePaid(RewardedUnit, id);
            _ads.FireRewarded(id);
            _ads.FireClosed(id);
            _h.Main.Drain();

            Assert.IsTrue(rewarded);
            Assert.AreEqual("revive", _firebase.AdRevenue.Single().PlacementId);
            Assert.AreEqual(1, _adjust.AdRevenue.Count);
        }

        [Test]
        public void PurchaseGoesOnlyToSinksThatTrackPurchases()
        {
            Init(Setup(withAds: false, withAdjust: true));
            CompleteFirebaseInit();
            _adjust.Ready = true;
            _adjust.Init.TrySetResult(SdkResult.Ok);
            _h.Main.Drain();

            NovaAnalytics.LogPurchase("tx-1", "remove_ads", 2.99);

            Assert.AreEqual("tx-1", _adjust.Purchases.Single().TransactionId);
            Assert.IsEmpty(_firebase.Purchases, "Firebase logs Android purchases by itself");
        }

        [Test]
        public void DeepLinkBeforeGameSubscribes_IsReplayedToFirstHandler()
        {
            NovaSdk.SetConsent(ConsentSnapshot.AllGranted);
            Init(Setup(withAds: false, withAdjust: true));
            _adjust.Listener!.OnDeepLink(new DeepLink("game://shop", false));

            var received = new List<DeepLink>();
            NovaAttribution.DeepLinkReceived += received.Add;

            Assert.AreEqual("game://shop", received.Single().Url);
        }

        [Test]
        public void InitializeTwice_ReturnsSameTask_AndShutdownAllowsReinit()
        {
            var first = Init(Setup(withAds: false));
            Assert.AreSame(first, Init(Setup(withAds: false)));

            NovaSdk.Shutdown();
            Assert.AreEqual(NovaSdkState.NotInitialized, NovaSdk.State);
            Assert.IsTrue(_firebase.Disposed);
            Assert.AreNotSame(first, Init(Setup(withAds: false)));
        }

        [Test]
        public void Iap_RemoveAdsPurchase_IsOwnedAndTurnsOffAds()
        {
            Init(Setup(withIap: true));
            CompleteFirebaseInit();
            Assert.IsTrue(NovaIap.IsReady);
            Assert.IsFalse(NovaAds.IsRemoveAds);

            PurchaseResult? result = null;
            NovaIap.Purchase("remove_ads", r => result = r);
            _h.Main.Drain();
            CollectionAssert.AreEqual(new[] { "store.remove_ads" }, _iapStore.Purchases);
            _iapStore.Deliver("tx1", "store.remove_ads");
            _h.Main.Drain();

            Assert.IsNotNull(result);
            Assert.IsTrue(result!.IsSuccess);
            Assert.IsTrue(NovaIap.IsOwned("remove_ads"));
            Assert.IsTrue(NovaIap.HasEntitlement("remove_ads"));
            Assert.IsTrue(NovaAds.IsRemoveAds);
            Assert.AreEqual("$1.99", NovaIap.GetPriceText("remove_ads"));
        }

        [Test]
        public void Iap_ConsumableHandlerSetBeforeInit_GrantsTestStorePurchase()
        {
            var grants = new List<IapGrant>();
            var purchased = new List<IapGrant>();
            NovaIap.SetConsumableHandler(g => { grants.Add(g); return true; });
            NovaIap.OnPurchased += purchased.Add;
            Init(Setup(withIap: true, testStore: true));
            CompleteFirebaseInit();

            PurchaseResult? result = null;
            NovaIap.Purchase("gems", r => result = r);
            _h.Main.Drain();

            Assert.IsTrue(result!.IsSuccess);
            Assert.AreEqual(1, grants.Count);
            Assert.AreEqual(1, purchased.Count);
            CollectionAssert.IsEmpty(_iapStore.Purchases, "test store is used instead of the real adapter");
        }

        [Test]
        public void Iap_NotConfigured_PurchaseReportsNotReady()
        {
            Init(Setup(withAds: false));
            CompleteFirebaseInit();

            PurchaseResult? result = null;
            NovaIap.Purchase("remove_ads", r => result = r);
            _h.Main.Drain();

            Assert.AreEqual(PurchaseStatus.NotReady, result!.Status);
            Assert.IsFalse(NovaIap.IsOwned("remove_ads"));
            Assert.AreEqual(string.Empty, NovaIap.GetPriceText("remove_ads"));
        }

        [Test]
        public void Notifications_CallsBeforeInit_AreAppliedOnceReady()
        {
            NovaNotifications.Schedule("energy", "Energy full", "Come back", TimeSpan.FromHours(4), data: "shop");
            NovaNotifications.ScheduleDaily("daily", "Daily reward", "Gift waiting", hour: 19);
            Assert.AreEqual(NotificationPermission.NotSupported, NovaNotifications.Permission);

            Init(Setup(withAds: false, withNotifications: true));
            CompleteFirebaseInit();

            CollectionAssert.AreEquivalent(new[] { "energy", "daily" }, _notifications.Scheduled.Keys);
            CollectionAssert.AreEquivalent(new[] { "energy", "daily" }, NovaNotifications.ScheduledIds);
            Assert.AreEqual(NotificationPermission.NotDetermined, NovaNotifications.Permission);

            NovaNotifications.Cancel("energy");
            CollectionAssert.AreEqual(new[] { "daily" }, _notifications.Scheduled.Keys);
        }

        [Test]
        public void Notifications_ColdStartOpen_IsReplayedToALateSubscriberAndLogged()
        {
            _notifications.LastOpenedPayload = NotificationService.BuildPayload("daily", "chest");
            Init(Setup(withAds: false, withNotifications: true));
            CompleteFirebaseInit();

            var opened = new List<NotificationOpened>();
            NovaNotifications.Opened += opened.Add;
            NovaNotifications.Opened += opened.Add;

            Assert.AreEqual(1, opened.Count, "replayed once, to the first subscriber");
            Assert.AreEqual(new NotificationOpened("daily", "chest", true), opened[0]);
            Assert.AreEqual(opened[0], NovaNotifications.LastOpened);
            Assert.IsTrue(_firebase.Events.Any(e => e.Name == "notification_open"));
        }

        [Test]
        public void Notifications_RequestPermission_ReportsTheAnswer()
        {
            Init(Setup(withAds: false, withNotifications: true));
            CompleteFirebaseInit();
            bool? allowed = null;

            NovaNotifications.RequestPermission(ok => allowed = ok);
            _notifications.PermissionPrompt!.SetResult(NotificationPermission.Granted);
            _h.Main.Drain();

            Assert.AreEqual(true, allowed);
            Assert.IsTrue(NovaNotifications.IsAllowed);
        }

        [Test]
        public void Crash_CallsBeforeInit_AreSentOnceCrashlyticsIsReady()
        {
            NovaCrash.Log("boot");
            NovaCrash.SetCustomKey("level", 12);
            NovaCrash.SetCustomKey("level", 13);
            NovaCrash.LogException(new InvalidOperationException("early"));
            NovaAnalytics.SetUserId("player_1");
            Assert.IsFalse(NovaCrash.IsReady);

            Init(Setup(withAds: false, withCrash: true));
            CompleteFirebaseInit();

            Assert.IsTrue(NovaCrash.IsReady);
            Assert.IsTrue(_crash.Options!.UncaughtExceptionsAsFatal);
            CollectionAssert.AreEqual(new[] { "boot" }, _crash.Logs);
            Assert.AreEqual("13", _crash.Keys["level"], "latest value wins");
            Assert.AreEqual("early", _crash.Exceptions.Single().Message);
            CollectionAssert.AreEqual(new[] { "player_1" }, _crash.UserIds);

            NovaCrash.Log("after");
            NovaAnalytics.SetUserId(null);
            CollectionAssert.AreEqual(new[] { "boot", "after" }, _crash.Logs);
            CollectionAssert.AreEqual(new[] { "player_1", null }, _crash.UserIds);
        }

        [Test]
        public void Crash_PendingLogsAreBounded()
        {
            for (int i = 0; i < NovaCrash.MaxPendingLogs + 5; i++) NovaCrash.Log("l" + i);

            Init(Setup(withAds: false, withCrash: true));
            CompleteFirebaseInit();

            Assert.AreEqual(NovaCrash.MaxPendingLogs, _crash.Logs.Count);
            Assert.AreEqual("l5", _crash.Logs[0], "oldest dropped");
        }

        [Test]
        public void Crash_Disabled_CreatesNoReporter()
        {
            Init(Setup(withAds: false));
            CompleteFirebaseInit();
            NovaCrash.Log("ignored");

            Assert.IsNull(_crash.Options);
            Assert.IsFalse(NovaCrash.IsReady);
            CollectionAssert.IsEmpty(_crash.Logs);
        }

        [Test]
        public void Crash_InitFailure_KeepsCallsPending()
        {
            _crash.InitResult = Task.FromResult<SdkResult>(new SdkError("x", SdkErrorCategory.Unavailable, "no firebase", false));
            Init(Setup(withAds: false, withCrash: true));
            CompleteFirebaseInit();

            NovaCrash.Log("kept");

            Assert.IsFalse(NovaCrash.IsReady);
            CollectionAssert.IsEmpty(_crash.Logs);
        }

        [Test]
        public void Crash_RetryableInitFailure_RetriesAndFlushesPendingCalls()
        {
            _crash.InitResult = Task.FromResult<SdkResult>(
                new SdkError("x", SdkErrorCategory.Timeout, "firebase timeout", true));
            NovaCrash.Log("before retry");
            Init(Setup(withAds: false, withCrash: true));
            CompleteFirebaseInit();

            Assert.AreEqual(1, _crash.InitCount);
            Assert.IsFalse(NovaCrash.IsReady);

            _crash.InitResult = Task.FromResult(SdkResult.Ok);
            _h.Scheduler.Advance(TimeSpan.FromSeconds(5));
            _h.Main.Drain();

            Assert.AreEqual(2, _crash.InitCount);
            Assert.IsTrue(NovaCrash.IsReady);
            CollectionAssert.AreEqual(new[] { "before retry" }, _crash.Logs);
        }

        [Test]
        public void Att_GoogleUmp_AskedAfterConsentForm_ConsentAppliedOnceWithAnswer()
        {
            Init(Setup(withAdjust: true, consent: NovaConsentSource.GoogleUmp, ios: true, attOnStartup: true), withAtt: true);
            CompleteFirebaseInit();
            Assert.AreEqual(0, _att.RequestCount, "ATT waits for the consent form");

            _ump.Gather.TrySetResult(SdkResult<ConsentSnapshot>.Ok(ConsentSnapshot.AllGranted));
            _h.Main.Drain();
            Assert.AreEqual(1, _att.RequestCount);
            Assert.AreSame(ConsentSnapshot.Unknown, NovaSdk.Consent, "consent is applied after the ATT answer");
            Assert.AreEqual(AdsModuleState.Blocked, NovaAds.Service!.State.Value);

            _att.Answer(AttStatus.Authorized);
            _h.Main.Drain();

            Assert.AreEqual(AttStatus.Authorized, NovaSdk.Consent.Att);
            Assert.IsTrue(NovaSdk.Consent.CanRequestAds);
            Assert.AreEqual(AttStatus.Authorized, _adjust.Consents.Single().Att, "Adjust starts with the ATT answer");
            Assert.AreEqual(AdsModuleState.Ready, NovaAds.Service!.State.Value);
            Assert.AreEqual(AttStatus.Authorized, NovaPrivacy.TrackingStatus);
        }

        [Test]
        public void Att_StoredConsent_AdsWaitForAttAnswer()
        {
            _ump.Stored = ConsentSnapshot.AllGranted;
            Init(Setup(consent: NovaConsentSource.GoogleUmp, ios: true, attOnStartup: true), withAtt: true);
            CompleteFirebaseInit();
            Assert.AreEqual(AttStatus.NotDetermined, NovaSdk.Consent.Att);
            Assert.AreEqual(AdsModuleState.Blocked, NovaAds.Service!.State.Value, "ads hold for the ATT answer");

            _ump.Gather.TrySetResult(SdkResult<ConsentSnapshot>.Ok(ConsentSnapshot.AllGranted));
            _h.Main.Drain();
            _att.Answer(AttStatus.Denied);
            _h.Main.Drain();

            Assert.AreEqual(AttStatus.Denied, NovaSdk.Consent.Att);
            Assert.AreEqual(AdsModuleState.Ready, NovaAds.Service!.State.Value);
        }

        [Test]
        public void Att_AlreadyAnswered_IsNotAskedAgain_AndOverridesGameConsent()
        {
            _att.Status = AttStatus.Authorized;
            NovaSdk.SetConsent(ConsentSnapshot.AllGranted);
            Init(Setup(ios: true, attOnStartup: true), withAtt: true);
            CompleteFirebaseInit();

            Assert.AreEqual(0, _att.RequestCount);
            Assert.AreEqual(AttStatus.Authorized, NovaSdk.Consent.Att);
            Assert.AreEqual(AdsModuleState.Ready, NovaAds.Service!.State.Value);
        }

        [Test]
        public void Att_GameConsentSource_WaitsForFirstGameConsentBeforeAsking()
        {
            Init(Setup(withAdjust: true, consent: NovaConsentSource.Game, ios: true, attOnStartup: true), withAtt: true);
            CompleteFirebaseInit();

            Assert.AreEqual(0, _att.RequestCount, "the game's CMP has not supplied consent yet");

            NovaSdk.SetConsent(ConsentSnapshot.AllGranted);
            _h.Main.Drain();

            Assert.AreEqual(1, _att.RequestCount);
            Assert.AreEqual(AttStatus.NotDetermined, NovaSdk.Consent.Att);
            Assert.AreEqual(AdsModuleState.Blocked, NovaAds.Service!.State.Value);
            CollectionAssert.IsEmpty(_adjust.Consents, "Adjust must not start before the ATT answer");

            _att.Answer(AttStatus.Authorized);
            _h.Main.Drain();

            Assert.AreEqual(AttStatus.Authorized, NovaSdk.Consent.Att);
            Assert.AreEqual(AdsModuleState.Ready, NovaAds.Service!.State.Value);
            Assert.AreEqual(AttStatus.Authorized, _adjust.Consents.Single().Att);
        }

        [Test]
        public void Att_NotOnStartup_AdsDoNotWait_RequestTrackingAsksOnce()
        {
            NovaSdk.SetConsent(ConsentSnapshot.AllGranted);
            Init(Setup(ios: true, attOnStartup: false), withAtt: true);
            CompleteFirebaseInit();
            Assert.AreEqual(0, _att.RequestCount);
            Assert.AreEqual(AttStatus.NotDetermined, NovaSdk.Consent.Att);
            Assert.AreEqual(AdsModuleState.Ready, NovaAds.Service!.State.Value);

            var answers = new List<AttStatus>();
            NovaPrivacy.RequestTracking(answers.Add);
            NovaPrivacy.RequestTracking(answers.Add);
            _h.Main.Drain();
            Assert.AreEqual(1, _att.RequestCount, "one system popup for concurrent requests");

            _att.Answer(AttStatus.Denied);
            _h.Main.Drain();

            CollectionAssert.AreEqual(new[] { AttStatus.Denied, AttStatus.Denied }, answers);
            Assert.AreEqual(AttStatus.Denied, NovaSdk.Consent.Att);
        }

        [Test]
        public void Att_UnderAge_IsNeverAsked()
        {
            NovaSdk.SetConsent(ConsentSnapshot.AllGranted);
            Init(Setup(ios: true, attOnStartup: false, attAllowed: false), withAtt: true);
            CompleteFirebaseInit();

            AttStatus? answer = null;
            NovaPrivacy.RequestTracking(s => answer = s);
            _h.Main.Drain();

            Assert.AreEqual(0, _att.RequestCount);
            Assert.AreEqual(AttStatus.NotDetermined, answer);
            Assert.AreEqual(AdsModuleState.Ready, NovaAds.Service!.State.Value);
        }

        [Test]
        public void Att_NoAdapter_RequestTrackingReturnsNotApplicable()
        {
            Init(Setup(withAds: false));
            AttStatus? answer = null;
            NovaPrivacy.RequestTracking(s => answer = s);
            _h.Main.Drain();

            Assert.AreEqual(AttStatus.NotApplicable, answer);
            Assert.AreEqual(AttStatus.NotApplicable, NovaPrivacy.TrackingStatus);
        }

        sealed class FakeAttPlatform : IAttPlatform
        {
            TaskCompletionSource<AttStatus>? _pending;
            public AttStatus Status { get; set; } = AttStatus.NotDetermined;
            public int RequestCount;

            public Task<AttStatus> RequestAsync(CancellationToken ct)
            {
                RequestCount++;
                _pending = new TaskCompletionSource<AttStatus>();
                return _pending.Task;
            }

            public void Answer(AttStatus status)
            {
                Status = status;
                _pending?.TrySetResult(status);
            }
        }

        sealed class FakeSinkSettings : ITrackingSinkSettings { }

        sealed class FakeConsentPlatform : IConsentPlatform
        {
            public readonly TaskCompletionSource<SdkResult<ConsentSnapshot>> Gather =
                new TaskCompletionSource<SdkResult<ConsentSnapshot>>();
            public readonly TaskCompletionSource<SdkResult<ConsentSnapshot>> PrivacyOptions =
                new TaskCompletionSource<SdkResult<ConsentSnapshot>>();
            public ConsentSnapshot Stored = ConsentSnapshot.Unknown;
            public Exception? GatherException;
            public bool PrivacyOptionsRequired;
            public int GatherCount;
            public bool Disposed;

            public string Id => ConsentPlatformIds.GoogleUmp;
            public bool IsPrivacyOptionsRequired => PrivacyOptionsRequired;
            public ConsentSnapshot ReadStored(ConsentGatherOptions options) => Stored;

            public Task<SdkResult<ConsentSnapshot>> GatherAsync(ConsentGatherOptions options, CancellationToken ct)
            {
                GatherCount++;
                if (GatherException != null)
                {
                    var exception = GatherException;
                    GatherException = null;
                    throw exception;
                }
                return Gather.Task;
            }

            public Task<SdkResult<ConsentSnapshot>> ShowPrivacyOptionsAsync(CancellationToken ct) => PrivacyOptions.Task;
            public void Dispose() => Disposed = true;
        }

        sealed class FakeSource : IRemoteConfigSource
        {
            public string Id => RemoteConfigSourceIds.Firebase;
            public Task<SdkResult> InitializeAsync(CancellationToken ct) => Task.FromResult(SdkResult.Ok);

            public Task<SdkResult<RemoteConfigFetchResult>> FetchAndActivateAsync(TimeSpan timeout, CancellationToken ct) =>
                Task.FromResult(SdkResult<RemoteConfigFetchResult>.Ok(
                    new RemoteConfigFetchResult(new Dictionary<string, string>(), true, null)));

            public void Dispose() { }
        }

        sealed class FakeSink : ITrackingSink, IUserPropertySink, IAttributionSink
        {
            public FakeSink(string id, SinkCapabilities capabilities)
            {
                Id = id;
                Capabilities = capabilities;
            }

            public readonly TaskCompletionSource<SdkResult> Init = new TaskCompletionSource<SdkResult>();
            public readonly List<TrackingEvent> Events = new List<TrackingEvent>();
            public readonly List<AdRevenueEvent> AdRevenue = new List<AdRevenueEvent>();
            public readonly List<PurchaseRevenueEvent> Purchases = new List<PurchaseRevenueEvent>();
            public readonly List<ConsentSnapshot> Consents = new List<ConsentSnapshot>();
            public bool Ready;
            public bool Disposed;
            public IAttributionListener? Listener;

            public string Id { get; }
            public bool IsReady => Ready;
            public SinkCapabilities Capabilities { get; }

            public Task<SdkResult> InitializeAsync(CancellationToken ct) => Init.Task;
            public void Apply(ConsentSnapshot snapshot) => Consents.Add(snapshot);
            public void Send(TrackingEvent e) => Events.Add(e);
            public void SendAdRevenue(AdRevenueEvent e) => AdRevenue.Add(e);
            public void SendPurchase(PurchaseRevenueEvent e) => Purchases.Add(e);
            public void SetUserProperty(string name, string? value) { }
            public void SetUserId(string? id) { }
            public void SetListener(IAttributionListener? listener) => Listener = listener;
            public void Dispose() => Disposed = true;
        }
    }
}
