#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.Testing;
using NUnit.Framework;

namespace NovaGames.Mobile.Tests
{
    // Một build dùng cả MAX và AdMob: mỗi format một provider, AdsManager đi qua RoutingAdsAdapter.
    public sealed class AdsMediationTests
    {
        const AdCapability AllFormats =
            AdCapability.Banner | AdCapability.MRec | AdCapability.Interstitial | AdCapability.Rewarded | AdCapability.AppOpen;

        static readonly AdUnit InterUnit = new AdUnit("inter", AdFormat.Interstitial, "max-inter", AdsProvider.Max);
        static readonly AdUnit RewardedUnit = new AdUnit("rewarded", AdFormat.Rewarded, "max-rv", AdsProvider.Max);
        static readonly AdUnit BannerUnit = new AdUnit("banner", AdFormat.Banner, "ca-app-pub-1/banner", AdsProvider.AdMob);

        static readonly InterstitialPlacement LevelEnd = new InterstitialPlacement("level_end");
        static readonly BannerPlacement HomeBanner = new BannerPlacement("home_banner");

        TestHarness _h = null!;
        FakeAdsAdapter _max = null!;
        FakeAdsAdapter _admob = null!;
        RecordingRevenuePipeline _revenue = null!;
        AdsManager? _ads;

        [SetUp]
        public void SetUp()
        {
            _h = new TestHarness();
            _max = new FakeAdsAdapter(AdProviderIds.Max, AllFormats) { ManagesFullScreenExpiry = true };
            _admob = new FakeAdsAdapter(AdProviderIds.AdMob, AllFormats | AdCapability.CollapsibleBanner);
            _revenue = new RecordingRevenuePipeline();
        }

        [TearDown]
        public void TearDown()
        {
            _ads?.Dispose();
            _h.Dispose();
        }

        static AdsOptions Options() => new AdsOptions(new[] { InterUnit, RewardedUnit, BannerUnit }, Array.Empty<AdPlacementBinding>())
        {
            Providers = new[]
            {
                new AdsProviderOptions(AdsProvider.Max) { TestDeviceIds = new[] { "gaid" } },
                new AdsProviderOptions(AdsProvider.AdMob) { TestDeviceIds = new[] { "hashed" } },
            },
        };

        AdsManager CreateReady(IReadOnlyDictionary<AdsProvider, IAdsAdapter> adapters)
        {
            var options = Options();
            _ads = new AdsManager(new RoutingAdsAdapter(adapters, options.AdUnits), options, _h.Context,
                new AdsDependencies(new SdkProperty<ConsentSnapshot>(TestConsent.Granted)) { Revenue = _revenue, Random = () => 0.5 });
            var init = _h.Run(_ads.InitializeAsync(CancellationToken.None));
            Assert.IsTrue(init.IsSuccess, init.ToString());
            _ads.SetPlayerLevel(10);
            return _ads;
        }

        Dictionary<AdsProvider, IAdsAdapter> Both() =>
            new Dictionary<AdsProvider, IAdsAdapter> { [AdsProvider.Max] = _max, [AdsProvider.AdMob] = _admob };

        [Test]
        public void EachFormatGoesToItsProvider_BothInitializedWithAllProviderOptions()
        {
            var ads = CreateReady(Both());

            Assert.AreEqual(1, _max.InitializeCount);
            Assert.AreEqual(1, _admob.InitializeCount);
            CollectionAssert.AreEqual(new[] { "gaid" }, _max.LastInitOptions!.For(AdsProvider.Max).TestDeviceIds);
            CollectionAssert.AreEqual(new[] { BannerUnit }, _admob.LastInitOptions!.UnitsOf(AdsProvider.AdMob));

            Assert.AreEqual(1, _max.LoadCount(InterUnit.Key), "full-screen preloads on MAX");
            Assert.AreEqual(0, _admob.Loads.Count);

            Assert.IsTrue(ads.Banners.Show(HomeBanner, new BannerOptions(BannerPosition.Bottom)).IsSuccess);
            Assert.IsTrue(_admob.AdViews.ContainsKey(BannerUnit.Key));
            Assert.IsFalse(_max.AdViews.ContainsKey(BannerUnit.Key));
        }

        [Test]
        public void CollapsibleBanner_FollowsBannerProvider()
        {
            var ads = CreateReady(Both());
            Assert.IsTrue(ads.Supports(AdCapability.CollapsibleBanner), "banner is served by AdMob");

            Assert.IsTrue(ads.Banners.Show(HomeBanner,
                new BannerOptions(BannerPosition.Bottom, BannerSize.AdaptiveAnchored, CollapsiblePolicy.Required)).IsSuccess);
            Assert.AreEqual(CollapseDirection.Bottom, _admob.AdViews[BannerUnit.Key].Collapse);
        }

        [Test]
        public void Revenue_ReportsMediationOfTheUnit()
        {
            var ads = CreateReady(Both());
            _max.FireLoaded(InterUnit);
            _h.Main.Drain();
            var show = ads.FullScreen.ShowAsync(LevelEnd, CancellationToken.None);
            var id = _max.LastOperationId;
            _max.FireDisplayed(id);
            _max.FirePaid(InterUnit, id);
            _max.FireClosed(id);
            _h.Run(show);

            ads.Banners.Show(HomeBanner, new BannerOptions(BannerPosition.Bottom));
            _admob.FirePaid(BannerUnit, Guid.Empty);
            _h.Main.Drain();

            Assert.AreEqual(2, _revenue.AdRevenue.Count);
            Assert.AreEqual(AdProviderIds.Max, _revenue.AdRevenue[0].Mediation);
            Assert.AreEqual(AdProviderIds.AdMob, _revenue.AdRevenue[1].Mediation);
        }

        [Test]
        public void TtlFollowsProvider_MaxSelfManaged_AdMobExpires()
        {
            var admobInter = InterUnit with { Provider = AdsProvider.AdMob, AdUnitId = "ca-app-pub-1/inter" };
            var options = new AdsOptions(new[] { admobInter, RewardedUnit }, Array.Empty<AdPlacementBinding>());
            _ads = new AdsManager(new RoutingAdsAdapter(Both(), options.AdUnits), options, _h.Context,
                new AdsDependencies(new SdkProperty<ConsentSnapshot>(TestConsent.Granted)) { Random = () => 0.5 });
            _h.Run(_ads.InitializeAsync(CancellationToken.None));
            _admob.FireLoaded(admobInter);
            _max.FireLoaded(RewardedUnit);
            _h.Main.Drain();

            _h.Scheduler.Advance(TimeSpan.FromHours(2));
            _h.Main.Drain();

            Assert.Contains(admobInter, _admob.Discards, "AdMob interstitial expires after 1 h");
            Assert.IsEmpty(_max.Discards, "MAX reloads expired ads itself");
        }

        [Test]
        public void ProviderNotInstalled_ItsFormatsUnsupported_OthersWork()
        {
            var ads = CreateReady(new Dictionary<AdsProvider, IAdsAdapter> { [AdsProvider.Max] = _max });

            Assert.IsFalse(ads.Supports(AdCapability.Banner));
            Assert.IsFalse(ads.Banners.Show(HomeBanner, new BannerOptions(BannerPosition.Bottom)).IsSuccess);
            _max.FireLoaded(InterUnit);
            _h.Main.Drain();
            Assert.AreEqual(AdAvailability.Ready, ads.FullScreen.GetAvailability(LevelEnd));
        }

        [Test]
        public void OneProviderInitFailsRetryably_AdsRetriesUntilBothReady()
        {
            _admob.AutoInitialize = false;
            var options = Options();
            _ads = new AdsManager(new RoutingAdsAdapter(Both(), options.AdUnits), options, _h.Context,
                new AdsDependencies(new SdkProperty<ConsentSnapshot>(TestConsent.Granted)) { Random = () => 0.5 });
            var init = _ads.InitializeAsync(CancellationToken.None);
            _admob.CompleteInit(new SdkError("admob", SdkErrorCategory.Network, "offline", true));
            Assert.IsFalse(_h.Run(init).IsSuccess);
            Assert.AreEqual(AdsModuleState.Initializing, _ads.State.Value);

            _h.Scheduler.Advance(TimeSpan.FromSeconds(2));
            _h.Main.Drain();
            _admob.CompleteInit(SdkResult.Ok);
            _h.Main.Drain();

            Assert.AreEqual(AdsModuleState.Ready, _ads.State.Value);
            Assert.AreEqual(2, _max.InitializeCount, "MAX init is idempotent across retries");
        }

        [Test]
        public void ConfigBuild_MixedProviders_UnitsCarryTheirProvider()
        {
            var options = AdsConfigOptions.Build(new Dictionary<AdFormat, (AdsProvider, string)>
            {
                [AdFormat.Interstitial] = (AdsProvider.Max, "max-inter"),
                [AdFormat.Banner] = (AdsProvider.AdMob, "ca-app-pub-1/banner"),
                [AdFormat.MRec] = (AdsProvider.None, "ignored"),
                [AdFormat.Rewarded] = (AdsProvider.Max, ""),
            }, new AdPlacement[] { LevelEnd, HomeBanner, new RewardedPlacement("revive") });

            Assert.AreEqual(2, options.AdUnits.Count);
            Assert.AreEqual(AdsProvider.Max, options.AdUnits[0].Provider);
            Assert.AreEqual(AdsProvider.AdMob, options.AdUnits[1].Provider);
            CollectionAssert.AreEqual(new[] { AdsProvider.Max, AdsProvider.AdMob }, options.UsedProviders);
            Assert.AreEqual(2, options.Placements.Count, "revive has no rewarded unit");
            CollectionAssert.IsEmpty(options.Validate());
        }
    }
}
