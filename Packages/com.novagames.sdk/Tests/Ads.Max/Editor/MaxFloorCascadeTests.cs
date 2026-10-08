#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using NovaGames.Mobile.RemoteConfig;
using NovaGames.Mobile.Testing;
using NUnit.Framework;

namespace NovaGames.Mobile.Ads.Max.Tests
{
    // Kịch bản bid floor test trên MaxFloorCascade / MaxFloorPlan (không cần MaxSdk).
    public sealed class MaxFloorCascadeTests
    {
        static readonly AdUnit Main = new AdUnit("inter", AdFormat.Interstitial, "main-id", AdsProvider.Max);
        static readonly AdUnit High = Main with { AdUnitId = "high-id" };
        static readonly AdUnit Medium = Main with { AdUnitId = "medium-id" };
        static readonly AdUnit RvMain = new AdUnit("rewarded", AdFormat.Rewarded, "rv-id", AdsProvider.Max);
        static readonly AdUnit Banner = new AdUnit("banner", AdFormat.Banner, "banner-id", AdsProvider.Max);
        static readonly TimeSpan FloorTimeout = TimeSpan.FromSeconds(15);

        TestHarness _h = null!;
        FakeMaxApi _api = null!;
        MaxFloorCascade _cascade = null!;
        List<string> _results = null!;

        [SetUp]
        public void SetUp()
        {
            _h = new TestHarness();
            _api = new FakeMaxApi();
            _results = new List<string>();
            _cascade = new MaxFloorCascade(new[] { High, Medium, Main }, _api, _h.Clock, _h.Scheduler, _h.Log, FloorTimeout,
                unit => _results.Add("loaded " + unit.AdUnitId),
                (unit, error) => _results.Add("failed " + unit.AdUnitId + " " + error.Kind));
        }

        [TearDown]
        public void TearDown()
        {
            _cascade.Dispose();
            _h.Dispose();
        }

        void Fill(AdUnit tier)
        {
            _api.Ready.Add(tier.AdUnitId);
            _cascade.OnTierLoaded(tier.AdUnitId);
        }

        void Fail(AdUnit tier, AdLoadFailure kind = AdLoadFailure.NoFill) =>
            _cascade.OnTierLoadFailed(tier.AdUnitId, new AdLoadError(kind, "fake " + kind));

        void Advance(TimeSpan by)
        {
            _h.Scheduler.Advance(by);
            _h.Main.Drain();
        }

        // ---------------- Load tuần tự ----------------

        [Test]
        public void HighFills_ReportsMainUnitOnce_OnlyHighRequested()
        {
            _cascade.Load();
            Fill(High);

            CollectionAssert.AreEqual(new[] { "high-id" }, _api.Loads);
            CollectionAssert.AreEqual(new[] { "loaded main-id" }, _results, "AdsManager only ever sees the main unit");
            Assert.IsTrue(_cascade.IsReady);
        }

        [Test]
        public void HighFails_LoadsMedium_ThenMain_NeverInParallel()
        {
            _cascade.Load();
            _cascade.Load();
            CollectionAssert.AreEqual(new[] { "high-id" }, _api.Loads, "second Load while loading is a no-op");

            Fail(High);
            CollectionAssert.AreEqual(new[] { "high-id", "medium-id" }, _api.Loads);
            Fail(Medium, AdLoadFailure.Network);
            CollectionAssert.AreEqual(new[] { "high-id", "medium-id", "main-id" }, _api.Loads);
            Fill(Main);

            CollectionAssert.AreEqual(new[] { "loaded main-id" }, _results);
        }

        [Test]
        public void WholeRoundFails_ReportsOneFailure_NextLoadStartsFromHigh()
        {
            _cascade.Load();
            Fail(High);
            Fail(Medium);
            Fail(Main, AdLoadFailure.Network);

            CollectionAssert.AreEqual(new[] { "failed main-id Network" }, _results, "intermediate tiers are not reported");
            _cascade.Load();
            Assert.AreEqual("high-id", _api.Loads.Last());
        }

        [Test]
        public void FloorTierTimeout_MovesOn_MainHasNoOwnTimeout()
        {
            _cascade.Load();
            Advance(FloorTimeout);
            Assert.AreEqual("medium-id", _api.Loads.Last());
            Advance(FloorTimeout);
            Assert.AreEqual("main-id", _api.Loads.Last());

            Advance(TimeSpan.FromMinutes(5));
            CollectionAssert.IsEmpty(_results, "main waits for MAX; AdsManager's LoadTimeout covers the round");
            Assert.AreEqual(0, _h.Scheduler.PendingCount);
        }

        [Test]
        public void CoreTimeoutDiscard_ResetsMainSoNextRoundStartsFromHigh()
        {
            _cascade.Load();
            Advance(FloorTimeout);
            Advance(FloorTimeout);
            Assert.AreEqual("main-id", _api.Loads.Last());

            // AdUnitSlot gọi adapter.Discard khi hết load timeout tổng.
            _cascade.Discard();
            _cascade.Load();

            Assert.AreEqual("high-id", _api.Loads.Last());
            Assert.AreEqual(4, _api.Loads.Count);
        }

        [Test]
        public void LateFillOfHigherTier_WinsWhileLowerTierLoads()
        {
            _cascade.Load();
            Advance(FloorTimeout);
            Fail(Medium);
            Assert.AreEqual("main-id", _api.Loads.Last());

            Fill(High);
            CollectionAssert.AreEqual(new[] { "loaded main-id" }, _results);

            _api.Ready.Add(Main.AdUnitId);
            _cascade.OnTierLoaded(Main.AdUnitId);
            Assert.AreEqual(1, _results.Count, "main's later fill is not reported twice");
            Assert.AreEqual(High, _cascade.BeginShow(), "the most expensive ready tier is shown");
        }

        [Test]
        public void AdAlreadyHeldByMax_IsReportedWithoutNewRequest()
        {
            _api.Ready.Add(Medium.AdUnitId);

            _cascade.Load();

            CollectionAssert.IsEmpty(_api.Loads);
            CollectionAssert.AreEqual(new[] { "loaded main-id" }, _results);
            Assert.AreEqual(Medium, _cascade.BeginShow());
        }

        [Test]
        public void LeftoverLowerTierFill_IsUsedWhenItsTurnComes()
        {
            _api.Ready.Add(Main.AdUnitId);
            _cascade.OnTierLoaded(Main.AdUnitId);
            CollectionAssert.IsEmpty(_results, "fill while idle is not reported");

            _api.Ready.Remove(Main.AdUnitId);
            _cascade.Load();
            _api.Ready.Add(Main.AdUnitId);
            _cascade.OnTierLoaded(Main.AdUnitId);
            CollectionAssert.IsEmpty(_results, "lower tier fill does not cut HIGH short");

            Fail(High);
            Fail(Medium);
            CollectionAssert.AreEqual(new[] { "loaded main-id" }, _results);
            CollectionAssert.AreEqual(new[] { "high-id", "medium-id" }, _api.Loads, "main is not requested again");
        }

        [Test]
        public void ThrowingLoad_CountsAsTierFailure()
        {
            _api.ThrowOn = High.AdUnitId;
            _cascade.Load();
            Assert.AreEqual("medium-id", _api.Loads.Last());
        }

        // ---------------- Show ----------------

        [Test]
        public void AfterShowOfMedium_NextLoadStartsFromHigh()
        {
            _cascade.Load();
            Fail(High);
            Fill(Medium);

            var shown = _cascade.BeginShow();
            Assert.AreEqual(Medium, shown);
            _api.Ready.Remove(Medium.AdUnitId);
            _cascade.EndShow();
            _cascade.Load();

            Assert.AreEqual("high-id", _api.Loads.Last());
        }

        [Test]
        public void LoadBeforeHiddenCallback_EndsShowAndStartsFromHigh()
        {
            _cascade.Load();
            Fill(High);
            _cascade.BeginShow();
            _api.Ready.Remove(High.AdUnitId);

            _cascade.Load();

            Assert.IsTrue(_cascade.IsLoading);
            Assert.AreEqual(0, _cascade.CurrentTier);
        }

        // ---------------- Discard ----------------

        [Test]
        public void DiscardWhileLoading_StopsRound_LateFillKeptForNextLoad()
        {
            _cascade.Load();
            Fail(High);
            _cascade.Discard();
            Assert.AreEqual(0, _h.Scheduler.PendingCount);

            Fill(Medium);
            CollectionAssert.IsEmpty(_results, "fill after discard is not reported");

            _cascade.Load();
            CollectionAssert.AreEqual(new[] { "loaded main-id" }, _results);
            Assert.AreEqual(2, _api.Loads.Count, "no new request: MAX already holds MEDIUM");
        }

        // ---------------- Plan (init) ----------------

        static RemoteConfigSnapshot Config(bool inter, bool rewarded) =>
            new RemoteConfigSnapshot(1, ConfigSource.Remote, DateTime.UtcNow, new Dictionary<string, string>
            {
                ["ad_inter_floor_enabled"] = inter ? "true" : "false",
                ["ad_rewarded_floor_enabled"] = rewarded ? "true" : "false",
            });

        static MaxAdsSettings Settings() => new MaxAdsSettings
        {
            FloorCascades = new[]
            {
                new MaxFloorCascadeSettings("main-id", new[] { "high-id", "medium-id" }),
                new MaxFloorCascadeSettings("rv-id", new[] { "rv-high-id" }),
            },
        };

        [Test]
        public void Plan_FlagOn_ListsTiersForB2BAutoRetryAndInit()
        {
            var plan = MaxFloorPlan.Create(new[] { Main, RvMain, Banner }, Settings(), Config(inter: true, rewarded: false), _h.Log);

            Assert.AreEqual(1, plan.Cascades.Count);
            CollectionAssert.AreEqual(new[] { "high-id", "medium-id", "main-id" }, plan.Cascades[0].Tiers.Select(t => t.AdUnitId));
            Assert.IsTrue(plan.Cascades[0].Tiers.All(t => t.Key == "inter" && t.Format == AdFormat.Interstitial));
            Assert.AreEqual("high-id,medium-id,main-id", plan.DisableBackToBackIds);
            CollectionAssert.AreEqual(new[] { "high-id", "medium-id", "main-id" }, plan.DisableAutoRetryUnits.Select(u => u.AdUnitId));
            CollectionAssert.AreEquivalent(new[] { "main-id", "rv-id", "banner-id", "high-id", "medium-id" }, plan.InitAdUnitIds,
                "floor units of a disabled cascade stay out of selective init");
        }

        [Test]
        public void Plan_FlagOffOrNoSettings_BehavesLikePlainMax()
        {
            var off = MaxFloorPlan.Create(new[] { Main, RvMain }, Settings(), Config(false, false), _h.Log);
            Assert.AreEqual(0, off.Cascades.Count);
            Assert.AreEqual("", off.DisableBackToBackIds);
            CollectionAssert.IsEmpty(off.DisableAutoRetryUnits);
            CollectionAssert.AreEqual(new[] { "main-id", "rv-id" }, off.InitAdUnitIds);

            var none = MaxFloorPlan.Create(new[] { Main }, null, Config(true, true), _h.Log);
            Assert.AreEqual(0, none.Cascades.Count);
        }

        [Test]
        public void Plan_SkipsUnknownMainAndReusedIds()
        {
            var settings = new MaxAdsSettings
            {
                FloorCascades = new[]
                {
                    new MaxFloorCascadeSettings("missing-id", new[] { "x-id" }),
                    new MaxFloorCascadeSettings("banner-id", new[] { "y-id" }),
                    new MaxFloorCascadeSettings("main-id", new[] { "rv-id", "main-id" }),
                },
            };
            var plan = MaxFloorPlan.Create(new[] { Main, RvMain, Banner }, settings, Config(true, true), _h.Log);

            Assert.AreEqual(0, plan.Cascades.Count);
            Assert.AreEqual(4, _h.Log.Count(Infrastructure.SdkLogLevel.Warning));
        }

        // ---------------- Asset (chỉ chạy trong Unity) ----------------

        [Test]
        public void MaxConfigAsset_ProviderIsMax_NoBuiltInTestIds_SettingsAlwaysAttached()
        {
            var config = UnityEngine.ScriptableObject.CreateInstance<MaxAdsConfig>();
            try
            {
                var options = config.ToOptions(new AdPlacement[] { new InterstitialPlacement("level_end") }, isDevelopment: true, isIos: false);
                Assert.AreEqual(AdsProvider.Max, options.Providers.Single().Provider);
                Assert.AreEqual(0, options.AdUnits.Count, "MAX has no Google-style test IDs");
                var settings = (MaxAdsSettings)options.Providers.Single().Settings!;
                CollectionAssert.IsEmpty(settings.FloorCascades);
                Assert.AreEqual(FloorTimeout, settings.FloorTierLoadTimeout);
                CollectionAssert.IsNotEmpty(config.Validate(isDevelopment: true, isIos: false));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(config);
            }
        }

        sealed class FakeMaxApi : IMaxFullScreenApi
        {
            public readonly List<string> Loads = new List<string>();
            public readonly HashSet<string> Ready = new HashSet<string>(StringComparer.Ordinal);
            public string? ThrowOn;

            public void Load(AdUnit unit)
            {
                if (unit.AdUnitId == ThrowOn) throw new InvalidOperationException("boom");
                Loads.Add(unit.AdUnitId);
            }

            public bool IsReady(AdUnit unit) => Ready.Contains(unit.AdUnitId);
        }
    }
}
