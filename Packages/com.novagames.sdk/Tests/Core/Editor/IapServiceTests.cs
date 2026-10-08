#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Entitlements;
using NovaGames.Mobile.Iap;
using NovaGames.Mobile.Testing;
using NUnit.Framework;

namespace NovaGames.Mobile.Tests
{
    public sealed class IapServiceTests
    {
        static readonly EntitlementId VipEntitlement = new EntitlementId("vip");
        static readonly IapProductDefinition Gems = new IapProductDefinition("gems", "store.gems", IapProductType.Consumable);
        static readonly IapProductDefinition RemoveAds = new IapProductDefinition("remove_ads", "store.remove_ads", IapProductType.NonConsumable)
        {
            Entitlements = new[] { EntitlementId.RemoveAds },
        };
        static readonly IapProductDefinition Vip = new IapProductDefinition("vip", "store.vip", IapProductType.Subscription)
        {
            Entitlements = new[] { VipEntitlement },
        };

        TestHarness _h = null!;
        FakeStoreAdapter _store = null!;
        RecordingRevenuePipeline _revenue = null!;
        CountingSuppression _suppression = null!;
        readonly List<IapService> _services = new List<IapService>();

        [SetUp]
        public void SetUp()
        {
            _h = new TestHarness();
            _store = new FakeStoreAdapter();
            _revenue = new RecordingRevenuePipeline();
            _suppression = new CountingSuppression();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var service in _services) service.Dispose();
            _services.Clear();
            _h.Dispose();
        }

        IapService Create(IStoreAdapter? store = null, IapOptions? options = null)
        {
            var service = new IapService(store ?? _store, options ?? new IapOptions(new[] { Gems, RemoveAds, Vip }), _h.Context,
                new IapDependencies { Revenue = _revenue, SuppressAppOpen = _suppression.Suppress });
            _services.Add(service);
            return service;
        }

        IapService CreateReady()
        {
            var service = Create();
            Assert.IsTrue(_h.Run(service.InitializeAsync(CancellationToken.None)).IsSuccess);
            Assert.AreEqual(IapState.Ready, service.State.Value);
            return service;
        }

        PurchaseResult Buy(IapService service, string productId, string transactionId, string storeId)
        {
            var task = service.PurchaseAsync(productId, CancellationToken.None);
            _h.Main.Drain();
            _store.Deliver(transactionId, storeId);
            return _h.Run(task);
        }

        [Test]
        public void NonConsumable_IsGrantedConfirmedAndReportedOnce()
        {
            var iap = CreateReady();
            var changed = new List<EntitlementId>();
            var granted = new List<IapGrant>();
            iap.Changed.Subscribe(changed.Add);
            iap.Purchased.Subscribe(granted.Add);

            var task = iap.PurchaseAsync("remove_ads", CancellationToken.None);
            _h.Main.Drain();
            CollectionAssert.AreEqual(new[] { "store.remove_ads" }, _store.Purchases);
            Assert.AreEqual(1, _suppression.Active, "app open is suppressed while the store dialog is open");

            _store.Deliver("tx1", "store.remove_ads");
            var result = _h.Run(task);

            Assert.AreEqual(PurchaseStatus.Completed, result.Status);
            Assert.AreEqual("tx1", result.TransactionId);
            CollectionAssert.AreEqual(new[] { "tx1" }, _store.Confirmed);
            Assert.IsTrue(iap.IsOwned("remove_ads"));
            Assert.IsTrue(iap.IsActive(EntitlementId.RemoveAds));
            CollectionAssert.AreEqual(new[] { EntitlementId.RemoveAds }, changed);
            Assert.AreEqual(1, granted.Count);
            Assert.AreEqual(1, _revenue.Purchases.Count);
            Assert.AreEqual("tx1", _revenue.Purchases[0].TransactionId);
            Assert.AreEqual(1.99, _revenue.Purchases[0].Value, 1e-9);
            Assert.AreEqual("USD", _revenue.Purchases[0].Currency);
            Assert.AreEqual(0, _suppression.Active);

            // Store gửi lại cùng giao dịch: chỉ confirm, không trao/gửi revenue lần nữa.
            _store.Deliver("tx1", "store.remove_ads");
            _h.Main.Drain();
            Assert.AreEqual(1, granted.Count);
            Assert.AreEqual(1, _revenue.Purchases.Count);
            Assert.AreEqual(2, _store.Confirmed.Count);
        }

        [Test]
        public void OwnedNonConsumable_ReturnsAlreadyOwnedWithoutOpeningTheStore()
        {
            var iap = CreateReady();
            Buy(iap, "remove_ads", "tx1", "store.remove_ads");

            var again = _h.Run(iap.PurchaseAsync("remove_ads", CancellationToken.None));

            Assert.AreEqual(PurchaseStatus.AlreadyOwned, again.Status);
            Assert.AreEqual(1, _store.Purchases.Count);
        }

        [Test]
        public void Consumable_HandlerRunsOncePerTransaction()
        {
            var iap = CreateReady();
            var grants = new List<IapGrant>();
            iap.SetConsumableHandler(g => { grants.Add(g); return true; });

            var result = Buy(iap, "gems", "tx1", "store.gems");
            _store.Deliver("tx1", "store.gems");
            _h.Main.Drain();
            var second = Buy(iap, "gems", "tx2", "store.gems");

            Assert.IsTrue(result.IsSuccess);
            Assert.IsTrue(second.IsSuccess);
            Assert.AreEqual(2, grants.Count);
            Assert.AreEqual("tx1", grants[0].TransactionId);
            Assert.AreEqual("tx2", grants[1].TransactionId);
            Assert.IsFalse(iap.IsOwned("gems"), "consumables are never owned");
            Assert.AreEqual(2, _revenue.Purchases.Count);
        }

        [Test]
        public void Consumable_WithoutHandler_StaysUnconfirmedUntilTheHandlerGrantsIt()
        {
            var iap = CreateReady();

            var result = Buy(iap, "gems", "tx1", "store.gems");

            Assert.AreEqual(PurchaseStatus.Pending, result.Status);
            CollectionAssert.IsEmpty(_store.Confirmed);
            CollectionAssert.IsEmpty(_revenue.Purchases);

            iap.SetConsumableHandler(_ => false);
            CollectionAssert.IsEmpty(_store.Confirmed, "handler returned false: still kept");

            var grants = new List<IapGrant>();
            iap.SetConsumableHandler(g => { grants.Add(g); return true; });

            Assert.AreEqual(1, grants.Count);
            CollectionAssert.AreEqual(new[] { "tx1" }, _store.Confirmed);
            Assert.AreEqual(1, _revenue.Purchases.Count);
        }

        [Test]
        public void Consumable_HandlerThrowing_IsKeptAndRetried()
        {
            var iap = CreateReady();
            iap.SetConsumableHandler(_ => throw new InvalidOperationException("save failed"));

            var result = Buy(iap, "gems", "tx1", "store.gems");

            Assert.AreEqual(PurchaseStatus.Pending, result.Status);
            CollectionAssert.IsEmpty(_store.Confirmed);

            iap.SetConsumableHandler(_ => true);
            CollectionAssert.AreEqual(new[] { "tx1" }, _store.Confirmed);
        }

        [Test]
        public void InvalidReceipt_IsClosedWithoutGrant()
        {
            _store.Validity = ReceiptValidity.Invalid;
            var iap = CreateReady();
            int grants = 0;
            iap.SetConsumableHandler(_ => { grants++; return true; });

            var result = Buy(iap, "gems", "tx1", "store.gems");

            Assert.AreEqual(PurchaseStatus.Failed, result.Status);
            Assert.AreEqual(0, grants);
            CollectionAssert.AreEqual(new[] { "tx1" }, _store.Confirmed);
            CollectionAssert.IsEmpty(_revenue.Purchases);
        }

        [Test]
        public void IndeterminateReceipt_IsNeitherGrantedNorConfirmed()
        {
            _store.Validity = ReceiptValidity.Indeterminate;
            var iap = CreateReady();

            var result = Buy(iap, "remove_ads", "tx1", "store.remove_ads");

            Assert.AreEqual(PurchaseStatus.Pending, result.Status);
            Assert.IsFalse(iap.IsOwned("remove_ads"));
            CollectionAssert.IsEmpty(_store.Confirmed);
        }

        [Test]
        public void CustomValidator_ReplacesTheStoreValidator()
        {
            _store.Validity = ReceiptValidity.Invalid;
            var iap = CreateReady();
            var validator = new FakeReceiptValidator { Result = ReceiptValidity.Valid };
            iap.SetReceiptValidator(validator);

            var result = Buy(iap, "remove_ads", "tx1", "store.remove_ads");

            Assert.IsTrue(result.IsSuccess);
            CollectionAssert.AreEqual(new[] { "tx1" }, validator.Validated);
        }

        [Test]
        public void ValidationDisabled_GrantsWithoutCallingTheValidator()
        {
            _store.Validity = ReceiptValidity.Invalid;
            var iap = Create(options: new IapOptions(new[] { RemoveAds }) { ValidateReceipts = false });
            _h.Run(iap.InitializeAsync(CancellationToken.None));

            Assert.IsTrue(Buy(iap, "remove_ads", "tx1", "store.remove_ads").IsSuccess);
        }

        [Test]
        public void TestStore_SkipsValidationAndRevenue()
        {
            _store.IsTestStore = true;
            _store.Validity = ReceiptValidity.Invalid;
            var iap = CreateReady();

            var result = Buy(iap, "remove_ads", "tx1", "store.remove_ads");

            Assert.IsTrue(result.IsSuccess);
            CollectionAssert.IsEmpty(_revenue.Purchases);
        }

        [Test]
        public void UserCancel_ReturnsCancelledAndReleasesSuppression()
        {
            var iap = CreateReady();

            var task = iap.PurchaseAsync("remove_ads", CancellationToken.None);
            _h.Main.Drain();
            _store.Fail("store.remove_ads", StoreFailure.UserCancelled);
            var result = _h.Run(task);

            Assert.AreEqual(PurchaseStatus.Cancelled, result.Status);
            Assert.AreEqual(0, _suppression.Active);
            Assert.IsFalse(iap.IsOwned("remove_ads"));
        }

        [Test]
        public void StoreFailure_ReturnsFailed()
        {
            var iap = CreateReady();

            var task = iap.PurchaseAsync("gems", CancellationToken.None);
            _h.Main.Drain();
            _store.Fail("store.gems", StoreFailure.PaymentDeclined);

            Assert.AreEqual(PurchaseStatus.Failed, _h.Run(task).Status);
        }

        [Test]
        public void DeferredPurchase_IsPendingThenGrantedWhenTheStoreDeliversIt()
        {
            var iap = CreateReady();
            var granted = new List<IapGrant>();
            iap.Purchased.Subscribe(granted.Add);

            var task = iap.PurchaseAsync("remove_ads", CancellationToken.None);
            _h.Main.Drain();
            _store.Defer("store.remove_ads");
            var result = _h.Run(task);

            Assert.AreEqual(PurchaseStatus.Pending, result.Status);
            Assert.IsFalse(iap.IsOwned("remove_ads"));

            _store.Deliver("tx1", "store.remove_ads");
            _h.Main.Drain();

            Assert.IsTrue(iap.IsOwned("remove_ads"));
            Assert.AreEqual(1, granted.Count);
        }

        [Test]
        public void SecondPurchaseWhileOneIsOpen_Fails()
        {
            var iap = CreateReady();
            var first = iap.PurchaseAsync("gems", CancellationToken.None);

            var second = _h.Run(iap.PurchaseAsync("remove_ads", CancellationToken.None));

            Assert.AreEqual(PurchaseStatus.Failed, second.Status);
            Assert.IsFalse(first.IsCompleted);
            Assert.AreEqual(1, _store.Purchases.Count);
        }

        [Test]
        public void UnknownProduct_Fails()
        {
            var iap = CreateReady();

            var result = _h.Run(iap.PurchaseAsync("nope", CancellationToken.None));

            Assert.AreEqual(PurchaseStatus.Failed, result.Status);
            CollectionAssert.IsEmpty(_store.Purchases);
        }

        [Test]
        public void ProductMissingFromTheStore_Fails()
        {
            _store.StoreProducts.Add(new StoreProduct("store.gems", false, string.Empty, 0m, string.Empty, "gems", string.Empty));
            var iap = CreateReady();

            var result = _h.Run(iap.PurchaseAsync("gems", CancellationToken.None));

            Assert.AreEqual(PurchaseStatus.Failed, result.Status);
            CollectionAssert.IsEmpty(_store.Purchases);
        }

        [Test]
        public void UnknownStoreProduct_IsLeftUnconfirmed()
        {
            CreateReady();

            _store.Deliver("tx1", "store.other");
            _h.Main.Drain();

            CollectionAssert.IsEmpty(_store.Confirmed);
        }

        [Test]
        public void JournalSurvivesRestart_OwnershipAndGrantedTransactions()
        {
            var first = CreateReady();
            first.SetConsumableHandler(_ => true);
            Buy(first, "remove_ads", "tx1", "store.remove_ads");
            Buy(first, "gems", "tx2", "store.gems");
            first.Dispose();

            _store = new FakeStoreAdapter();
            var second = Create();
            Assert.IsTrue(second.IsOwned("remove_ads"), "ownership is available before the store connects");
            Assert.IsTrue(second.IsActive(EntitlementId.RemoveAds));

            _h.Run(second.InitializeAsync(CancellationToken.None));
            int grants = 0;
            second.SetConsumableHandler(_ => { grants++; return true; });
            _store.Deliver("tx2", "store.gems");
            _h.Main.Drain();

            Assert.AreEqual(0, grants, "a transaction granted before the restart is only confirmed");
            CollectionAssert.AreEqual(new[] { "tx2" }, _store.Confirmed);
            CollectionAssert.AreEqual(new[] { "tx1", "tx2" }, _revenue.Purchases.ConvertAll(p => p.TransactionId));
        }

        [Test]
        public void Subscription_FollowsTheStoreOwnershipAndExpiry()
        {
            var iap = CreateReady();
            var changed = new List<EntitlementId>();
            iap.Changed.Subscribe(changed.Add);

            _store.Owned(new StoreOwnership("store.vip", _h.Clock.UtcNow.AddDays(30)));
            Assert.IsTrue(iap.IsOwned("vip"));
            Assert.IsTrue(iap.IsActive(VipEntitlement));

            _h.Clock.Advance(TimeSpan.FromDays(31));
            Assert.IsFalse(iap.IsOwned("vip"), "expired");

            _store.Owned(new StoreOwnership("store.vip", _h.Clock.UtcNow.AddDays(30)));
            Assert.IsTrue(iap.IsOwned("vip"));
            _store.Owned();
            Assert.IsFalse(iap.IsOwned("vip"), "no longer returned by the store");
            // Bật, bật lại sau khi hết hạn, tắt khi store không còn trả về.
            CollectionAssert.AreEqual(new[] { VipEntitlement, VipEntitlement, VipEntitlement }, changed);
        }

        [Test]
        public void Subscription_PendingConfirm_StaysOwnedOnlyIfAlreadyGranted()
        {
            var iap = CreateReady();
            _store.Deliver("tx-vip", "store.vip");
            _h.Main.Drain();
            Assert.IsTrue(iap.IsOwned("vip"));

            // Lần mở sau: store vẫn coi giao dịch là pending (chưa acknowledge xong) -> vẫn sở hữu.
            _store.Owned(new StoreOwnership("store.vip", _h.Clock.UtcNow.AddDays(30), "tx-vip"));
            Assert.IsTrue(iap.IsOwned("vip"));

            // Pending chưa từng được SDK trao (chưa kiểm tra receipt) -> chưa tính.
            _store.Owned(new StoreOwnership("store.remove_ads", null, "tx-unknown"));
            Assert.IsFalse(iap.IsOwned("remove_ads"));
        }

        [Test]
        public void OwnershipFetch_RestoresNonConsumables()
        {
            var iap = CreateReady();

            _store.Owned(new StoreOwnership("store.remove_ads"), new StoreOwnership("store.gems"));

            Assert.IsTrue(iap.IsOwned("remove_ads"));
            Assert.IsFalse(iap.IsOwned("gems"));
        }

        [Test]
        public void InitFailure_ReturnsNotReadyAndRetriesOnTheNextPurchase()
        {
            _store.InitResult = () => Task.FromResult<SdkResult>(new SdkError("x", SdkErrorCategory.Network, "offline", true));
            var iap = Create();

            var init = _h.Run(iap.InitializeAsync(CancellationToken.None));
            Assert.IsFalse(init.IsSuccess);
            Assert.AreEqual(IapState.Unavailable, iap.State.Value);

            var result = _h.Run(iap.PurchaseAsync("remove_ads", CancellationToken.None));
            Assert.AreEqual(PurchaseStatus.NotReady, result.Status);
            Assert.AreEqual(2, _store.InitCalls);
            Assert.AreEqual(0, _suppression.Active);

            _store.InitResult = null;
            var task = iap.PurchaseAsync("remove_ads", CancellationToken.None);
            _h.Main.Drain();
            Assert.AreEqual(IapState.Ready, iap.State.Value);
            Assert.AreEqual(3, _store.InitCalls);
            _store.Deliver("tx1", "store.remove_ads");
            Assert.IsTrue(_h.Run(task).IsSuccess);
        }

        [Test]
        public void InitTimeout_BecomesReadyWhenTheStoreConnectsLater()
        {
            var connect = new TaskCompletionSource<SdkResult>();
            _store.InitResult = () => connect.Task;
            var iap = Create();

            var init = iap.InitializeAsync(CancellationToken.None);
            _h.Main.Drain();
            _h.Scheduler.Advance(new IapOptions(Array.Empty<IapProductDefinition>()).InitTimeout);
            Assert.AreEqual(SdkErrorCategory.Timeout, _h.Run(init).Error!.Category);
            Assert.AreEqual(IapState.Unavailable, iap.State.Value);

            connect.SetResult(SdkResult.Ok);
            _h.Main.Drain();

            Assert.AreEqual(IapState.Ready, iap.State.Value);
            Assert.AreEqual(1, _store.InitCalls);
        }

        [Test]
        public void NoStoreAdapter_IsUnavailableButKeepsSavedOwnership()
        {
            var first = CreateReady();
            Buy(first, "remove_ads", "tx1", "store.remove_ads");
            first.Dispose();

            var iap = new IapService(null, new IapOptions(new[] { RemoveAds }), _h.Context);
            _services.Add(iap);

            Assert.IsFalse(_h.Run(iap.InitializeAsync(CancellationToken.None)).IsSuccess);
            Assert.AreEqual(IapState.Unavailable, iap.State.Value);
            Assert.IsTrue(iap.IsActive(EntitlementId.RemoveAds));
        }

        [Test]
        public void Restore_DelegatesToTheStoreAndSuppressesAppOpen()
        {
            var iap = CreateReady();
            var restore = new TaskCompletionSource<SdkResult>();
            _store.RestoreResult = restore.Task;

            var task = iap.RestoreAsync(CancellationToken.None);
            _h.Main.Drain();
            Assert.AreEqual(1, _suppression.Active);
            _store.Owned(new StoreOwnership("store.remove_ads"));
            restore.SetResult(SdkResult.Ok);

            Assert.IsTrue(_h.Run(task).IsSuccess);
            Assert.AreEqual(1, _store.RestoreCalls);
            Assert.AreEqual(0, _suppression.Active);
            Assert.IsTrue(iap.IsOwned("remove_ads"));
        }

        [Test]
        public void Products_UseStorePrices()
        {
            _store.StoreProducts.Add(new StoreProduct("store.gems", true, "22.000 ₫", 22000m, "VND", "Gems", "100 gems"));
            var iap = CreateReady();

            var gems = iap.GetProduct("gems")!;

            Assert.AreEqual("22.000 ₫", gems.PriceText);
            Assert.AreEqual("VND", gems.Currency);
            Assert.IsTrue(gems.IsAvailable);
            Assert.IsFalse(iap.GetProduct("remove_ads")!.IsAvailable, "not returned by the store");
            Assert.IsNull(iap.GetProduct("nope"));
            Assert.AreEqual(3, iap.Products.Count);
        }

        [Test]
        public void Dispose_FinishesTheOpenPurchase()
        {
            var iap = CreateReady();
            var task = iap.PurchaseAsync("remove_ads", CancellationToken.None);
            _h.Main.Drain();

            iap.Dispose();

            Assert.AreEqual(PurchaseStatus.NotReady, _h.Run(task).Status);
            Assert.IsTrue(_store.Disposed);
            Assert.AreEqual(0, _suppression.Active);
        }

        [Test]
        public void CancellingTheWait_ReturnsCancelledButStillGrantsALatePayment()
        {
            var iap = CreateReady();
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var granted = new List<IapGrant>();
            iap.Purchased.Subscribe(granted.Add);

            var cancelled = _h.Run(iap.PurchaseAsync("remove_ads", cts.Token));
            Assert.AreEqual(PurchaseStatus.Cancelled, cancelled.Status);

            _store.Deliver("tx1", "store.remove_ads");
            _h.Main.Drain();
            Assert.AreEqual(1, granted.Count);
        }

        [Test]
        public void TestStoreAdapter_CompletesPurchasesWithoutAStore()
        {
            var iap = Create(new TestStoreAdapter(_h.Main));
            _h.Run(iap.InitializeAsync(CancellationToken.None));
            iap.SetConsumableHandler(_ => true);

            var result = _h.Run(iap.PurchaseAsync("gems", CancellationToken.None));

            Assert.IsTrue(result.IsSuccess);
            Assert.AreEqual("$0.99", iap.GetProduct("gems")!.PriceText);
            CollectionAssert.IsEmpty(_revenue.Purchases);
        }
    }
}
