#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NovaGames.Mobile.Bootstrap;
using NovaGames.Mobile.Infrastructure;
using UnityEngine;
using UnityEngine.Purchasing;
using UnityEngine.Purchasing.Security;

namespace NovaGames.Mobile.Iap
{
    /// <summary>
    /// Store adapter cho Unity IAP 5 (Google Play, App Store). Chỉ chuyển API StoreController sang SPI của SDK;
    /// trao thưởng/journal/revenue do IapService xử lý. Receipt Google Play được kiểm tra bằng license key đã obfuscate
    /// (<c>GooglePlayLicense</c>, đăng ký từ Assets của game); iOS dùng StoreKit 2 nên receipt đã được kiểm tra trên máy.
    /// </summary>
    public sealed class UnityIapStoreAdapter : IStoreAdapter, IReceiptValidator
    {
        const string Op = "iap.unity";

        readonly ModuleContext _ctx;
        readonly ISdkLogger _log;
        readonly List<StoreProduct> _products = new List<StoreProduct>();
        readonly Dictionary<string, IapProductType> _types = new Dictionary<string, IapProductType>(StringComparer.Ordinal);
        readonly Dictionary<string, PendingOrder> _pending = new Dictionary<string, PendingOrder>(StringComparer.Ordinal);

        StoreController? _store;
        IStoreListener? _listener;
        Task<SdkResult>? _init;
        TaskCompletionSource<SdkResult>? _productsFetch;
        TaskCompletionSource<SdkResult>? _purchasesFetch;
        CrossPlatformValidator? _googleValidator;
        bool _googleValidatorCreated;
        bool _disposed;

        public UnityIapStoreAdapter(ModuleContext ctx)
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _log = ctx.Logs.Create("iap.unity");
        }

        public string Id => StoreAdapterIds.UnityIap;
        public bool IsTestStore => false;
        public IReadOnlyList<StoreProduct> Products => _products;

        public Task<SdkResult> InitializeAsync(IReadOnlyList<IapProductDefinition> products, IStoreListener listener, CancellationToken ct)
        {
            _listener = listener ?? throw new ArgumentNullException(nameof(listener));
            if (_init != null && !(_init.IsCompleted && !_init.Result.IsSuccess)) return _init;
            _init = InitializeCoreAsync(products);
            return _init;
        }

        async Task<SdkResult> InitializeCoreAsync(IReadOnlyList<IapProductDefinition> products)
        {
            try
            {
                if (_store is null)
                {
                    _store = UnityIAPServices.StoreController();
                    Subscribe(_store);
                }

                await _store.Connect();
                if (_disposed) return SdkError.Disposed(Op);

                var definitions = new List<ProductDefinition>(products.Count);
                _types.Clear();
                foreach (var product in products)
                {
                    _types[product.StoreId] = product.Type;
                    definitions.Add(new ProductDefinition(product.StoreId, ToUnity(product.Type)));
                }

                var fetch = new TaskCompletionSource<SdkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _productsFetch = fetch;
                _store.FetchProducts(definitions);
                var result = await fetch.Task;
                if (result.IsSuccess && !_disposed)
                {
                    // Giao dịch pending (chưa confirm) được store gửi qua OnPurchasePending; đã confirm qua OnPurchasesFetched.
                    _store.FetchPurchases();
                }
                return result;
            }
            catch (Exception e)
            {
                return SdkError.FromException(Op + ".initialize", e, Id);
            }
        }

        public void Purchase(string storeId)
        {
            var store = _store ?? throw new InvalidOperationException("Unity IAP is not initialized");
            var product = store.GetProductById(storeId);
            if (product is null)
            {
                OnMain(() => _listener?.OnPurchaseFailed(storeId, StoreFailure.ProductUnavailable, "Product was not returned by the store"));
                return;
            }
            store.PurchaseProduct(product);
        }

        public void Confirm(string transactionId)
        {
            if (_store is null || transactionId is null) return;
            if (_pending.TryGetValue(transactionId, out var order))
            {
                _pending.Remove(transactionId);
                _store.ConfirmPurchase(order);
            }
            else
            {
                _log.Warning("No pending order for transaction " + transactionId + " to confirm");
            }
        }

        public async Task<SdkResult> RestoreAsync(CancellationToken ct)
        {
            var store = _store;
            if (store is null) return SdkError.NotInitialized(Op + ".restore");

            var fetch = new TaskCompletionSource<SdkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _purchasesFetch = fetch;
            if (Application.platform == RuntimePlatform.IPhonePlayer || Application.platform == RuntimePlatform.OSXPlayer)
            {
                // App Store gửi lại giao dịch đã mua; sau đó lấy danh sách sở hữu mới.
                store.RestoreTransactions((success, error) => OnMain(() =>
                {
                    if (success) store.FetchPurchases();
                    else fetch.TrySetResult(new SdkError(Op + ".restore", SdkErrorCategory.Provider, "Restore failed: " + error, true, Id));
                }));
            }
            else
            {
                store.FetchPurchases();
            }
            return await fetch.Task;
        }

        // ---------------- Receipt validation ----------------

        public Task<ReceiptValidity> ValidateAsync(StoreTransaction transaction, CancellationToken ct)
        {
            // iOS (StoreKit 2) đã kiểm tra chữ ký trên máy; Editor dùng fake store của Unity.
            if (Application.platform != RuntimePlatform.Android) return Task.FromResult(ReceiptValidity.Valid);

            var validator = GoogleValidator();
            if (validator is null) return Task.FromResult(ReceiptValidity.Valid);
            try
            {
                validator.Validate(transaction.Receipt);
                return Task.FromResult(ReceiptValidity.Valid);
            }
            catch (IAPSecurityException e)
            {
                // Unity IAP gói mọi lỗi (key sai/của game khác, receipt lạ, lỗi parse) thành IAPSecurityException nên không
                // phân biệt được receipt giả với lỗi cấu hình. Không trao và KHÔNG confirm: receipt giả không mang lại gì,
                // còn giao dịch thật bị lỗi cấu hình sẽ được Google tự hoàn tiền sau 3 ngày thay vì người chơi mất tiền.
                _log.Error("Google Play receipt could not be verified (" + e.GetType().Name + "): purchase not granted and " +
                           "left unconfirmed. Check the license key in NovaGames > IAP > Google Play License Key", e);
                return Task.FromResult(ReceiptValidity.Indeterminate);
            }
            catch (Exception e)
            {
                _log.Error("Google Play receipt validation threw", e);
                return Task.FromResult(ReceiptValidity.Indeterminate);
            }
        }

        CrossPlatformValidator? GoogleValidator()
        {
            if (_googleValidatorCreated) return _googleValidator;
            _googleValidatorCreated = true;
            if (!GooglePlayLicense.TryGet(out var data, out var order, out var salt))
            {
                _log.Error("Google Play license key is not set (NovaGames > IAP > Google Play License Key): receipts are NOT validated");
                return null;
            }
            try
            {
                _googleValidator = new CrossPlatformValidator(Obfuscator.DeObfuscate(data, order, salt), Application.identifier);
            }
            catch (Exception e)
            {
                _log.Error("Google Play license key is invalid: receipts are NOT validated", e);
            }
            return _googleValidator;
        }

        // ---------------- StoreController events ----------------

        void Subscribe(StoreController store)
        {
            store.OnStoreDisconnected += OnStoreDisconnected;
            store.OnProductsFetched += OnProductsFetched;
            store.OnProductsFetchFailed += OnProductsFetchFailed;
            store.OnPurchasesFetched += OnPurchasesFetched;
            store.OnPurchasesFetchFailed += OnPurchasesFetchFailed;
            store.OnPurchasePending += OnPurchasePending;
            store.OnPurchaseConfirmed += OnPurchaseConfirmed;
            store.OnPurchaseFailed += OnPurchaseFailed;
            store.OnPurchaseDeferred += OnPurchaseDeferred;
        }

        void Unsubscribe(StoreController store)
        {
            store.OnStoreDisconnected -= OnStoreDisconnected;
            store.OnProductsFetched -= OnProductsFetched;
            store.OnProductsFetchFailed -= OnProductsFetchFailed;
            store.OnPurchasesFetched -= OnPurchasesFetched;
            store.OnPurchasesFetchFailed -= OnPurchasesFetchFailed;
            store.OnPurchasePending -= OnPurchasePending;
            store.OnPurchaseConfirmed -= OnPurchaseConfirmed;
            store.OnPurchaseFailed -= OnPurchaseFailed;
            store.OnPurchaseDeferred -= OnPurchaseDeferred;
        }

        void OnStoreDisconnected(StoreConnectionFailureDescription failure) => OnMain(() =>
        {
            _log.Warning("Store disconnected: " + failure?.Message);
            _productsFetch?.TrySetResult(new SdkError(Op + ".disconnected", SdkErrorCategory.Unavailable,
                "Store disconnected: " + failure?.Message, true, Id));
        });

        void OnProductsFetched(List<Product> fetched) => OnMain(() =>
        {
            RefreshProducts();
            _productsFetch?.TrySetResult(SdkResult.Ok);
        });

        void OnProductsFetchFailed(ProductFetchFailed failure) => OnMain(() =>
        {
            int failed = failure?.FailedFetchProducts?.Count ?? 0;
            _log.Warning("Could not fetch " + failed + " products: " + failure?.FailureReason);
            RefreshProducts();
            // Một phần sản phẩm lỗi (sai id) vẫn cho phép mua các sản phẩm còn lại.
            if (_products.Count > 0) _productsFetch?.TrySetResult(SdkResult.Ok);
            else _productsFetch?.TrySetResult(new SdkError(Op + ".products", SdkErrorCategory.Unavailable,
                "No products fetched: " + failure?.FailureReason, true, Id));
        });

        void OnPurchasesFetched(Orders orders) => OnMain(() =>
        {
            var owned = new List<StoreOwnership>();
            var store = _store;
            if (store != null)
            {
                foreach (var order in store.GetPurchases())
                {
                    // Pending = đã thanh toán nhưng chưa confirm xong (vd. lần trước app tắt giữa chừng): vẫn đang sở hữu.
                    if (!(order is ConfirmedOrder || order is PendingOrder)) continue;
                    var storeId = StoreIdOf(order);
                    if (storeId is null || !_types.TryGetValue(storeId, out var type) || type == IapProductType.Consumable) continue;
                    var pendingId = order is PendingOrder ? order.Info?.TransactionID : null;
                    owned.Add(new StoreOwnership(storeId, type == IapProductType.Subscription ? ExpiryOf(order) : null, pendingId));
                }
            }
            _listener?.OnOwnershipFetched(owned);
            _purchasesFetch?.TrySetResult(SdkResult.Ok);
        });

        void OnPurchasesFetchFailed(PurchasesFetchFailureDescription failure) => OnMain(() =>
        {
            _log.Warning("Could not fetch purchases: " + failure?.Message);
            _purchasesFetch?.TrySetResult(new SdkError(Op + ".purchases", SdkErrorCategory.Provider,
                "Could not fetch purchases: " + failure?.Message, true, Id));
        });

        void OnPurchasePending(PendingOrder order) => OnMain(() =>
        {
            var transactionId = order?.Info?.TransactionID;
            var storeId = StoreIdOf(order);
            if (order is null || string.IsNullOrEmpty(transactionId) || storeId is null)
            {
                _log.Error("Pending order without transaction id or product");
                return;
            }
            _pending[transactionId!] = order;
            _listener?.OnPurchasePending(new StoreTransaction(transactionId!, storeId, order.Info.Receipt ?? string.Empty));
        });

        void OnPurchaseConfirmed(Order order) => OnMain(() =>
        {
            if (order is ConfirmedOrder) _log.Debug("Confirmed " + StoreIdOf(order));
            else _log.Warning("Confirming the purchase of " + StoreIdOf(order) + " did not succeed: " + order?.GetType().Name);
        });

        void OnPurchaseFailed(FailedOrder order) => OnMain(() =>
            _listener?.OnPurchaseFailed(StoreIdOf(order) ?? string.Empty, ToFailure(order.FailureReason), order.Details ?? string.Empty));

        void OnPurchaseDeferred(DeferredOrder order) => OnMain(() =>
        {
            var storeId = StoreIdOf(order);
            if (storeId != null) _listener?.OnPurchaseDeferred(storeId);
        });

        // ---------------- Helpers ----------------

        void RefreshProducts()
        {
            _products.Clear();
            var store = _store;
            if (store is null) return;
            foreach (var product in store.GetProducts())
            {
                var id = product?.definition?.id;
                if (id is null) continue;
                var metadata = product!.metadata;
                _products.Add(new StoreProduct(id, product.availableToPurchase,
                    metadata?.localizedPriceString ?? string.Empty, metadata?.localizedPrice ?? 0m,
                    metadata?.isoCurrencyCode ?? string.Empty, metadata?.localizedTitle ?? id,
                    metadata?.localizedDescription ?? string.Empty));
            }
        }

        static string? StoreIdOf(Order? order)
        {
            var items = order?.CartOrdered?.Items();
            if (items is null || items.Count == 0) return null;
            return items[0]?.Product?.definition?.id;
        }

        static DateTime? ExpiryOf(Order order)
        {
            DateTime? latest = null;
            var infos = order.Info?.PurchasedProductInfo;
            if (infos is null) return null;
            foreach (var info in infos)
            {
                var subscription = info?.subscriptionInfo;
                if (subscription is null) continue;
                var expires = DateTime.SpecifyKind(subscription.GetExpireDate(), DateTimeKind.Utc);
                if (latest is null || expires > latest) latest = expires;
            }
            return latest;
        }

        static ProductType ToUnity(IapProductType type) => type switch
        {
            IapProductType.NonConsumable => ProductType.NonConsumable,
            IapProductType.Subscription => ProductType.Subscription,
            _ => ProductType.Consumable,
        };

        static StoreFailure ToFailure(PurchaseFailureReason reason) => reason switch
        {
            PurchaseFailureReason.UserCancelled => StoreFailure.UserCancelled,
            PurchaseFailureReason.ProductUnavailable => StoreFailure.ProductUnavailable,
            PurchaseFailureReason.PurchasingUnavailable => StoreFailure.PurchasingUnavailable,
            PurchaseFailureReason.NotSupported => StoreFailure.PurchasingUnavailable,
            PurchaseFailureReason.UserNotAuthenticated => StoreFailure.PurchasingUnavailable,
            PurchaseFailureReason.ExistingPurchasePending => StoreFailure.PurchaseInProgress,
            PurchaseFailureReason.DuplicateTransaction => StoreFailure.PurchaseInProgress,
            PurchaseFailureReason.PaymentDeclined => StoreFailure.PaymentDeclined,
            PurchaseFailureReason.StoreNotConnected => StoreFailure.Network,
            _ => StoreFailure.Unknown,
        };

        // Sự kiện Unity IAP thường đã ở main thread; nếu không thì chuyển về main thread.
        void OnMain(Action action)
        {
            if (_disposed) return;
            if (_ctx.Main.IsMainThread) _log.TryRun("Unity IAP callback", action);
            else _ctx.Main.Post(() => { if (!_disposed) _log.TryRun("Unity IAP callback", action); });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_store != null) Unsubscribe(_store);
            _productsFetch?.TrySetResult(SdkError.Disposed(Op));
            _purchasesFetch?.TrySetResult(SdkError.Disposed(Op));
            _pending.Clear();
            _listener = null;
        }
    }

    static class UnityIapRegistration
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Register() => AdapterRegistry.RegisterStore(StoreAdapterIds.UnityIap, ctx => new UnityIapStoreAdapter(ctx));
    }
}
