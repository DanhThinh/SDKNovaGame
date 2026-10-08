#nullable enable
using System;
using System.Threading;
using NovaGames.Mobile.Ads;
using NovaGames.Mobile.Infrastructure;
using NovaGames.Mobile.Privacy;
using NovaGames.Mobile.Testing;
using NUnit.Framework;

namespace NovaGames.Mobile.Tests
{
    public sealed class NovaAdsTests
    {
        static readonly AdUnit InterUnit = new AdUnit("inter", AdFormat.Interstitial, "inter-id", AdsProvider.Max);
        static readonly AdUnit RewardedUnit = new AdUnit("rewarded", AdFormat.Rewarded, "rewarded-id", AdsProvider.Max);
        static readonly AdUnit BannerUnit = new AdUnit("banner", AdFormat.Banner, "banner-id", AdsProvider.Max);
        static readonly AdUnit MrecUnit = new AdUnit("mrec", AdFormat.MRec, "mrec-id", AdsProvider.Max);

        TestHarness _h = null!;
        FakeAdsAdapter _adapter = null!;
        AdsManager _ads = null!;

        [SetUp]
        public void SetUp()
        {
            // Editor không reload domain sau Play Mode: NovaAds có thể còn giữ AdsManager của lần chơi trước.
            NovaAds.Reset();
            _h = new TestHarness();
            _adapter = new FakeAdsAdapter();
            // Không khai báo placement nào: NovaAds dùng tên string, AdsManager rơi về unit của format.
            var options = new AdsOptions(new[] { InterUnit, RewardedUnit, BannerUnit, MrecUnit },
                Array.Empty<AdPlacementBinding>());
            _ads = new AdsManager(_adapter, options, _h.Context, new AdsDependencies(
                new SdkProperty<ConsentSnapshot>(TestConsent.Granted)) { Random = () => 0.5 });
        }

        [TearDown]
        public void TearDown()
        {
            NovaAds.Unbind(_ads);
            _ads.Dispose();
            _h.Dispose();
        }

        void BindReady()
        {
            Assert.IsTrue(_h.Run(_ads.InitializeAsync(CancellationToken.None)).IsSuccess);
            NovaAds.Bind(_ads, _h.Log);
            NovaAds.SetLevel(10);
            _adapter.FireLoaded(InterUnit);
            _adapter.FireLoaded(RewardedUnit);
            _h.Main.Drain();
        }

        [Test]
        public void NotBound_InterstitialContinuesGame_RewardedFails()
        {
            bool done = false, rewarded = false, failed = false;
            NovaAds.ShowInterstitial("level_end", () => done = true);
            NovaAds.ShowRewarded("revive", () => rewarded = true, () => failed = true);

            Assert.IsTrue(done);
            Assert.IsFalse(rewarded);
            Assert.IsTrue(failed);
            Assert.IsFalse(NovaAds.IsRewardReady("revive"));
        }

        [Test]
        public void Rewarded_StringPlacement_UsesRewardedUnit_AndGrants()
        {
            BindReady();
            Assert.IsTrue(NovaAds.IsRewardReady("revive"));

            bool rewarded = false, failed = false;
            NovaAds.ShowRewarded("revive", () => rewarded = true, () => failed = true);
            var id = _adapter.LastOperationId;
            Assert.AreEqual(RewardedUnit, _adapter.LastShownUnit);
            Assert.AreEqual("revive", _adapter.Shows[0].PlacementId);

            _adapter.FireDisplayed(id);
            _adapter.FireRewarded(id);
            _adapter.FireClosed(id);
            _h.Main.Drain();

            Assert.IsTrue(rewarded);
            Assert.IsFalse(failed);
        }

        [Test]
        public void Interstitial_Capped_CallsOnDoneWithoutShowing()
        {
            BindReady();
            NovaAds.SetLevel(0);
            Assert.IsFalse(NovaAds.IsInterReady("level_end"));

            bool done = false;
            NovaAds.ShowInterstitial("level_end", () => done = true);
            _h.Main.Drain();

            Assert.IsTrue(done);
            Assert.AreEqual(0, _adapter.Shows.Count);
        }

        [Test]
        public void Interstitial_Shown_CallsOnDoneAfterClose()
        {
            BindReady();
            bool done = false;
            NovaAds.ShowInterstitial("level_end", () => done = true);
            var id = _adapter.LastOperationId;
            _adapter.FireDisplayed(id);
            _h.Main.Drain();
            Assert.IsFalse(done, "game waits until the ad closes");

            _adapter.FireClosed(id);
            _h.Main.Drain();
            Assert.IsTrue(done);
        }

        [Test]
        public void BannerRequestedBeforeBind_IsShownOnBind()
        {
            NovaAds.ShowBanner();
            BindReady();
            Assert.IsTrue(_adapter.AdViews.ContainsKey(BannerUnit.Key));
        }

        [Test]
        public void HideMRecsTemporarily_HidesAndRestoresAtTheSamePosition()
        {
            BindReady();
            NovaAds.ShowMRec(MrecPosition.BottomCenter, "result");
            _h.Main.Drain();
            Assert.AreEqual(1, _adapter.AdViewRequests.Count);

            var handle = NovaAds.HideMRecsTemporarily();
            CollectionAssert.Contains(_adapter.Calls, "HideAdView mrec");

            handle.Dispose();
            _h.Main.Drain();
            Assert.AreEqual(2, _adapter.AdViewRequests.Count, "shown again");
            Assert.AreEqual(MrecPosition.BottomCenter, _adapter.AdViewRequests[1].MrecPosition);
            handle.Dispose();
            Assert.AreEqual(2, _adapter.AdViewRequests.Count, "dispose is idempotent");
        }

        [Test]
        public void HideMRecsTemporarily_DoesNotRestoreAnMrecTheGameHid()
        {
            BindReady();
            NovaAds.ShowMRec(MrecPosition.Centered, "result");
            _h.Main.Drain();

            var handle = NovaAds.HideMRecsTemporarily();
            NovaAds.HideMRec("result");
            handle.Dispose();
            _h.Main.Drain();

            Assert.AreEqual(1, _adapter.AdViewRequests.Count);
        }

        [Test]
        public void HideMRecsTemporarily_WithoutMrec_IsANoOp()
        {
            BindReady();

            using (NovaAds.HideMRecsTemporarily()) { }

            CollectionAssert.DoesNotContain(_adapter.Calls, "HideAdView mrec");
        }

        [Test]
        public void ThrowingGameCallback_IsLogged_NotPropagated()
        {
            BindReady();
            Assert.DoesNotThrow(() => NovaAds.ShowRewarded("revive", () => { }, () => throw new InvalidOperationException("game bug")));
            var id = _adapter.LastOperationId;
            _adapter.FireDisplayFailed(id);
            _h.Main.Drain();

            Assert.IsTrue(_h.Log.Entries.Exists(e => e.Level == SdkLogLevel.Error && e.Message.Contains("onFailed")));
        }
    }
}
