#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.Entitlements;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.RemoteConfig;
using NovaGames.Mobile.Testing;
using NUnit.Framework;

namespace NovaGames.Mobile.Tests
{
    public sealed class AdsManagerTests
    {
        static readonly AdUnit InterUnit = new AdUnit("inter", AdFormat.Interstitial, "inter-id", AdsProvider.Max);
        static readonly AdUnit RewardedUnit = new AdUnit("rewarded", AdFormat.Rewarded, "rewarded-id", AdsProvider.Max);
        static readonly AdUnit AppOpenUnit = new AdUnit("aoa", AdFormat.AppOpen, "aoa-id", AdsProvider.Max);
        static readonly AdUnit BannerUnit = new AdUnit("banner", AdFormat.Banner, "banner-id", AdsProvider.Max);
        static readonly AdUnit MrecUnit = new AdUnit("mrec", AdFormat.MRec, "mrec-id", AdsProvider.Max);

        static readonly InterstitialPlacement LevelEnd = new InterstitialPlacement("level_end");
        static readonly InterstitialPlacement Pause = new InterstitialPlacement("pause");
        static readonly RewardedPlacement Revive = new RewardedPlacement("revive");
        static readonly AppOpenPlacement Resume = new AppOpenPlacement("resume");
        static readonly BannerPlacement HomeBanner = new BannerPlacement("home_banner");
        static readonly BannerPlacement ShopBanner = new BannerPlacement("shop_banner");
        static readonly MrecPlacement ResultMrec = new MrecPlacement("result_mrec");

        TestHarness _h = null!;
        FakeAdsAdapter _adapter = null!;
        SdkProperty<ConsentSnapshot> _consent = null!;
        FakeEntitlements _entitlements = null!;
        FakeLifecycle _lifecycle = null!;
        RecordingRevenuePipeline _revenue = null!;
        AdsManager _ads = null!;

        [SetUp]
        public void SetUp()
        {
            _h = new TestHarness();
            _adapter = new FakeAdsAdapter();
            _consent = new SdkProperty<ConsentSnapshot>(TestConsent.Granted);
            _entitlements = new FakeEntitlements();
            _lifecycle = new FakeLifecycle();
            _revenue = new RecordingRevenuePipeline();
        }

        [TearDown]
        public void TearDown()
        {
            _ads?.Dispose();
            _h.Dispose();
        }

        static AdsOptions Options() => new AdsOptions(
            new[] { InterUnit, RewardedUnit, AppOpenUnit, BannerUnit, MrecUnit },
            new[]
            {
                new AdPlacementBinding(LevelEnd.Id, InterUnit.Key),
                new AdPlacementBinding(Pause.Id, InterUnit.Key),
                new AdPlacementBinding(Revive.Id, RewardedUnit.Key),
                new AdPlacementBinding(Resume.Id, AppOpenUnit.Key),
                new AdPlacementBinding(HomeBanner.Id, BannerUnit.Key),
                new AdPlacementBinding(ShopBanner.Id, BannerUnit.Key),
                new AdPlacementBinding(ResultMrec.Id, MrecUnit.Key),
            });

        AdsManager Create(AdsOptions? options = null, bool isIos = false, IRemoteConfigService? remoteConfig = null,
                          AdsConfigKeys? keys = null)
        {
            _ads = new AdsManager(_adapter, options ?? Options(), _h.Context, new AdsDependencies(_consent)
            {
                Entitlements = _entitlements,
                Lifecycle = _lifecycle,
                Network = new FakeNetwork(),
                Revenue = _revenue,
                IsIos = isIos,
                RemoteConfig = remoteConfig,
                ConfigKeys = keys ?? AdsConfigKeys.Default,
                Random = () => 0.5,
            });
            return _ads;
        }

        AdsManager CreateReady(bool loadAll = true)
        {
            var ads = Create();
            var result = _h.Run(ads.InitializeAsync(CancellationToken.None));
            Assert.IsTrue(result.IsSuccess, result.ToString());
            Assert.AreEqual(AdsModuleState.Ready, ads.State.Value);
            ads.SetPlayerLevel(10);
            if (loadAll) LoadAll();
            return ads;
        }

        void LoadAll()
        {
            _adapter.FireLoaded(InterUnit);
            _adapter.FireLoaded(RewardedUnit);
            _adapter.FireLoaded(AppOpenUnit);
            _h.Main.Drain();
        }

        // Show -> displayed -> closed, trả kết quả cuối.
        InterstitialResult ShowInterstitial(InterstitialPlacement placement)
        {
            var task = _ads.ShowAsync(placement, CancellationToken.None);
            if (task.IsCompleted) return task.Result;
            var id = _adapter.LastOperationId;
            _adapter.FireDisplayed(id);
            _adapter.FireClosed(id);
            return _h.Run(task);
        }

        // ---------------- Init & gate ----------------

        [Test]
        public void AdapterInit_ReceivesProviderSettingsAndActiveRemoteConfig_Untouched()
        {
            var settings = new TestProviderSettings();
            var snapshot = new RemoteConfigSnapshot(3, ConfigSource.Remote, DateTime.UtcNow,
                new Dictionary<string, string> { ["vendor_flag"] = "true" });
            Create(Options() with { Providers = new[] { new AdsProviderOptions(AdsProvider.Max) { Settings = settings } } }, remoteConfig: new StaticRemoteConfig(snapshot));
            _h.Run(_ads.InitializeAsync(CancellationToken.None));

            var init = _adapter.LastInitOptions!;
            Assert.AreSame(settings, init.For(AdsProvider.Max).Settings);
            Assert.AreSame(snapshot, init.RemoteConfig);
        }

        [Test]
        public void AdapterInit_WithoutRemoteConfig_GetsEmptySnapshot()
        {
            CreateReady(loadAll: false);
            Assert.IsNull(_adapter.LastInitOptions!.For(AdsProvider.Max).Settings);
            Assert.AreSame(RemoteConfigSnapshot.Empty, _adapter.LastInitOptions.RemoteConfig);
        }

        sealed class TestProviderSettings : IAdsProviderSettings { }

        sealed class StaticRemoteConfig : IRemoteConfigService
        {
            public StaticRemoteConfig(RemoteConfigSnapshot snapshot) { Current = new SdkProperty<RemoteConfigSnapshot>(snapshot); }

            public ISdkProperty<RemoteConfigSnapshot> Current { get; }

            public Task<SdkResult<RemoteConfigSnapshot>> FetchAndActivateAsync(TimeSpan timeout, CancellationToken ct) =>
                Task.FromResult(SdkResult<RemoteConfigSnapshot>.Ok(Current.Value));
        }

        [Test]
        public void ProviderNone_IsDisabled_AndAdapterNeverInitialized()
        {
            var ads = Create(AdsOptions.Disabled);
            var result = _h.Run(ads.InitializeAsync(CancellationToken.None));

            Assert.AreEqual(SdkErrorCategory.Configuration, result.Error!.Category);
            Assert.AreEqual(AdsModuleState.Disabled, ads.State.Value);
            Assert.AreEqual(0, _adapter.InitializeCount);
            Assert.AreEqual(ShowOutcome.Disabled, _h.Run(ads.ShowAsync(LevelEnd, CancellationToken.None)).Outcome);
        }

        [Test]
        public void ConsentNotGranted_BlocksInit_ThenInitsWhenConsentArrives_WithConsentAppliedFirst()
        {
            _consent.Set(TestConsent.Denied);
            var ads = Create();
            var result = _h.Run(ads.InitializeAsync(CancellationToken.None));

            Assert.AreEqual(SdkErrorCategory.Blocked, result.Error!.Category);
            Assert.AreEqual(AdsModuleState.Blocked, ads.State.Value);
            Assert.AreEqual(0, _adapter.InitializeCount);
            Assert.AreEqual(0, _adapter.Loads.Count, "no ad traffic before consent");

            _consent.Set(TestConsent.Granted);
            _h.Main.Drain();

            Assert.AreEqual(AdsModuleState.Ready, ads.State.Value);
            Assert.AreEqual(1, _adapter.InitializeCount);
            int apply = _adapter.Calls.LastIndexOf("Apply");
            int init = _adapter.Calls.IndexOf("Initialize");
            Assert.Less(apply, init, "consent must be applied before adapter init");
            Assert.AreEqual(1, _adapter.LoadCount(InterUnit.Key), "full-screen units preload when ready");
        }

        [Test]
        public void Ios_WaitsForAtt_ThenStartsAfterHoldTimeout()
        {
            _consent.Set(TestConsent.Granted with { Att = AttStatus.NotDetermined });
            var ads = Create(isIos: true);
            ads.InitializeAsync(CancellationToken.None);
            _h.Main.Drain();
            Assert.AreEqual(0, _adapter.InitializeCount, "iOS must not init ads before ATT is decided");

            _h.Scheduler.Advance(TimeSpan.FromSeconds(30));
            _h.Main.Drain();
            Assert.AreEqual(1, _adapter.InitializeCount, "hold expired: start without IDFA");
            Assert.AreEqual(AdsModuleState.Ready, ads.State.Value);
        }

        [Test]
        public void Ios_AttAnswered_StartsImmediately()
        {
            _consent.Set(TestConsent.Granted with { Att = AttStatus.NotDetermined });
            var ads = Create(isIos: true);
            ads.InitializeAsync(CancellationToken.None);
            _consent.Set(TestConsent.Granted with { Att = AttStatus.Denied });
            _h.Main.Drain();
            Assert.AreEqual(1, _adapter.InitializeCount);
        }

        [Test]
        public void AdapterInitFailure_StaysFailed_AndShowsReturnBlockedOrDisabled()
        {
            _adapter.AutoInitialize = false;
            var ads = Create();
            var init = ads.InitializeAsync(CancellationToken.None);
            _adapter.CompleteInit(new SdkError("fake", SdkErrorCategory.Provider, "boom", false));
            var result = _h.Run(init);

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(AdsModuleState.Failed, ads.State.Value);
            _consent.Set(TestConsent.Granted with { PolicyVersion = "2" });
            _h.Main.Drain();
            Assert.AreEqual(1, _adapter.InitializeCount, "failed adapter is not re-initialized");
        }

        [Test]
        public void AdapterInitRetryableFailure_RetriesWithBackoff_ThenReady()
        {
            _adapter.AutoInitialize = false;
            var ads = Create();
            var init = ads.InitializeAsync(CancellationToken.None);
            _adapter.CompleteInit(new SdkError("fake", SdkErrorCategory.Network, "offline", true));

            Assert.IsFalse(_h.Run(init).IsSuccess);
            Assert.AreEqual(AdsModuleState.Initializing, ads.State.Value, "retryable failure keeps trying");

            _h.Scheduler.Advance(TimeSpan.FromSeconds(2));
            _h.Main.Drain();
            Assert.AreEqual(2, _adapter.InitializeCount, "re-initialized after ~2 s backoff");

            _adapter.CompleteInit(SdkResult.Ok);
            _h.Main.Drain();
            Assert.AreEqual(AdsModuleState.Ready, ads.State.Value);
            Assert.IsTrue(_h.Run(ads.InitializeAsync(CancellationToken.None)).IsSuccess);
        }

        [Test]
        public void AdapterInitWithoutCallback_TimesOut_ThenReadyWhenVendorFinallyCallsBack()
        {
            _adapter.AutoInitialize = false;
            var ads = Create();
            var init = ads.InitializeAsync(CancellationToken.None);
            _h.Main.Drain();
            _h.Scheduler.Advance(TimeSpan.FromSeconds(10));

            Assert.AreEqual(SdkErrorCategory.Timeout, _h.Run(init).Error!.Category);
            Assert.AreEqual(AdsModuleState.Initializing, ads.State.Value, "not stuck: timeout is retried");

            _h.Scheduler.Advance(TimeSpan.FromSeconds(2));
            _h.Main.Drain();
            _adapter.CompleteInit(SdkResult.Ok);
            _h.Main.Drain();
            Assert.AreEqual(AdsModuleState.Ready, ads.State.Value);
        }

        // ---------------- Interstitial capping ----------------

        [Test]
        public void Interstitial_ShowsOnce_ThenCappedByInterval_UntilIntervalPasses()
        {
            CreateReady();

            Assert.AreEqual(ShowOutcome.Shown, ShowInterstitial(LevelEnd).Outcome);
            _adapter.FireLoaded(InterUnit);
            _h.Main.Drain();

            Assert.AreEqual(ShowOutcome.Capped, _h.Run(_ads.ShowAsync(Pause, CancellationToken.None)).Outcome,
                "interval is global across placements sharing the unit");

            _h.Clock.Advance(TimeSpan.FromSeconds(30));
            Assert.AreEqual(ShowOutcome.Shown, ShowInterstitial(Pause).Outcome);
        }

        [Test]
        public void Interstitial_CappedBelowStartLevel_AndWhenLevelUnknown()
        {
            CreateReady();
            _ads.SetPlayerLevel(0);
            Assert.AreEqual(AdAvailability.Capped, _ads.GetAvailability(LevelEnd));
            _ads.SetPlayerLevel(2);
            Assert.AreEqual(AdAvailability.Capped, _ads.GetAvailability(LevelEnd));
            _ads.SetPlayerLevel(3);
            Assert.AreEqual(AdAvailability.Ready, _ads.GetAvailability(LevelEnd));
        }

        [Test]
        public void Interstitial_CappedForSixtySecondsAfterRewarded()
        {
            CreateReady();
            var rewarded = _ads.ShowAsync(Revive, RewardedShowOptions.Immediate, CancellationToken.None);
            var id = _adapter.LastOperationId;
            _adapter.FireDisplayed(id);
            _adapter.FireRewarded(id);
            _adapter.FireClosed(id);
            Assert.AreEqual(ShowOutcome.Rewarded, _h.Run(rewarded).Outcome);

            _h.Clock.Advance(TimeSpan.FromSeconds(59));
            Assert.AreEqual(AdAvailability.Capped, _ads.GetAvailability(LevelEnd));
            _h.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.AreEqual(AdAvailability.Ready, _ads.GetAvailability(LevelEnd));
        }

        [Test]
        public void Capping_PersistsAcrossManagerInstances()
        {
            CreateReady();
            Assert.AreEqual(ShowOutcome.Shown, ShowInterstitial(LevelEnd).Outcome);
            _ads.Dispose();

            _adapter = new FakeAdsAdapter();
            CreateReady();
            Assert.AreEqual(AdAvailability.Capped, _ads.GetAvailability(LevelEnd));
        }

        [Test]
        public void RemoteConfig_PolicyUsesCustomKeyNames_AndClampsSafetyFloor()
        {
            var keys = new AdsConfigKeys(new AdsConfigKeyNames { InterstitialEnabled = "inter_ad_on_off" });
            var policy = keys.Read(new RemoteConfigSnapshot(1, ConfigSource.Remote, DateTime.UtcNow, new Dictionary<string, string>
            {
                ["inter_ad_on_off"] = "false",
                ["ad_inter_interval"] = "5",
                ["ad_inter_start_level"] = "7",
                ["ad_inter_after_rewarded"] = "-3",
                ["ad_rewarded_grace"] = "99999",
            }));

            Assert.IsFalse(policy.InterstitialEnabled);
            Assert.AreEqual(TimeSpan.FromSeconds(30), policy.InterstitialInterval, "clamped to 30 s floor");
            Assert.AreEqual(7, policy.InterstitialStartLevel);
            Assert.AreEqual(TimeSpan.FromSeconds(60), policy.InterstitialAfterRewarded, "invalid value falls back to default");
            Assert.AreEqual(TimeSpan.FromMilliseconds(3000), policy.RewardGrace, "clamped to 3 s ceiling");
        }

        [Test]
        public void RemoteConfig_KeysFromGameDefinitions_UseTheirNameAndDefault()
        {
            // Như RemoteKeys.Ads(definitions): key (tên + default) do game cấp, SDK chỉ đọc.
            var keys = new AdsConfigKeys
            {
                InterstitialInterval = new IntKey("inter_ad_capping_time", 90),
                BannerEnabled = new BoolKey("banner_ad_on_off", false),
            };
            CollectionAssert.Contains(keys.All.Select(k => k.Name).ToList(), "inter_ad_capping_time");

            var defaults = keys.Read(RemoteConfigSnapshot.Empty);
            Assert.AreEqual(TimeSpan.FromSeconds(90), defaults.InterstitialInterval, "default comes from the game key");
            Assert.IsFalse(defaults.BannerEnabled);

            var remote = keys.Read(new RemoteConfigSnapshot(1, ConfigSource.Remote, DateTime.UtcNow, new Dictionary<string, string>
            {
                ["inter_ad_capping_time"] = "45",
                ["ad_inter_interval"] = "300", // tên mặc định của SDK không còn được đọc
            }));
            Assert.AreEqual(TimeSpan.FromSeconds(45), remote.InterstitialInterval);
        }

        [Test]
        public void AdMobConfigAsset_UseGoogleTestIds_AppliesToEveryBuild_AndWarnsInRelease()
        {
            var config = UnityEngine.ScriptableObject.CreateInstance<AdMobAdsConfig>();
            try
            {
                var placements = new AdPlacement[] { LevelEnd, Revive, HomeBanner };
                var off = config.ToOptions(placements, isDevelopment: true, isIos: false);
                Assert.AreEqual(AdsProvider.AdMob, off.Providers.Single().Provider);
                Assert.AreEqual(0, off.AdUnits.Count, "test IDs are off by default and no real IDs are configured");
                Assert.IsFalse(config.UsesTestAdUnitIds);
                CollectionAssert.IsNotEmpty(config.Validate(isDevelopment: true, isIos: false));

                var serialized = new UnityEditor.SerializedObject(config);
                serialized.FindProperty("useGoogleTestIds").boolValue = true;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                foreach (var isDevelopment in new[] { true, false })
                {
                    var options = config.ToOptions(placements, isDevelopment, isIos: false);
                    Assert.AreEqual(5, options.AdUnits.Count, "development=" + isDevelopment);
                    Assert.AreEqual("ca-app-pub-3940256099942544/1033173712",
                        options.AdUnits.First(u => u.Format == AdFormat.Interstitial).AdUnitId);
                    Assert.AreEqual(3, options.Placements.Count);
                    CollectionAssert.IsEmpty(options.Validate());
                }
                Assert.IsTrue(config.UsesTestAdUnitIds);
                CollectionAssert.IsEmpty(config.Validate(isDevelopment: true, isIos: false));
                StringAssert.Contains("release build", string.Join("\n", config.Validate(isDevelopment: false, isIos: false)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(config);
            }
        }

        // ---------------- Rewarded & exactly-once ----------------

        [Test]
        public void Rewarded_RewardBeforeClose_IsRewarded()
        {
            CreateReady();
            var task = _ads.ShowAsync(Revive, RewardedShowOptions.Immediate, CancellationToken.None);
            var id = _adapter.LastOperationId;
            _adapter.FireDisplayed(id);
            _adapter.FireRewarded(id, 5);
            _adapter.FireClosed(id);

            var result = _h.Run(task);
            Assert.AreEqual(ShowOutcome.Rewarded, result.Outcome);
            Assert.AreEqual(5, result.Reward!.Amount);
            Assert.AreEqual(id, result.ShowOperationId);
        }

        [Test]
        public void Rewarded_RewardAfterCloseWithinGrace_IsRewarded()
        {
            CreateReady();
            var task = _ads.ShowAsync(Revive, RewardedShowOptions.Immediate, CancellationToken.None);
            var id = _adapter.LastOperationId;
            _adapter.FireDisplayed(id);
            _adapter.FireClosed(id);
            _h.Main.Drain();
            Assert.IsFalse(task.IsCompleted, "close without reward waits for grace window");

            _h.Scheduler.Advance(TimeSpan.FromMilliseconds(500));
            _adapter.FireRewarded(id);
            Assert.AreEqual(ShowOutcome.Rewarded, _h.Run(task).Outcome);
        }

        [Test]
        public void Rewarded_NoReward_ClosedWithoutRewardAfterGrace_AndLateRewardIgnored()
        {
            CreateReady();
            var task = _ads.ShowAsync(Revive, RewardedShowOptions.Immediate, CancellationToken.None);
            var id = _adapter.LastOperationId;
            _adapter.FireDisplayed(id);
            _adapter.FireClosed(id);
            _h.Main.Drain();
            _h.Scheduler.Advance(TimeSpan.FromSeconds(1));

            var result = _h.Run(task);
            Assert.AreEqual(ShowOutcome.ClosedWithoutReward, result.Outcome);
            Assert.IsNull(result.Reward);

            _adapter.FireRewarded(id);
            _h.Main.Drain();
            Assert.IsTrue(_h.Log.Entries.Exists(e => e.Message.Contains("ads.reward_late")));
        }

        [Test]
        public void DuplicateCloseCallbacks_CompleteOnce()
        {
            CreateReady();
            var task = _ads.ShowAsync(LevelEnd, CancellationToken.None);
            var id = _adapter.LastOperationId;
            _adapter.FireDisplayed(id);
            _adapter.FireClosed(id);
            _adapter.FireClosed(id);
            _adapter.FireDisplayFailed(id);

            Assert.AreEqual(ShowOutcome.Shown, _h.Run(task).Outcome);
            _h.Main.Drain();
            Assert.AreEqual(1, _h.Store.Values.Count, "capping recorded once");
        }

        [Test]
        public void Rewarded_WaitForLoad_ShowsWhenLoadedInTime()
        {
            CreateReady(loadAll: false);
            var task = _ads.ShowAsync(Revive, new RewardedShowOptions(TimeSpan.FromSeconds(3)), CancellationToken.None);
            _h.Main.Drain();
            Assert.AreEqual(0, _adapter.Shows.Count);

            _adapter.FireLoaded(RewardedUnit);
            _h.Main.Drain();
            Assert.AreEqual(1, _adapter.Shows.Count);
            var id = _adapter.LastOperationId;
            _adapter.FireDisplayed(id);
            _adapter.FireRewarded(id);
            _adapter.FireClosed(id);
            Assert.AreEqual(ShowOutcome.Rewarded, _h.Run(task).Outcome);
        }

        [Test]
        public void Rewarded_WaitForLoad_TimesOutAsNotReady()
        {
            CreateReady(loadAll: false);
            var task = _ads.ShowAsync(Revive, new RewardedShowOptions(TimeSpan.FromSeconds(3)), CancellationToken.None);
            _h.Main.Drain();
            _h.Scheduler.Advance(TimeSpan.FromSeconds(3));
            Assert.AreEqual(ShowOutcome.NotReady, _h.Run(task).Outcome);
        }

        [Test]
        public void ConcurrentShow_SecondIsBusy_AndLockReleasedAfterClose()
        {
            CreateReady();
            var first = _ads.ShowAsync(Revive, RewardedShowOptions.Immediate, CancellationToken.None);
            var second = _ads.ShowAsync(LevelEnd, CancellationToken.None);
            Assert.AreEqual(ShowOutcome.Busy, _h.Run(second).Outcome);

            var id = _adapter.LastOperationId;
            _adapter.FireDisplayed(id);
            _adapter.FireRewarded(id);
            _adapter.FireClosed(id);
            _h.Run(first);
            _h.Clock.Advance(TimeSpan.FromSeconds(61));
            Assert.AreEqual(AdAvailability.Ready, _ads.GetAvailability(LevelEnd));
        }

        [Test]
        public void DisplayTimeout_CompletesDisplayFailed_WithoutCapping()
        {
            CreateReady();
            var task = _ads.ShowAsync(LevelEnd, CancellationToken.None);
            _h.Scheduler.Advance(TimeSpan.FromSeconds(5));

            var result = _h.Run(task);
            Assert.AreEqual(ShowOutcome.DisplayFailed, result.Outcome);
            Assert.AreEqual(SdkErrorCategory.Timeout, result.Error!.Category);
            Assert.AreEqual(0, _h.Store.Values.Count, "not displayed -> no capping");
        }

        // Android: ad activity che Unity -> Unity pause ngay sau Show. Timer vẫn chạy (thread pool) và post về main trong
        // lúc pause; callback của vendor chỉ được xử lý sau resume, xếp sau action của timer.
        [Test]
        public void Android_CallbacksArriveAfterResume_RewardedStillGranted()
        {
            CreateReady();
            var task = _ads.ShowAsync(Revive, RewardedShowOptions.Immediate, CancellationToken.None);
            var id = _adapter.LastOperationId;
            _lifecycle.SetPaused(true);
            _h.Scheduler.Advance(TimeSpan.FromSeconds(30));
            _lifecycle.SetPaused(false);
            _adapter.FireDisplayed(id);
            _adapter.FireRewarded(id);
            _adapter.FireClosed(id);

            var result = _h.Run(task);
            Assert.AreEqual(ShowOutcome.Rewarded, result.Outcome);
            Assert.IsNotNull(result.Reward);
        }

        [Test]
        public void Android_InterstitialShownWhileUnityPaused_RecordsCapping()
        {
            CreateReady();
            var task = _ads.ShowAsync(LevelEnd, CancellationToken.None);
            var id = _adapter.LastOperationId;
            _lifecycle.SetPaused(true);
            _h.Scheduler.Advance(TimeSpan.FromSeconds(20));
            _lifecycle.SetPaused(false);
            _adapter.FireDisplayed(id);
            _adapter.FireClosed(id);

            Assert.AreEqual(ShowOutcome.Shown, _h.Run(task).Outcome);
            _adapter.FireLoaded(InterUnit);
            _h.Main.Drain();
            Assert.AreEqual(AdAvailability.Capped, _ads.GetAvailability(LevelEnd));
        }

        [Test]
        public void Android_PausedBeforeDisplayedCallback_NoCallbacks_ClosedByWatchdog()
        {
            CreateReady();
            var task = _ads.ShowAsync(LevelEnd, CancellationToken.None);
            _lifecycle.SetPaused(true);
            _lifecycle.SetPaused(false);
            _h.Scheduler.Advance(TimeSpan.FromSeconds(3));

            Assert.AreEqual(ShowOutcome.Shown, _h.Run(task).Outcome);
        }

        [Test]
        public void Ios_BackgroundBeforeDisplay_DisplayTimeoutRestartsOnReturn()
        {
            var ads = Create(isIos: true);
            _h.Run(ads.InitializeAsync(CancellationToken.None));
            ads.SetPlayerLevel(10);
            LoadAll();

            var task = ads.ShowAsync(LevelEnd, CancellationToken.None);
            _lifecycle.SetPaused(true);
            _h.Scheduler.Advance(TimeSpan.FromMinutes(1));
            _lifecycle.SetPaused(false);
            _h.Main.Drain();
            Assert.IsFalse(task.IsCompleted, "time in background does not count toward the display timeout");

            _h.Scheduler.Advance(TimeSpan.FromSeconds(5));
            Assert.AreEqual(ShowOutcome.DisplayFailed, _h.Run(task).Outcome);
        }

        [Test]
        public void Presentation_HandlerCalledAroundShow()
        {
            CreateReady();
            var handler = new RecordingPresentationHandler();
            _ads.SetPresentationHandler(handler);

            ShowInterstitial(LevelEnd);
            CollectionAssert.AreEqual(new[] { "showing Interstitial", "closed Interstitial" }, handler.Calls);
        }

        [Test]
        public void CancelledBeforeShow_ReturnsCancelled_WithoutVendorShow()
        {
            CreateReady();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.AreEqual(ShowOutcome.Cancelled, _h.Run(_ads.ShowAsync(LevelEnd, cts.Token)).Outcome);
            Assert.AreEqual(0, _adapter.Shows.Count);
        }

        // ---------------- Load state machine ----------------

        [Test]
        public void LoadFailure_RetriesWithBackoff()
        {
            CreateReady(loadAll: false);
            Assert.AreEqual(1, _adapter.LoadCount(InterUnit.Key));

            _adapter.FireLoadFailed(InterUnit);
            _h.Main.Drain();
            _h.Scheduler.Advance(TimeSpan.FromSeconds(1.9));
            _h.Main.Drain();
            Assert.AreEqual(1, _adapter.LoadCount(InterUnit.Key), "first retry after ~2 s");
            _h.Scheduler.Advance(TimeSpan.FromSeconds(0.2));
            _h.Main.Drain();
            Assert.AreEqual(2, _adapter.LoadCount(InterUnit.Key));

            _adapter.FireLoadFailed(InterUnit);
            _h.Main.Drain();
            _h.Scheduler.Advance(TimeSpan.FromSeconds(4));
            _h.Main.Drain();
            Assert.AreEqual(3, _adapter.LoadCount(InterUnit.Key), "second retry after ~4 s");
        }

        [Test]
        public void FullScreenRetry_ContinuesWhileOffline_AndRecoversWithoutPollingAvailability()
        {
            var network = new FakeNetwork();
            _ads = new AdsManager(_adapter, Options(), _h.Context, new AdsDependencies(_consent)
            {
                Entitlements = _entitlements,
                Lifecycle = _lifecycle,
                Network = network,
                Random = () => 0.5,
            });
            _h.Run(_ads.InitializeAsync(CancellationToken.None));
            Assert.AreEqual(1, _adapter.LoadCount(RewardedUnit.Key));

            _adapter.FireLoadFailed(RewardedUnit, AdLoadFailure.Network);
            _h.Main.Drain();
            network.IsReachable = false;
            _h.Scheduler.Advance(TimeSpan.FromSeconds(2));
            _h.Main.Drain();
            Assert.AreEqual(1, _adapter.LoadCount(RewardedUnit.Key));

            network.IsReachable = true;
            _h.Scheduler.Advance(TimeSpan.FromSeconds(4));
            _h.Main.Drain();
            Assert.AreEqual(2, _adapter.LoadCount(RewardedUnit.Key));
        }

        [Test]
        public void FullScreen_StartedOffline_LoadsAsSoonAsNetworkReturns()
        {
            var network = new FakeNetwork { IsReachable = false };
            _ads = new AdsManager(_adapter, Options(), _h.Context, new AdsDependencies(_consent)
            {
                Entitlements = _entitlements,
                Lifecycle = _lifecycle,
                Network = network,
                Random = () => 0.5,
            });
            _h.Run(_ads.InitializeAsync(CancellationToken.None));
            Assert.AreEqual(0, _adapter.LoadCount(RewardedUnit.Key), "offline: nothing loads, no retry was scheduled");

            network.IsReachable = true;
            _h.Scheduler.Advance(TimeSpan.FromSeconds(5));
            _h.Main.Drain();

            Assert.AreEqual(1, _adapter.LoadCount(RewardedUnit.Key));
        }

        [Test]
        public void DisplayedAd_WithoutCloseCallback_IsClosedByWatchdog_AndUnlocksFullScreen()
        {
            CreateReady();
            var rewarded = _ads.ShowAsync(Revive, RewardedShowOptions.Immediate, CancellationToken.None);
            var id = _adapter.LastOperationId;
            _adapter.FireDisplayed(id);
            _adapter.FireRewarded(id);
            _h.Main.Drain();
            Assert.IsFalse(rewarded.IsCompleted);

            // Vendor mất callback close.
            _h.Scheduler.Advance(TimeSpan.FromMinutes(3));
            _h.Main.Drain();

            Assert.AreEqual(ShowOutcome.Rewarded, _h.Run(rewarded).Outcome, "reward already received is kept");
            Assert.AreNotEqual(AdAvailability.Blocked, _ads.GetAvailability(Revive), "full-screen lock released");
        }

        [Test]
        public void DisplayedAd_ShowWatchdog_PausedWhileAppInBackground()
        {
            CreateReady();
            var show = _ads.ShowAsync(LevelEnd, CancellationToken.None);
            var id = _adapter.LastOperationId;
            _adapter.FireDisplayed(id);
            _h.Main.Drain();

            _lifecycle.SetPaused(true);
            _h.Scheduler.Advance(TimeSpan.FromMinutes(10));
            _h.Main.Drain();
            Assert.IsFalse(show.IsCompleted, "time in background does not count");

            _adapter.FireClosed(id);
            _lifecycle.SetPaused(false);
            Assert.AreEqual(ShowOutcome.Shown, _h.Run(show).Outcome);
        }

        [Test]
        public void LoadTimeout_DiscardsProviderRequest_BeforeRetry()
        {
            CreateReady(loadAll: false);
            _h.Scheduler.Advance(TimeSpan.FromSeconds(60));
            _h.Main.Drain();

            Assert.Contains(InterUnit, _adapter.Discards);
            _h.Scheduler.Advance(TimeSpan.FromSeconds(2));
            _h.Main.Drain();
            Assert.AreEqual(2, _adapter.LoadCount(InterUnit.Key));
        }

        [Test]
        public void ExpiredAd_IsDiscardedAndReloaded_NeverShown()
        {
            CreateReady();
            _h.Scheduler.Advance(TimeSpan.FromHours(4));
            _h.Main.Drain();

            Assert.IsTrue(_adapter.Discards.Contains(AppOpenUnit), "app-open discarded after 4h TTL");
            Assert.AreEqual(2, _adapter.LoadCount(AppOpenUnit.Key));
            Assert.AreNotEqual(AdAvailability.Ready, _ads.AppOpen.GetAvailability(Resume));
        }

        [Test]
        public void UndeclaredPlacement_UsesFirstUnitOfItsFormat()
        {
            CreateReady();
            var undeclared = new InterstitialPlacement("shop_exit");
            Assert.AreEqual(AdAvailability.Ready, _ads.GetAvailability(undeclared));

            var task = _ads.ShowAsync(undeclared, CancellationToken.None);
            Assert.AreEqual(InterUnit, _adapter.LastShownUnit);
            Assert.AreEqual("shop_exit", _adapter.Shows[0].PlacementId);
            var id = _adapter.LastOperationId;
            _adapter.FireDisplayed(id);
            _adapter.FireClosed(id);
            Assert.AreEqual(ShowOutcome.Shown, _h.Run(task).Outcome);
        }

        [Test]
        public void VendorManagedExpiry_NoSdkTtl_AdStaysReady()
        {
            _adapter.ManagesFullScreenExpiry = true;
            CreateReady();
            _h.Scheduler.Advance(TimeSpan.FromHours(5));
            _h.Main.Drain();

            Assert.IsEmpty(_adapter.Discards);
            Assert.AreEqual(1, _adapter.LoadCount(AppOpenUnit.Key));
            Assert.AreEqual(AdAvailability.Ready, _ads.AppOpen.GetAvailability(Resume));
        }

        [Test]
        public void LoadTimeout_VendorAlreadyHasAd_UsesItWithoutDiscard()
        {
            CreateReady(loadAll: false);
            // Callback loaded còn xếp hàng khi timer load hết hạn (Android: Unity pause trong lúc load).
            _adapter.SetReadyWithoutCallback(InterUnit);
            _h.Scheduler.Advance(TimeSpan.FromSeconds(60));
            _h.Main.Drain();

            Assert.IsFalse(_adapter.Discards.Contains(InterUnit));
            Assert.AreEqual(1, _adapter.LoadCount(InterUnit.Key));
            Assert.AreEqual(AdAvailability.Ready, _ads.GetAvailability(LevelEnd));
        }

        [Test]
        public void Retry_VendorStillHoldsLateFill_ReadyWithoutNewLoad()
        {
            CreateReady(loadAll: false);
            _h.Scheduler.Advance(TimeSpan.FromSeconds(60));
            _h.Main.Drain();
            // MAX không hủy được request đã timeout: fill trễ vẫn nằm trong vendor.
            _adapter.SetReadyWithoutCallback(InterUnit);
            _h.Scheduler.Advance(TimeSpan.FromSeconds(2));
            _h.Main.Drain();

            Assert.AreEqual(1, _adapter.LoadCount(InterUnit.Key));
            Assert.AreEqual(AdAvailability.Ready, _ads.GetAvailability(LevelEnd));
        }

        [Test]
        public void AfterShow_UnitReloadsAutomatically()
        {
            CreateReady();
            ShowInterstitial(LevelEnd);
            _h.Main.Drain();
            Assert.AreEqual(2, _adapter.LoadCount(InterUnit.Key));
        }

        // ---------------- Entitlement / consent / kill switch ----------------

        [Test]
        public void RemoveAds_MidSession_DestroysViewsAndBlocksNonRewarded()
        {
            CreateReady();
            Assert.IsTrue(_ads.Show(HomeBanner, new BannerOptions(BannerPosition.Bottom)).IsSuccess);
            Assert.IsTrue(_adapter.AdViews.ContainsKey(BannerUnit.Key));

            _entitlements.Set(EntitlementId.RemoveAds, true);

            Assert.IsFalse(_adapter.AdViews.ContainsKey(BannerUnit.Key));
            Assert.IsTrue(_adapter.Discards.Contains(InterUnit));
            Assert.AreEqual(AdAvailability.RemovedByEntitlement, _ads.GetAvailability(LevelEnd));
            Assert.AreEqual(AdAvailability.RemovedByEntitlement, _ads.AppOpen.GetAvailability(Resume));
            Assert.AreEqual(AdAvailability.Ready, _ads.GetAvailability(Revive), "rewarded stays available");
            Assert.IsFalse(_ads.Show(HomeBanner, new BannerOptions(BannerPosition.Bottom)).IsSuccess);
        }

        [Test]
        public void ConsentRevoked_MidSession_StopsAds()
        {
            CreateReady();
            _ads.Show(HomeBanner, new BannerOptions(BannerPosition.Bottom));

            _consent.Set(TestConsent.Denied);

            Assert.AreEqual(AdsModuleState.Blocked, _ads.State.Value);
            Assert.IsFalse(_adapter.AdViews.ContainsKey(BannerUnit.Key));
            Assert.IsTrue(_adapter.Discards.Contains(RewardedUnit));
            Assert.AreEqual(ShowOutcome.Blocked, _h.Run(_ads.ShowAsync(Revive, RewardedShowOptions.Immediate, CancellationToken.None)).Outcome);
            Assert.AreEqual(TestConsent.Denied, _adapter.AppliedConsents[_adapter.AppliedConsents.Count - 1]);
        }

        [Test]
        public void ForceUpdateRequired_BlocksAds()
        {
            var blocking = new SdkProperty<bool>(true);
            _ads = new AdsManager(_adapter, Options(), _h.Context, new AdsDependencies(_consent) { ForceUpdateBlocking = blocking });
            _h.Run(_ads.InitializeAsync(CancellationToken.None));
            Assert.AreEqual(0, _adapter.InitializeCount);

            blocking.Set(false);
            _h.Main.Drain();
            Assert.AreEqual(1, _adapter.InitializeCount);
        }

        [Test]
        public void ShowBeforeReady_IsQueuedUntilReady()
        {
            _consent.Set(TestConsent.Denied);
            var ads = Create();
            ads.InitializeAsync(CancellationToken.None);
            Assert.IsTrue(ads.Show(HomeBanner, new BannerOptions(BannerPosition.Bottom)).IsSuccess);
            Assert.AreEqual(0, _adapter.AdViewRequests.Count);

            _consent.Set(TestConsent.Granted);
            _h.Main.Drain();
            Assert.IsTrue(_adapter.AdViews.ContainsKey(BannerUnit.Key));
        }

        [Test]
        public void Banner_ShowHideShowBeforeReady_IsCreatedWhenGateOpens()
        {
            _consent.Set(TestConsent.Denied);
            var ads = Create();
            ads.InitializeAsync(CancellationToken.None);
            var options = new BannerOptions(BannerPosition.Bottom);
            ads.Show(HomeBanner, options);
            ads.Hide(HomeBanner);
            ads.Show(HomeBanner, options);

            Assert.AreEqual(AdViewState.Loading, ads.GetState(HomeBanner));
            _consent.Set(TestConsent.Granted);
            _h.Main.Drain();

            Assert.AreEqual(1, _adapter.AdViewRequests.Count);
            Assert.IsTrue(_adapter.AdViews.ContainsKey(BannerUnit.Key));
        }

        // ---------------- Banner / collapsible / MREC ----------------

        [Test]
        public void Collapsible_Required_OnProviderWithoutCapability_IsUnsupported()
        {
            CreateReady();
            var result = _ads.Show(HomeBanner, new BannerOptions(BannerPosition.Bottom, Collapsible: CollapsiblePolicy.Required));
            Assert.AreEqual(SdkErrorCategory.Unavailable, result.Error!.Category);
            Assert.AreEqual(0, _adapter.AdViewRequests.Count, "game UI untouched");
        }

        [Test]
        public void Collapsible_Preferred_OnProviderWithoutCapability_FallsBackToRegularBanner()
        {
            CreateReady();
            Assert.IsTrue(_ads.Show(HomeBanner, new BannerOptions(BannerPosition.Bottom, Collapsible: CollapsiblePolicy.Preferred)).IsSuccess);
            Assert.AreEqual(CollapseDirection.None, _adapter.AdViews[BannerUnit.Key].Collapse);
        }

        [Test]
        public void Collapsible_DirectionFromPosition_AndThrottledByInterval()
        {
            _adapter.Capabilities |= AdCapability.CollapsibleBanner;
            CreateReady();

            _ads.Show(HomeBanner, new BannerOptions(BannerPosition.Top, Collapsible: CollapsiblePolicy.Required));
            Assert.AreEqual(CollapseDirection.Top, _adapter.AdViews[BannerUnit.Key].Collapse);

            _ads.Destroy(HomeBanner);
            _ads.Show(HomeBanner, new BannerOptions(BannerPosition.Bottom, Collapsible: CollapsiblePolicy.Required));
            Assert.AreEqual(CollapseDirection.None, _adapter.AdViews[BannerUnit.Key].Collapse, "within 30 s: regular request");

            _h.Clock.Advance(TimeSpan.FromSeconds(30));
            _ads.Destroy(HomeBanner);
            _ads.Show(HomeBanner, new BannerOptions(BannerPosition.Bottom, Collapsible: CollapsiblePolicy.Required));
            Assert.AreEqual(CollapseDirection.Bottom, _adapter.AdViews[BannerUnit.Key].Collapse);
        }

        [Test]
        public void Banner_ShowIsIdempotent_HideAndDestroyAreNoOpWhenMissing()
        {
            CreateReady();
            var options = new BannerOptions(BannerPosition.Bottom);
            _ads.Show(HomeBanner, options);
            _ads.Show(HomeBanner, options);
            Assert.AreEqual(1, _adapter.AdViewRequests.Count);

            _ads.Hide(ResultMrec);
            _ads.Destroy(ResultMrec);
            Assert.AreEqual(AdViewState.None, _ads.GetState(ResultMrec));
        }

        [Test]
        public void BannerRetry_ContinuesAcrossPause_AndRecoversOnResume()
        {
            CreateReady(loadAll: false);
            _ads.Show(HomeBanner, new BannerOptions(BannerPosition.Bottom));
            _adapter.FireLoadFailed(BannerUnit, AdLoadFailure.Network);
            _h.Main.Drain();

            _lifecycle.SetPaused(true);
            _h.Scheduler.Advance(TimeSpan.FromSeconds(2));
            _h.Main.Drain();
            Assert.AreEqual(1, _adapter.AdViewRequests.Count);

            _lifecycle.SetPaused(false);
            _h.Scheduler.Advance(TimeSpan.FromSeconds(4));
            _h.Main.Drain();
            Assert.AreEqual(2, _adapter.AdViewRequests.Count);
            Assert.AreEqual(AdViewState.Loading, _ads.GetState(HomeBanner));
        }

        [Test]
        public void Banner_FirstLoadFailsWhileHidden_NextShowRecreatesView()
        {
            CreateReady(loadAll: false);
            var options = new BannerOptions(BannerPosition.Bottom);
            _ads.Show(HomeBanner, options);
            _ads.Hide(HomeBanner);
            _adapter.FireLoadFailed(BannerUnit, AdLoadFailure.NoFill);
            _h.Main.Drain();
            _h.Scheduler.Advance(TimeSpan.FromMinutes(1));
            _h.Main.Drain();
            Assert.AreEqual(1, _adapter.AdViewRequests.Count, "no retry while hidden: a new view would appear on screen");

            _ads.Show(HomeBanner, options);

            Assert.AreEqual(2, _adapter.AdViewRequests.Count);
            Assert.Less(_adapter.Calls.LastIndexOf("DestroyAdView " + BannerUnit.Key),
                        _adapter.Calls.LastIndexOf("ShowAdView " + BannerUnit.Key), "empty view is destroyed before the new request");
            Assert.AreEqual(AdViewState.Loading, _ads.GetState(HomeBanner));
        }

        [Test]
        public void Banner_ReshownBeforeFirstLoad_FailureIsStillRetried()
        {
            CreateReady(loadAll: false);
            var options = new BannerOptions(BannerPosition.Bottom);
            _ads.Show(HomeBanner, options);
            _ads.Hide(HomeBanner);
            _ads.Show(HomeBanner, options);
            Assert.AreEqual(AdViewState.Loading, _ads.GetState(HomeBanner), "not Visible until the first load succeeds");

            _adapter.FireLoadFailed(BannerUnit, AdLoadFailure.Network);
            _h.Main.Drain();
            _h.Scheduler.Advance(TimeSpan.FromMinutes(1));
            _h.Main.Drain();

            Assert.AreEqual(3, _adapter.AdViewRequests.Count, "create, re-show, retry");
        }

        [Test]
        public void Banner_FailureAfterSuccessfulLoad_IsLeftToVendorRefresh()
        {
            CreateReady(loadAll: false);
            var options = new BannerOptions(BannerPosition.Bottom);
            _ads.Show(HomeBanner, options);
            _adapter.FireLoaded(BannerUnit);
            _h.Main.Drain();
            _ads.Hide(HomeBanner);
            _adapter.FireLoadFailed(BannerUnit, AdLoadFailure.NoFill);
            _h.Main.Drain();

            _ads.Show(HomeBanner, options);

            Assert.AreEqual(AdViewState.Visible, _ads.GetState(HomeBanner));
            Assert.AreEqual(-1, _adapter.Calls.IndexOf("DestroyAdView " + BannerUnit.Key));
        }

        [Test]
        public void Banner_ShowWithoutOptions_UsesConfiguredPosition()
        {
            Create(Options() with { DefaultBanner = new BannerOptions(BannerPosition.Top) });
            _h.Run(_ads.InitializeAsync(CancellationToken.None));

            Assert.IsTrue(_ads.Banners.Show(HomeBanner).IsSuccess);
            Assert.AreEqual(BannerPosition.Top, _adapter.AdViews[BannerUnit.Key].BannerPosition);
            Assert.AreEqual(BannerPosition.Bottom, AdsOptions.Disabled.DefaultBanner.Position, "default is bottom");
        }

        [Test]
        public void Banner_ChangedOptions_DestroysOldViewBeforeCreatingNew()
        {
            CreateReady();
            _ads.Show(HomeBanner, new BannerOptions(BannerPosition.Bottom));
            _ads.Show(HomeBanner, new BannerOptions(BannerPosition.Top));

            int destroy = _adapter.Calls.LastIndexOf("DestroyAdView " + BannerUnit.Key);
            int create = _adapter.Calls.LastIndexOf("ShowAdView " + BannerUnit.Key);
            Assert.Less(destroy, create);
            Assert.AreEqual(BannerPosition.Top, _adapter.AdViews[BannerUnit.Key].BannerPosition);
        }

        [Test]
        public void Banner_LayoutChange_IsReported()
        {
            CreateReady();
            var layouts = new List<BannerLayoutChanged>();
            _ads.LayoutChanged.Subscribe(layouts.Add);
            _ads.Show(HomeBanner, new BannerOptions(BannerPosition.Bottom));
            _adapter.FireLoaded(BannerUnit);
            _adapter.FireLayout(BannerUnit, new BannerLayout(50, 150, true, true));
            _h.Main.Drain();

            Assert.AreEqual(AdViewState.Visible, _ads.GetState(HomeBanner));
            Assert.AreEqual(1, layouts.Count);
            Assert.AreEqual(150, _ads.GetLayout(HomeBanner).HeightPx);
        }

        [Test]
        public void Mrec_ShowAndHide()
        {
            CreateReady();
            Assert.IsTrue(_ads.Show(ResultMrec, new MrecOptions(MrecPosition.Centered)).IsSuccess);
            _ads.Hide(ResultMrec);
            Assert.AreEqual(AdViewState.Hidden, _ads.GetState(ResultMrec));
            Assert.Contains("HideAdView " + MrecUnit.Key, _adapter.Calls);
        }

        [Test]
        public void Mrec_CustomPixelPosition_AndSize_AreExposed()
        {
            CreateReady();
            var sizes = new List<MrecSizeChanged>();
            _ads.Mrec.SizeChanged.Subscribe(sizes.Add);

            Assert.IsTrue(_ads.Mrec.Show(ResultMrec, MrecOptions.AtPixels(120, 240)).IsSuccess);
            var request = _adapter.AdViews[MrecUnit.Key];
            Assert.AreEqual(MrecPosition.Custom, request.MrecPosition);
            Assert.AreEqual(new MrecPoint(120, 240), request.MrecPixelPosition);
            Assert.AreEqual(MrecSize.Standard, _ads.Mrec.GetSize(ResultMrec));

            var size = new MrecSize(300, 250, 900, 750);
            _adapter.FireMrecSize(MrecUnit, size);
            _h.Main.Drain();
            Assert.AreEqual(size, _ads.Mrec.GetSize(ResultMrec));
            CollectionAssert.AreEqual(new[] { new MrecSizeChanged(ResultMrec, size) }, sizes);

            _ads.Mrec.Destroy(ResultMrec);
            Assert.AreEqual(MrecSize.None, _ads.Mrec.GetSize(ResultMrec));
            Assert.AreEqual(MrecSize.None, sizes[sizes.Count - 1].Size);
        }

        [Test]
        public void Mrec_InvalidCustomPosition_IsRejected()
        {
            CreateReady();
            var result = _ads.Mrec.Show(ResultMrec,
                new MrecOptions(MrecPosition.Custom, new MrecPoint(float.NaN, -1)));

            Assert.IsFalse(result.IsSuccess);
            Assert.AreEqual(SdkErrorCategory.Configuration, result.Error!.Category);
            Assert.AreEqual(0, _adapter.AdViewRequests.Count);
        }

        [Test]
        public void UnsupportedFormat_ReturnsImmediately_WithoutSideEffects()
        {
            _adapter.Capabilities = AdCapability.Interstitial | AdCapability.Rewarded;
            CreateReady(loadAll: false);

            Assert.AreEqual(ShowOutcome.Unsupported, _h.Run(_ads.AppOpen.TryShowAsync(Resume, AppOpenTrigger.ColdStart, CancellationToken.None)).Outcome);
            Assert.AreEqual(SdkErrorCategory.Unavailable, _ads.Show(ResultMrec, new MrecOptions(MrecPosition.Centered)).Error!.Category);
            Assert.AreEqual(0, _adapter.Shows.Count);
            Assert.AreEqual(0, _adapter.AdViewRequests.Count);
        }

        // ---------------- App open ----------------

        [Test]
        public void AppOpen_SuppressedWhileHandleOpen()
        {
            CreateReady();
            var handle = _ads.AppOpen.Suppress("purchase");
            Assert.AreEqual(ShowOutcome.Blocked, _h.Run(_ads.AppOpen.TryShowAsync(Resume, AppOpenTrigger.Resume, CancellationToken.None)).Outcome);
            handle.Dispose();
            Assert.AreEqual(AdAvailability.Ready, _ads.AppOpen.GetAvailability(Resume));
        }

        [Test]
        public void AppOpen_AutoShowOnResume_RespectsMinimumBackground()
        {
            CreateReady();
            _ads.AppOpen.SetAutoShowOnResume(Resume);

            _lifecycle.SetPaused(true);
            _h.Clock.Advance(TimeSpan.FromSeconds(2));
            _lifecycle.SetPaused(false);
            Assert.AreEqual(0, _adapter.Shows.Count, "short background: no app-open");

            _lifecycle.SetPaused(true);
            _h.Clock.Advance(TimeSpan.FromSeconds(10));
            _lifecycle.SetPaused(false);
            Assert.AreEqual(1, _adapter.Shows.Count);
            Assert.AreEqual(AppOpenUnit, _adapter.LastShownUnit);
        }

        [Test]
        public void AppOpen_NotShownOnResumeCausedByAdOrAdClick()
        {
            CreateReady();
            _ads.AppOpen.SetAutoShowOnResume(Resume);

            // Android: ad activity làm Unity pause/resume.
            var task = _ads.ShowAsync(LevelEnd, CancellationToken.None);
            var id = _adapter.LastOperationId;
            _adapter.FireDisplayed(id);
            _h.Main.Drain();
            _lifecycle.SetPaused(true);
            _h.Clock.Advance(TimeSpan.FromSeconds(20));
            _adapter.FireClosed(id);
            _h.Run(task);
            _lifecycle.SetPaused(false);
            Assert.AreEqual(1, _adapter.Shows.Count, "resume caused by the interstitial");

            // Click banner mở browser rồi quay lại.
            _ads.Show(HomeBanner, new BannerOptions(BannerPosition.Bottom));
            _adapter.FireClicked(BannerUnit, Guid.Empty);
            _h.Main.Drain();
            _lifecycle.SetPaused(true);
            _h.Clock.Advance(TimeSpan.FromSeconds(20));
            _lifecycle.SetPaused(false);
            Assert.AreEqual(1, _adapter.Shows.Count, "resume caused by ad click");
        }

        [Test]
        public void ResumeWatchdog_ClosesOperationWhenCloseCallbackMissing()
        {
            CreateReady();
            var task = _ads.ShowAsync(LevelEnd, CancellationToken.None);
            _adapter.FireDisplayed(_adapter.LastOperationId);
            _h.Main.Drain();
            _lifecycle.SetPaused(true);
            _lifecycle.SetPaused(false);
            _h.Scheduler.Advance(TimeSpan.FromSeconds(3));

            Assert.AreEqual(ShowOutcome.Shown, _h.Run(task).Outcome);
        }

        // ---------------- Revenue ----------------

        [Test]
        public void Revenue_TwoBannerImpressions_AreTwoEvents()
        {
            CreateReady();
            _ads.Show(HomeBanner, new BannerOptions(BannerPosition.Bottom));
            _adapter.FirePaid(BannerUnit, Guid.Empty);
            _adapter.FirePaid(BannerUnit, Guid.Empty);
            _h.Main.Drain();

            Assert.AreEqual(2, _revenue.AdRevenue.Count);
            Assert.AreNotEqual(_revenue.AdRevenue[0].ShowOperationId, _revenue.AdRevenue[1].ShowOperationId);
            Assert.AreEqual(HomeBanner.Id, _revenue.AdRevenue[0].PlacementId);
            Assert.AreEqual(AdProviderIds.Max, _revenue.AdRevenue[0].Mediation);
        }

        [Test]
        public void Revenue_DuplicatePaidForSameShowOperation_IsOneEvent()
        {
            CreateReady();
            var impressions = new List<AdImpression>();
            _ads.Impressions.Subscribe(impressions.Add);

            var task = _ads.ShowAsync(LevelEnd, CancellationToken.None);
            var id = _adapter.LastOperationId;
            _adapter.FireDisplayed(id);
            _adapter.FirePaid(InterUnit, id);
            _adapter.FirePaid(InterUnit, id);
            _adapter.FireClosed(id);
            _h.Run(task);
            _h.Main.Drain();

            Assert.AreEqual(1, _revenue.AdRevenue.Count);
            Assert.AreEqual(id, _revenue.AdRevenue[0].ShowOperationId);
            Assert.AreEqual(LevelEnd.Id, _revenue.AdRevenue[0].PlacementId);
            Assert.AreEqual(1, impressions.Count);
        }

        [Test]
        public void Revenue_DuplicateImpressionId_IsDropped()
        {
            CreateReady();
            _ads.Show(HomeBanner, new BannerOptions(BannerPosition.Bottom));
            _adapter.FirePaid(BannerUnit, Guid.Empty, impressionId: "imp-1");
            _adapter.FirePaid(BannerUnit, Guid.Empty, impressionId: "imp-1");
            _h.Main.Drain();
            Assert.AreEqual(1, _revenue.AdRevenue.Count);
        }

        // ---------------- Misc ----------------

        [Test]
        public void AvailabilityChanged_RaisedWhenRewardedBecomesReady()
        {
            CreateReady(loadAll: false);
            var changes = new List<AdAvailabilityChanged>();
            _ads.AvailabilityChanged.Subscribe(changes.Add);
            _ads.GetAvailability(Revive);

            _adapter.FireLoaded(RewardedUnit);
            _h.Main.Drain();

            Assert.IsTrue(changes.Exists(c => c.Placement == Revive && c.Availability == AdAvailability.Ready));
        }

        [Test]
        public void Dispose_CompletesPendingShowAsCancelled_AndDisposesAdapter()
        {
            CreateReady();
            var task = _ads.ShowAsync(LevelEnd, CancellationToken.None);
            _ads.Dispose();

            Assert.AreEqual(ShowOutcome.Cancelled, _h.Run(task).Outcome);
            Assert.IsTrue(_adapter.Disposed);
            Assert.AreEqual(AdsModuleState.Disposed, _ads.State.Value);
        }

        [Test]
        public void OptionsWithUnknownAdUnit_FailValidation()
        {
            var options = new AdsOptions(new[] { InterUnit },
                new[] { new AdPlacementBinding("x", "missing") });
            var ads = Create(options);
            var result = _h.Run(ads.InitializeAsync(CancellationToken.None));
            Assert.AreEqual(SdkErrorCategory.Configuration, result.Error!.Category);
            Assert.AreEqual(AdsModuleState.Failed, ads.State.Value);
            Assert.AreEqual(0, _adapter.InitializeCount);
        }

        sealed class RecordingPresentationHandler : IAdPresentationHandler
        {
            public readonly List<string> Calls = new List<string>();
            public void OnFullScreenShowing(AdFormat format) => Calls.Add("showing " + format);
            public void OnFullScreenClosed(AdFormat format) => Calls.Add("closed " + format);
        }
    }
}
